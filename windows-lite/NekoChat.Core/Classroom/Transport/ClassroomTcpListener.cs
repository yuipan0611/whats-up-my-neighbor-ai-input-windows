using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NekoChat.Core.Classroom.Transport;

public sealed record ClassroomListenerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int Port { get; init; } = DirectLanMessaging.DefaultPort;
    public int MaximumConnections { get; init; } = 8;
    public int ClassroomRequestsPerMinute { get; init; } = 120;
    public int MaximumSources { get; init; } = 1024;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    internal Action<TcpClient>? AcceptedForTest { get; init; }
}

public sealed class ClassroomConnectionContext
{
    internal ClassroomConnectionContext(string sourceIp, string localIp) { SourceIp = sourceIp; LocalIp = localIp; }
    public string SourceIp { get; }
    public string LocalIp { get; }
}
public sealed record ClassroomTransportResponse(int StatusCode, ReadOnlyMemory<byte> Body);

/// <summary>The host owns the endpoint/store. SetReady must be fast, clear RAM sessions, and never wait for storage.</summary>
public interface IClassroomRequestEndpoint
{
    void SetReady(bool ready);
    ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header);
    ClassroomTransportResponse Error(string code);
    Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection,
        Func<bool> stillCurrent, CancellationToken cancellation);
}

/// <summary>
/// Classroom stream/admission owner, independent of the signed V2 listener. All attribution context comes from
/// the accepted socket. A timed-out request keeps its physical slot until the endpoint's actual work returns.
/// The host must await disposal before disposing the borrowed endpoint or store.
/// </summary>
public sealed class ClassroomTcpListener : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IClassroomRequestEndpoint _endpoint;
    private readonly ClassroomListenerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Task, TcpClient> _workers = new();
    private readonly SourceQuota _classroom, _legacy;
    private Task? _loop, _disposal;
    private long _generation, _acceptFailures;
    private int _running, _port;
    public bool Running => Volatile.Read(ref _running) == 1;
    public int Port => Volatile.Read(ref _port);
    public long AcceptFailures => Interlocked.Read(ref _acceptFailures);
    public int ActiveConnections { get { lock (_gate) return _workers.Count; } }
    public event Action<string>? Diagnostic;
    public ClassroomTcpListener(IClassroomRequestEndpoint endpoint, ClassroomListenerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint); _endpoint = endpoint; _options = options ?? new();
        if (_options.BindAddress is null || _options.BindAddress.AddressFamily != AddressFamily.InterNetwork ||
            !(_options.BindAddress.Equals(IPAddress.Any) || LanInvitation.PrivateIPv4(_options.BindAddress.ToString())) ||
            _options.Port is < 0 or > 65535 || _options.MaximumConnections is < 1 or > 8 ||
            _options.ClassroomRequestsPerMinute is < 1 or > 10000 || _options.MaximumSources is < 1 or > 65536 ||
            _options.ConnectionTimeout <= TimeSpan.Zero || _options.ConnectionTimeout > TimeSpan.FromMinutes(1) || _options.Clock is null)
            throw new ArgumentException("Invalid classroom listener options.", nameof(options));
        _listener = new(_options.BindAddress, _options.Port); _port = _options.Port;
        _classroom = new(_options.Clock, _options.ClassroomRequestsPerMinute, _options.MaximumSources);
        _legacy = new(_options.Clock, 20, _options.MaximumSources);
    }
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this); if (Running) return;
            _listener.Start(32);
            try { _endpoint.SetReady(true); }
            catch { _listener.Stop(); throw; }
            Volatile.Write(ref _port, ((IPEndPoint)_listener.LocalEndpoint).Port);
            Volatile.Write(ref _running, 1); _loop = Task.Run(AcceptAsync);
        }
    }
    public void ResetAfterResume()
    {
        lock (_gate)
        {
            if (_disposal is not null || !Running) return;
            Interlocked.Increment(ref _generation); _endpoint.SetReady(false);
            foreach (var client in _workers.Values) client.Dispose();
            _endpoint.SetReady(true);
        }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient? client = null; bool transferred = false;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                    _options.AcceptedForTest?.Invoke(client);
                    if (client.Client is not { RemoteEndPoint: IPEndPoint remote, LocalEndPoint: IPEndPoint local })
                        throw new SocketException((int)SocketError.NotConnected);
                    string source = remote.Address.ToString();
                    string? reject = !LanInvitation.PrivateIPv4(source) ? "source_not_allowed" : null;
                    lock (_gate)
                    {
                        if (_workers.Count >= _options.MaximumConnections) reject = "rate_limited";
                        if (reject is null && Running && !_lifetime.IsCancellationRequested)
                        {
                            long generation = Volatile.Read(ref _generation);
                            var context = new ClassroomConnectionContext(source, local.Address.ToString());
                            var owned = client;
                            var work = Task.Run(() => HandleAsync(owned, context, generation)); _workers.Add(work, owned); transferred = true;
                            _ = work.ContinueWith(done => {
                                lock (_gate) _workers.Remove(done);
                                if (done.IsFaulted) { _ = done.Exception; Report("request_failed"); }
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            continue;
                        }
                    }
                    // Rejection stays inline and bounded; no extra rejection worker is spawned.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
                    var stream = client.GetStream();
                    await RespondAsync(stream, _endpoint.Error(reject ?? "receiver_not_ready"), timeout.Token).ConfigureAwait(false);
                    await FinishAsync(client, stream, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    if (_lifetime.IsCancellationRequested) return;
                    Interlocked.Increment(ref _acceptFailures); Report("accept_retry");
                    // Resource pressure must not turn into a hot accept/reject spin or stop healthy connections.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                }
                finally { if (!transferred) client?.Dispose(); }
            }
        }
        finally { Volatile.Write(ref _running, 0); SetNotReady(); }
    }
    private async Task HandleAsync(TcpClient client, ClassroomConnectionContext connection, long generation)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(_options.ConnectionTimeout);
            using var closeOnDeadline = timeout.Token.Register(static value => ((TcpClient)value!).Dispose(), client);
            bool Current() => Running && generation == Volatile.Read(ref _generation) && !timeout.IsCancellationRequested;
            NetworkStream? stream = null;
            try
            {
                stream = client.GetStream();
                var frame = await ClassroomStreamFraming.ReadRequestAsync(stream, bytes =>
                {
                    // Classification only selects a quota; the supplied codec still validates the entire head.
                    bool classroom = IsClassroomRequestLine(bytes.Span);
                    if (!(classroom ? _classroom : _legacy).Allow(connection.SourceIp)) return new("rate_limited");
                    return _endpoint.ValidateHead(bytes);
                }, timeout.Token).ConfigureAwait(false);
                ClassroomTransportResponse response;
                if (!frame.Success) response = _endpoint.Error(frame.Code);
                else if (!Current()) response = _endpoint.Error("receiver_not_ready");
                else
                {
                    // Never WaitAsync(timeout) around storage work: a closed socket must not release its physical slot.
                    response = await _endpoint.HandleAsync(frame.Frame!, connection, Current, timeout.Token).ConfigureAwait(false);
                }
                if (Current()) await RespondAsync(stream, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            catch { Report("request_failed"); }
            finally { if (stream is not null) await FinishAsync(client, stream, _lifetime.Token).ConfigureAwait(false); }
        }
    }
    private static bool IsClassroomRequestLine(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf("\r\n"u8); if (end < 0) return false;
        var line = bytes[..end];
        return line.SequenceEqual("POST /nekochat/classroom/v1/hello HTTP/1.1"u8) ||
            line.SequenceEqual("POST /nekochat/classroom/v1/message HTTP/1.1"u8) ||
            line.SequenceEqual("POST /nekochat/classroom/v1/receipt HTTP/1.1"u8);
    }
    private static async Task RespondAsync(Stream stream, ClassroomTransportResponse response, CancellationToken token)
    {
        if (response.Body.Length > 8192 || response.StatusCode is < 200 or > 599)
            throw new InvalidOperationException("Invalid classroom endpoint response.");
        string reason = response.StatusCode switch { 200 => "OK", 403 => "Forbidden", 409 => "Conflict", 413 => "Content Too Large", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Bad Request" };
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.StatusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(response.Body, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task FinishAsync(TcpClient client, NetworkStream stream, CancellationToken parent)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(parent); timeout.CancelAfter(TimeSpan.FromMilliseconds(50));
        try
        {
            if (client.Client is not { } socket) return;
            socket.Shutdown(SocketShutdown.Send); byte[] drain = new byte[8192]; int remaining = 65536;
            while (remaining > 0)
            {
                int n = await stream.ReadAsync(drain.AsMemory(0, Math.Min(drain.Length, remaining)), timeout.Token).ConfigureAwait(false);
                if (n == 0) break; remaining -= n;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    private void Report(string code) { try { Diagnostic?.Invoke(code); } catch { } }
    private void SetNotReady() { try { _endpoint.SetReady(false); } catch { Report("receiver_not_ready"); } }
    public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= StopAsync()); }
    private async Task StopAsync()
    {
        Interlocked.Increment(ref _generation); Volatile.Write(ref _running, 0); SetNotReady();
        var cancel = _lifetime.CancelAsync(); _listener.Stop();
        // Running=false and cancellation are published before the snapshot, so no new worker can register.
        Task[] workers; lock (_gate) workers = _workers.Keys.ToArray();
        try { await Task.WhenAll(workers.Append(_loop ?? Task.CompletedTask).Append(cancel)).ConfigureAwait(false); }
        finally { lock (_gate) _workers.Clear(); _lifetime.Dispose(); }
    }
    private sealed class SourceQuota(TimeProvider clock, int maximum, int capacity)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Queue<long>> _sources = new(StringComparer.Ordinal);
        internal bool Allow(string source)
        {
            lock (_gate)
            {
                long now = clock.GetTimestamp();
                foreach (var queue in _sources.Values)
                    while (queue.TryPeek(out long first) && clock.GetElapsedTime(first, now) >= TimeSpan.FromMinutes(1)) queue.Dequeue();
                foreach (var empty in _sources.Where(p => p.Value.Count == 0).Select(p => p.Key).ToArray()) _sources.Remove(empty);
                if (!_sources.TryGetValue(source, out var entries))
                { if (_sources.Count >= capacity) return false; _sources.Add(source, entries = new()); }
                if (entries.Count >= maximum) return false; entries.Enqueue(now); return true;
            }
        }
    }
}
