using System.Net;
using System.Net.Http.Headers;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

public sealed record PairingClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>Explicit P1 control transport. Borrows the actor, owns HTTP/session lifetime. No private messages or automatic fresh sessions.</summary>
public sealed class PairingLanClient : IAsyncDisposable
{
    private readonly PairingInitiator _actor;
    private readonly PairingClientOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Route? _route;
    private long _generation;
    private int _stopped;
    private Task? _disposal;
    private Task _yieldNotice = Task.CompletedTask;
    public PairingView View => _actor.View;
    public PairingLanClient(PairingInitiator actor, PairingClientOptions? options = null) : this(actor, options ?? new(), new SocketsHttpHandler()) { }
    internal PairingLanClient(PairingInitiator actor, PairingClientOptions options, SocketsHttpHandler handler)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor)); _options = options;
        if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > options.RequestTimeout || options.RequestTimeout > TimeSpan.FromSeconds(4)) throw new ArgumentException("Invalid pairing timeouts.");
        handler.AllowAutoRedirect = false; handler.UseProxy = false; handler.ConnectTimeout = options.ConnectTimeout; handler.MaxResponseHeadersLength = 8;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<PairingStep> BeginAsync(string ip, int port, string? expectedPeerId, Func<bool> scopeCurrent, CancellationToken cancellation = default)
    {
        ValidateEndpoint(ip, port); ArgumentNullException.ThrowIfNull(scopeCurrent);
        if (expectedPeerId is not null && !V2Format.IsPeerId(expectedPeerId)) throw new ArgumentException("Invalid expected peer.");
        if (Volatile.Read(ref _stopped) != 0) return new("stopped");
        if (!_io.Wait(0)) return new("busy");
        Route? route = null;
        long intent; lock (_lifecycle) intent = Interlocked.Increment(ref _generation);
        try
        {
            var old = Volatile.Read(ref _route);
            if (old is not null)
            {
                var cancel = await _actor.CancelAsync().ConfigureAwait(false); await Drop(old).ConfigureAwait(false);
                if (cancel.Packet is not null) await BestEffort(old.Ip, old.Port, cancel.Packet, cancellation).ConfigureAwait(false);
            }
            route = new(ip, port, intent, scopeCurrent, _shutdown.Token, cancellation);
            lock (_lifecycle)
            {
                if (Volatile.Read(ref _stopped) != 0 || intent != Interlocked.Read(ref _generation)) { route.Dispose(); return new("cancelled"); }
                Volatile.Write(ref _route, route);
            }
            var owned = route;
            route.Registration = route.Token.Register(() => {
                lock (_lifecycle)
                    if (Volatile.Read(ref _stopped) == 0 && ReferenceEquals(_route, owned) && owned.Generation == Interlocked.Read(ref _generation))
                        Volatile.Write(ref owned.CancelWork, _actor.AbortAsync(PairingFailure.ScopeChanged));
            });
            if (!Current(route)) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
            var request = await _actor.BeginAsync(expectedPeerId, () => Current(owned)).ConfigureAwait(false);
            if (!Current(route) || request.Packet is null) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
            var commitment = await Exchange(route, request.Packet).ConfigureAwait(false);
            if (commitment.Error is not null) return await Fail(route, Failure(commitment.Error)).ConfigureAwait(false);
            if (!Current(route)) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
            var nonce = await _actor.AdvanceAsync(commitment.Body, route.Token).ConfigureAwait(false);
            if (nonce.Code != "send_nonce" || nonce.Packet is null) return nonce;
            var reveal = await Exchange(route, nonce.Packet).ConfigureAwait(false);
            if (reveal.Error is not null) return await Fail(route, Failure(reveal.Error)).ConfigureAwait(false);
            if (!Current(route)) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
            return await _actor.AdvanceAsync(reveal.Body, route.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException)
        { return route is null ? new("cancelled") : await Fail(route, PairingFailure.Transport).ConfigureAwait(false); }
        finally { _io.Release(); }
    }
    public async Task<PairingStep> ConfirmAsync(PairingConsent consent, CancellationToken cancellation = default)
    {
        if (Volatile.Read(ref _stopped) != 0) return new("stopped");
        if (!_io.Wait(0)) return new("busy");
        var route = Volatile.Read(ref _route);
        if (route is null) { _io.Release(); return new("no_session"); }
        using var active = CancellationTokenSource.CreateLinkedTokenSource(route.Token, cancellation);
        try
        {
            if (!Current(route)) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
            var step = await _actor.ConfirmLocalAsync(consent, active.Token).ConfigureAwait(false);
            while (step.Packet is not null && Current(route) && !active.IsCancellationRequested)
            {
                Response response;
                try { response = await Exchange(route, step.Packet, active.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException)
                { if (active.IsCancellationRequested) throw; response = new([], "transport_error"); }
                if (!Current(route)) return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
                if (response.Error is null)
                {
                    var progress = await _actor.AdvanceAsync(response.Body, active.Token).ConfigureAwait(false);
                    if (progress.Code != "pending") return progress;
                }
                else if (response.Error is not ("transport_error" or "rate_limited" or "storage_unavailable" or "receiver_not_ready" or "pair_unavailable"))
                    return await Fail(route, Failure(response.Error)).ConfigureAwait(false);
                // Retries are only the already human-approved identical pair.confirm, never a new request/session or user message.
                await Task.Delay(PairingTiming.Poll, active.Token).ConfigureAwait(false);
                step = await _actor.PollAsync().ConfigureAwait(false);
                if (step.Packet is null) return step;
            }
            return Current(route) && !active.IsCancellationRequested ? step : await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException)
        { return await Fail(route, PairingFailure.ScopeChanged).ConfigureAwait(false); }
        finally { _io.Release(); }
    }
    public Task<PairingStep> CancelAsync() => CancelCore(false);
    internal Task<PairingStep> YieldAsync() => CancelCore(true);
    private async Task<PairingStep> CancelCore(bool superseded)
    {
        Task<PairingStep> stop; Route? route;
        lock (_lifecycle)
        {
            if (Volatile.Read(ref _stopped) != 0) return new("stopped");
            Interlocked.Increment(ref _generation); route = _route; stop = superseded ? _actor.AbortAsync(PairingFailure.Superseded) : _actor.CancelAsync();
        }
        route?.Cancel();
        var result = await stop.ConfigureAwait(false);
        await _io.WaitAsync().ConfigureAwait(false);
        bool releasedByNotice = false;
        try
        {
            if (Volatile.Read(ref _stopped) == 0 && route is not null && ReferenceEquals(Volatile.Read(ref _route), route))
            {
                if (superseded)
                {
                    // Old I has physically drained. R may accept now; the single outbound slot still owns this bounded notification.
                    lock (_lifecycle) _yieldNotice = Task.Run(async () => {
                        try { if (result.Packet is not null) await BestEffort(route.Ip, route.Port, result.Packet, _shutdown.Token).ConfigureAwait(false); await Drop(route).ConfigureAwait(false); }
                        finally { _io.Release(); }
                    });
                    releasedByNotice = true;
                }
                else
                {
                    if (result.Packet is not null) await BestEffort(route.Ip, route.Port, result.Packet, _shutdown.Token).ConfigureAwait(false);
                    await Drop(route).ConfigureAwait(false);
                }
            }
        }
        finally { if (!releasedByNotice) _io.Release(); }
        return result;
    }
    private bool Current(Route route)
    {
        try { return Volatile.Read(ref _stopped) == 0 && ReferenceEquals(Volatile.Read(ref _route), route) && route.Generation == Interlocked.Read(ref _generation) && !route.Token.IsCancellationRequested && route.ScopeCurrent(); }
        catch { return false; }
    }
    private async Task<PairingStep> Fail(Route route, PairingFailure failure)
    {
        Task<PairingStep>? cleanup = null;
        lock (_lifecycle)
            if (Volatile.Read(ref _stopped) == 0 && route.Generation == Interlocked.Read(ref _generation) && ReferenceEquals(_route, route)) cleanup = _actor.AbortAsync(failure);
        return cleanup is null ? new("stale_scope") : await cleanup.ConfigureAwait(false);
    }
    private static PairingFailure Failure(string error) => error switch {
        "pair_state" or "pair_cancelled" or "pair_unavailable" => PairingFailure.RemoteEnded,
        "invalid_response" => PairingFailure.InvalidResponse, _ => PairingFailure.Transport };
    private async Task Drop(Route route)
    {
        lock (_lifecycle) if (ReferenceEquals(_route, route)) Volatile.Write(ref _route, null);
        route.Cancel(); route.Registration.Dispose();
        if (Volatile.Read(ref route.CancelWork) is { } cleanup) await cleanup.ConfigureAwait(false);
        route.Dispose();
    }
    private async Task BestEffort(string ip, int port, byte[] packet, CancellationToken cancellation)
    {
        try { await ExchangeRaw(ip, port, packet, cancellation).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException) { }
    }
    private Task<Response> Exchange(Route route, byte[] packet, CancellationToken? active = null)
    {
        if (!Current(route)) throw new OperationCanceledException();
        return ExchangeRaw(route.Ip, route.Port, packet, active ?? route.Token);
    }
    private async Task<Response> ExchangeRaw(string ip, int port, byte[] packet, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:{port}/nekochat/v2/pair") {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(packet)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }; request.Headers.ConnectionClose = true;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        var content = response.Content.Headers;
        if (content.ContentLength is > PairingWire.MaxBodyBytes || content.ContentEncoding.Count != 0 || content.ContentType?.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) != true ||
            content.ContentType.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return new([], "invalid_response");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        byte[] bytes = new byte[PairingWire.MaxBodyBytes + 1]; int length = 0;
        while (length < bytes.Length) { int read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token).ConfigureAwait(false); if (read == 0) break; length += read; }
        if (length > PairingWire.MaxBodyBytes) return new([], "invalid_response");
        byte[] body = bytes[..length];
        if (response.StatusCode == HttpStatusCode.OK) return new(body);
        if (V2Json.TryParseObject(body, new(PairingWire.MaxBodyBytes, 4, 32), out var error, out _) && error!.HasExactKeys("protocol", "version", "kind", "code") &&
            error.TryGetString("protocol", out var protocol) && protocol == "nekochat" && error.TryGetNumberToken("version", out var version) && version == "2" &&
            error.TryGetString("kind", out var kind) && kind == "error" && error.TryGetString("code", out var code) && ErrorStatus.TryGetValue(code, out int status) && status == (int)response.StatusCode)
            return new(body, code);
        return new([], "invalid_response");
    }
    private static readonly Dictionary<string, int> ErrorStatus = new(StringComparer.Ordinal) {
        ["invalid_payload"] = 400, ["unsupported_version"] = 400, ["payload_too_large"] = 413, ["pair_unavailable"] = 403,
        ["pair_state"] = 409, ["pair_cancelled"] = 409, ["rate_limited"] = 429, ["storage_unavailable"] = 503, ["receiver_not_ready"] = 503
    };
    internal static void ValidateEndpoint(string ip, int port)
    { if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || parsed.ToString() != ip || !LanInvitation.PrivateIPv4(ip) || port is < 1 or > 65535) throw new ArgumentException("A canonical private IPv4 endpoint is required."); }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            Volatile.Write(ref _stopped, 1); Interlocked.Increment(ref _generation);
            var route = _route;
            // Cancellation runs outside the lifecycle lock: synchronous callbacks may need that lock.
            return new(_disposal = Task.Run(async () => {
                route?.Cancel(); _shutdown.Cancel();
                await _actor.AbortAsync(PairingFailure.ScopeChanged).ConfigureAwait(false);
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    Task notice; lock (_lifecycle) notice = _yieldNotice;
                    try { await notice.ConfigureAwait(false); }
                    finally { try { if (route is not null) await Drop(route).ConfigureAwait(false); } finally { _http.Dispose(); _shutdown.Dispose(); } }
                }
                finally { _io.Release(); }
            }));
        }
    }
    private sealed record Response(byte[] Body, string? Error = null);
    private sealed class Route : IDisposable
    {
        internal readonly string Ip; internal readonly int Port; internal readonly long Generation;
        internal readonly Func<bool> ScopeCurrent;
        private readonly CancellationTokenSource Life;
        private readonly object _gate = new();
        private bool _disposed;
        internal readonly CancellationToken Token;
        internal CancellationTokenRegistration Registration;
        internal Task<PairingStep>? CancelWork;
        internal Route(string ip, int port, long generation, Func<bool> current, CancellationToken lifetime, CancellationToken caller)
        { Ip = ip; Port = port; Generation = generation; ScopeCurrent = current; Life = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller); Life.CancelAfter(PairingTiming.Session); Token = Life.Token; }
        internal void Cancel() { lock (_gate) if (!_disposed) Life.Cancel(); }
        public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; Life.Dispose(); } }
    }
}
