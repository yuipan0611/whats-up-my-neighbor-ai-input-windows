using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core.Storage;
using NekoChat.Core.Pairing;

namespace NekoChat.Core.V2;

public sealed record V2ListenerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int Port { get; init; } = DirectLanMessaging.DefaultPort;
    public int MaxConnections { get; init; } = 8;
    public int RequestsPerSourcePerMinute { get; init; } = 20;
    public int MaxSourceEntries { get; init; } = 1024;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan PairingWireTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    internal Action<TcpClient>? AcceptedForTest { get; init; }
}

/// <summary>Single-request HTTP/1.1 LAN transport. No URL ACL registration, proxy, TLS identity claim or firewall changes.</summary>
public sealed class V2TcpListener : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly V2Receiver? _receiver;
    private readonly IPairingEndpoint? _pairing;
    private readonly SemaphoreSlim _pairSlot = new(1, 1);
    private Task _pairWork = Task.CompletedTask, _pairDrain = Task.CompletedTask;
    public bool PairSlotOccupied => _pairSlot.CurrentCount == 0;
    public int ActivePairingJobs { get { lock (_gate) return _pairWork.IsCompleted ? 0 : 1; } }
    private readonly V2ListenerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _workers = new();
    private readonly SourceRateLimit _rate;
    private Task? _loop, _disposal;
    private int _running, _boundPort;
    private long _acceptFailures;
    public bool Running => Volatile.Read(ref _running) == 1;
    public int Port => Volatile.Read(ref _boundPort);
    public long AcceptFailures => Interlocked.Read(ref _acceptFailures);
    public int ActiveConnections { get { lock (_gate) return _workers.Count; } }
    public event Action<V2DeliveryNotice>? MessageStored;
    /// <summary>Legacy source is the TCP IPv4 address. No sender identity, persisted delivery or read fact is asserted.</summary>
    public event Action<string, DirectMessage>? LegacyMessageReceived;
    /// <summary>Optional local persistence before the legacy courtesy reply. The host owns and drains the borrowed store.</summary>
    public Func<string, DirectMessage, Func<bool>, bool>? LegacyMessageHandler { private get; init; }
    public event Action<string>? Diagnostic;
    public V2TcpListener(V2Receiver? receiver, V2ListenerOptions? options = null) : this(receiver, options, null) { }
    public V2TcpListener(V2Receiver? receiver, V2ListenerOptions? options, IPairingEndpoint? pairing)
    {
        _receiver = receiver; _pairing = pairing; _options = options ?? new();
        if (_options.BindAddress.AddressFamily != AddressFamily.InterNetwork ||
            !(_options.BindAddress.Equals(IPAddress.Any) || LanInvitation.PrivateIPv4(_options.BindAddress.ToString())) ||
            _options.Port is < 0 or > 65535 || _options.MaxConnections is < 1 or > 128 ||
            _options.RequestsPerSourcePerMinute is < 1 or > 10000 || _options.MaxSourceEntries is < 1 or > 65536 ||
            _options.ConnectionTimeout <= TimeSpan.Zero || _options.ConnectionTimeout > TimeSpan.FromMinutes(1) || _options.Clock is null ||
            _options.PairingWireTimeout <= TimeSpan.Zero || _options.PairingWireTimeout > TimeSpan.FromSeconds(2))
            throw new ArgumentException("Invalid LAN listener options.", nameof(options));
        _boundPort = _options.Port; _listener = new(_options.BindAddress, _options.Port); _rate = new(_options.Clock, _options.RequestsPerSourcePerMinute, _options.MaxSourceEntries);
    }
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (Running) return;
            _listener.Start(32); Volatile.Write(ref _boundPort, ((IPEndPoint)_listener.LocalEndpoint).Port);
            Volatile.Write(ref _running, 1); _receiver?.SetReady(true); _loop = AcceptAsync();
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
                    if (client.Client is not { RemoteEndPoint: IPEndPoint remote }) throw new SocketException((int)SocketError.NotConnected);
                    string source = remote.Address.ToString();
                    string? rejected = !LanInvitation.PrivateIPv4(source) ? "unpaired_sender" : !_rate.Allow(source) ? "rate_limited" : null;
                    lock (_gate)
                    {
                        if (rejected is null && _workers.Count >= _options.MaxConnections) rejected = "rate_limited";
                        if (rejected is null && !_lifetime.IsCancellationRequested)
                        {
                            var owned = client; var task = Task.Run(() => HandleAsync(owned, source)); _workers.Add(task); transferred = true;
                            _ = task.ContinueWith(done => {
                                lock (_gate) _workers.Remove(done);
                                if (done.IsFaulted) { _ = done.Exception; Report("request_failed"); }
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            continue;
                        }
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
                    var stream = client.GetStream();
                    await RespondAsync(stream, V2HttpResponse.Error(rejected ?? "receiver_not_ready"), timeout.Token).ConfigureAwait(false);
                    await FinishAsync(client, stream, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    if (_lifetime.IsCancellationRequested) return;
                    Interlocked.Increment(ref _acceptFailures); Report("accept_retry");
                    try { await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                }
                finally { if (!transferred) client?.Dispose(); }
            }
        }
        finally { Volatile.Write(ref _running, 0); _receiver?.SetReady(false); }
    }
    private async Task HandleAsync(TcpClient client, string source)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(_options.ConnectionTimeout);
            NetworkStream? stream = null; bool pairLease = false, requestLineKnown = false;
            Task<V2HttpResponse>? pairWork = null;
            long started = TimeProvider.System.GetTimestamp();
            try
            {
                stream = client.GetStream(); byte[] header = new byte[8192]; int count = 0, end = -1;
                while (end < 0 && count < header.Length)
                {
                    int read = await stream.ReadAsync(header.AsMemory(count), timeout.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    count += read;
                    if (!requestLineKnown)
                    {
                        int lineEnd = header.AsSpan(0, count).IndexOf("\r\n"u8);
                        if (lineEnd >= 0)
                        {
                            requestLineKnown = true;
                            if (header.AsSpan(0, lineEnd).SequenceEqual("POST /nekochat/v2/pair HTTP/1.1"u8))
                            {
                                if (_pairing is null) { await RespondAsync(stream, V2HttpResponse.Error("unsupported_version"), timeout.Token).ConfigureAwait(false); return; }
                                if (!_pairSlot.Wait(0)) { await RespondAsync(stream, PairingResponses.Error("pair_unavailable"), timeout.Token).ConfigureAwait(false); return; }
                                pairLease = true;
                                TimeSpan budget = _options.PairingWireTimeout < _options.ConnectionTimeout ? _options.PairingWireTimeout : _options.ConnectionTimeout;
                                TimeSpan remaining = budget - TimeProvider.System.GetElapsedTime(started);
                                if (remaining <= TimeSpan.Zero) return;
                                timeout.CancelAfter(remaining);
                            }
                        }
                    }
                    for (int i = Math.Max(0, count - read - 3); i <= count - 4; i++)
                        if (header[i] == 13 && header[i + 1] == 10 && header[i + 2] == 13 && header[i + 3] == 10) { end = i + 4; break; }
                }
                string path = "", code = "invalid_payload"; int length = 0;
                if (end < 0 || !TryHeaders(header.AsSpan(0, end), out path, out length, out code))
                { await RespondAsync(stream, V2HttpResponse.Error(end < 0 ? "invalid_payload" : code), timeout.Token).ConfigureAwait(false); return; }
                if (path != "/nekochat/v1/direct" && path != "/nekochat/v2/pair" && _receiver is null)
                { await RespondAsync(stream, V2HttpResponse.Error("receiver_not_ready"), timeout.Token).ConfigureAwait(false); return; }
                int limit = path == "/nekochat/v2/pair" ? PairingWire.MaxBodyBytes : path == "/nekochat/v1/direct" ? LegacyDirectProtocol.MaxBodyBytes : path == "/nekochat/v2/message" ? _receiver!.MaxMessageBodyBytes : V2JsonLimits.Control.MaxBytes;
                if (length > limit) { await RespondAsync(stream, path == "/nekochat/v2/pair" ? PairingResponses.Error("invalid_payload") : V2HttpResponse.Error("payload_too_large"), timeout.Token).ConfigureAwait(false); return; }
                int buffered = count - end;
                if (buffered > length) { await RespondAsync(stream, V2HttpResponse.Error("invalid_payload"), timeout.Token).ConfigureAwait(false); return; }
                byte[] body = new byte[length]; header.AsSpan(end, buffered).CopyTo(body);
                while (buffered < length)
                {
                    int read = await stream.ReadAsync(body.AsMemory(buffered), timeout.Token).ConfigureAwait(false);
                    if (read == 0) { await RespondAsync(stream, V2HttpResponse.Error("invalid_payload"), timeout.Token).ConfigureAwait(false); return; }
                    buffered += read;
                }
                V2HttpResponse response;
                if (path == "/nekochat/v2/pair")
                {
                    if (_pairing is null || !pairLease || timeout.IsCancellationRequested) return;
                    var pairToken = timeout.Token;
                    pairWork = Task.Run(() => _pairing.HandleAsync(body, source, pairToken));
                    lock (_gate) _pairWork = pairWork;
                    // A wire timeout closes this connection, never the physical class lease while storage still runs.
                    response = await pairWork.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                else if (path == "/nekochat/v1/direct")
                {
                    if (!LegacyDirectProtocol.TryParse(body, out var legacy, out var problem)) response = V2HttpResponse.Error(problem);
                    else if (timeout.IsCancellationRequested) response = V2HttpResponse.Error("receiver_not_ready");
                    else
                    {
                        try
                        {
                            if (LegacyMessageHandler is { } save && !save(source, legacy!, () => !timeout.IsCancellationRequested)) response = V2HttpResponse.Error("storage_unavailable");
                            else { LegacyMessageReceived?.Invoke(source, legacy!); response = new(200, "{\"status\":\"delivered\"}"u8.ToArray()); }
                        }
                        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException) { response = V2HttpResponse.Error("storage_unavailable"); }
                    }
                }
                else response = _receiver!.Handle(path, body, source, () => !timeout.IsCancellationRequested);
                if (response.Notice is { } notice)
                {
                    try { MessageStored?.Invoke(notice); } catch { Report("notification_failed"); }
                }
                await RespondAsync(stream, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            catch { Report("request_failed"); }
            finally
            {
                if (stream is not null) await FinishAsync(client, stream, _lifetime.Token).ConfigureAwait(false);
                if (pairLease)
                {
                    if (pairWork is null || pairWork.IsCompleted) _pairSlot.Release();
                    else
                    {
                        lock (_gate) _pairDrain = pairWork.ContinueWith(completed => { _ = completed.Exception; _pairSlot.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }
    }
    private static bool TryHeaders(ReadOnlySpan<byte> bytes, out string path, out int length, out string error)
    {
        path = ""; length = 0; error = "invalid_payload";
        foreach (byte b in bytes) if (b is not (9 or 10 or 13) && (b < 32 || b > 126)) return false;
        string[] lines = Encoding.ASCII.GetString(bytes[..^4]).Split("\r\n", StringSplitOptions.None);
        string[] request = lines[0].Split(' ', StringSplitOptions.None);
        if (request.Length != 3 || request[0] != "POST" || request[2] != "HTTP/1.1") return false;
        path = request[1];
        if (path is not ("/nekochat/v1/direct" or "/nekochat/v2/hello" or "/nekochat/v2/message" or "/nekochat/v2/receipt" or "/nekochat/v2/pair")) { error = "unsupported_version"; return false; }
        if (lines.Length is < 2 or > 65) return false;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':'); if (colon <= 0) return false;
            string name = lines[i][..colon], value = lines[i][(colon + 1)..].Trim(' ', '\t');
            if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                value.Any(c => c < 32 && c != '\t' || c == 127) || !headers.TryAdd(name, value)) return false;
        }
        if (!headers.TryGetValue("Host", out var host) || host.Length == 0 || headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Expect") ||
            !headers.TryGetValue("Content-Length", out var rawLength) || rawLength.Length > 10 || !rawLength.All(char.IsAsciiDigit) ||
            !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length <= 0 ||
            !headers.TryGetValue("Content-Type", out var media)) return false;
        string[] parts = media.Split(';', StringSplitOptions.TrimEntries);
        return parts[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length == 1 || parts.Length == 2 && (parts[1].Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("charset=\"utf-8\"", StringComparison.OrdinalIgnoreCase)));
    }
    internal static async Task RespondAsync(Stream stream, V2HttpResponse response, CancellationToken cancellation)
    {
        string reason = response.StatusCode switch { 200 => "OK", 403 => "Forbidden", 409 => "Conflict", 413 => "Content Too Large", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Bad Request" };
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.StatusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellation).ConfigureAwait(false); await stream.WriteAsync(response.Body, cancellation).ConfigureAwait(false); await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
    private static async Task FinishAsync(TcpClient client, NetworkStream stream, CancellationToken cancellation)
    {
        // Sending FIN first lets the peer receive the complete error even when the rejected request still has unread bytes.
        // Drain only a bounded amount/time; an endless attacker stream cannot hold the connection open.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMilliseconds(50));
        try
        {
            client.Client.Shutdown(SocketShutdown.Send); byte[] drain = new byte[8192]; int remaining = 65536;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(drain.AsMemory(0, Math.Min(drain.Length, remaining)), timeout.Token).ConfigureAwait(false);
                if (read == 0) break; remaining -= read;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    private void Report(string code) { try { Diagnostic?.Invoke(code); } catch { } }
    public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= StopAsync()); }
    private async Task StopAsync()
    {
        Task pairStop;
        try { pairStop = _pairing?.StopAsync() ?? Task.CompletedTask; } catch { pairStop = Task.CompletedTask; Report("pairing_stop_failed"); }
        _receiver?.SetReady(false); var cancel = _lifetime.CancelAsync(); _listener.Stop(); Volatile.Write(ref _running, 0);
        Exception? transportFailure = null;
        try
        {
            try { await Task.WhenAll(_loop ?? Task.CompletedTask, cancel).ConfigureAwait(false); }
            catch (Exception error) { transportFailure = error; }
            Task[] workers; lock (_gate) workers = _workers.ToArray();
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception error) { transportFailure ??= error; }
            finally { lock (_gate) _workers.Clear(); }
            // Pair work can be registered by a worker just before cancellation; snapshot only after all workers finished.
            Task pairWork, pairDrain; lock (_gate) { pairWork = _pairWork; pairDrain = _pairDrain; }
            try { await pairWork.ConfigureAwait(false); } catch { Report("pairing_request_failed"); }
            try { await pairDrain.ConfigureAwait(false); await pairStop.ConfigureAwait(false); }
            catch { Report("pairing_stop_failed"); }
        }
        finally { _pairSlot.Dispose(); _lifetime.Dispose(); }
        if (transportFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transportFailure).Throw();
    }
    internal sealed class SourceRateLimit(TimeProvider clock, int limit, int capacity)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Queue<long>> _sources = new(StringComparer.Ordinal);
        internal bool Allow(string source)
        {
            lock (_gate)
            {
                long now = clock.GetTimestamp();
                foreach (var entry in _sources)
                    while (entry.Value.TryPeek(out long first) && clock.GetElapsedTime(first, now) >= TimeSpan.FromMinutes(1)) entry.Value.Dequeue();
                foreach (string empty in _sources.Where(p => p.Value.Count == 0).Select(p => p.Key).ToArray()) _sources.Remove(empty);
                if (!_sources.TryGetValue(source, out var queue))
                {
                    if (_sources.Count >= capacity) return false;
                    _sources.Add(source, queue = new());
                }
                if (queue.Count >= limit) return false;
                queue.Enqueue(now); return true;
            }
        }
    }
}
