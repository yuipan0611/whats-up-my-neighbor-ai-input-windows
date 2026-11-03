using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

/// <summary>Dialog/window lifetime adapter. Owns the coordinator; borrows identity/store. No I/O from Current/View or token callbacks.</summary>
public sealed class PairingSessionHost : IPairingEndpoint, IAsyncDisposable
{
    private readonly PairingCoordinator _coordinator;
    private readonly object _gate = new();
    private PairingDialogSession? _current;
    private long _generation;
    private int _stopped;
    private Task? _disposal;
    public PairingSessionHost(PairingIdentity identity, TimeProvider? clock = null) => _coordinator = new(identity, clock);
    public PairingDialogSession OpenDialog(PairingRosterTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped != 0, this);
            var old = _current; EndCurrent("superseded");
            var drain = _coordinator.InvalidateAsync(); if (old is not null) old.Drain = drain;
            var session = new PairingDialogSession(this, target, ++_generation, drain); Volatile.Write(ref _current, session); return session;
        }
    }
    internal bool Current(PairingDialogSession session) => Volatile.Read(ref _stopped) == 0 && ReferenceEquals(Volatile.Read(ref _current), session) && session.Generation == Interlocked.Read(ref _generation) && session.Alive;
    internal PairingView View(PairingDialogSession session)
    {
        lock (_gate)
        {
            if (!Current(session)) return session.Last;
            if (!session.Started) return session.Last;
            var view = _coordinator.View with { RosterTarget = session.Target }; session.Last = view; return view;
        }
    }
    internal async Task<PairingStep> Begin(PairingDialogSession session, string? endpoint, int port, bool listen, CancellationToken cancellation)
    {
        await session.Preparation.ConfigureAwait(false); if (!Current(session)) return new("window_closed");
        if (listen)
        {
            var opening = _coordinator.OpenRosterWindowAsync(session.Target, () => Current(session), cancellation); session.Started = true;
            return new(await opening.ConfigureAwait(false) ? "listening" : "not_opened");
        }
        var beginning = _coordinator.BeginRosterAsync(session.Target, endpoint!, port, () => Current(session), cancellation); session.Started = true;
        return await beginning.ConfigureAwait(false);
    }
    internal Task<PairingStep> Confirm(PairingDialogSession session, PairingConsent consent, CancellationToken cancellation) => Current(session) && ReferenceEquals(consent.RosterTarget, session.Target) ? _coordinator.ConfirmAsync(consent, cancellation) : Task.FromResult(new PairingStep("stale_consent"));
    internal Task<bool> Reject(PairingDialogSession session, PairingConsent consent) => Current(session) ? _coordinator.RejectAsync(consent) : Task.FromResult(false);
    private void EndCurrent(string reason)
    {
        if (_current is not { } session) return;
        var previous = session.Started ? _coordinator.View : session.Last;
        session.Last = new(PairingStage.Closed, reason, MayBeAsymmetric: previous.MayBeAsymmetric, RosterTarget: session.Target);
        session.End(); Volatile.Write(ref _current, null); Interlocked.Increment(ref _generation);
    }
    internal Task Close(PairingDialogSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, session)) { EndCurrent("window_closed"); session.Drain = _coordinator.InvalidateAsync(); }
            session.End(); return session.Drain;
        }
    }
    public Task InvalidateAsync(string reason = "scope_changed")
    { lock (_gate) { var old = _current; EndCurrent(reason); var drain = _stopped == 0 ? _coordinator.InvalidateAsync() : _disposal ?? Task.CompletedTask; if (old is not null) old.Drain = drain; return drain; } }
    public Task StopAsync() => InvalidateAsync("receiver_stopped");
    public Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default) => _coordinator.HandleAsync(body, source, cancellation);
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            var old = _current; EndCurrent("stopped"); Volatile.Write(ref _stopped, 1); _disposal = _coordinator.DisposeAsync().AsTask(); if (old is not null) old.Drain = _disposal; return new(_disposal);
        }
    }
}
public sealed class PairingDialogSession : IAsyncDisposable
{
    private readonly PairingSessionHost _host;
    private int _alive = 1, _busy, _started;
    private readonly object _closeGate = new();
    internal bool Started { get => Volatile.Read(ref _started) != 0; set => Volatile.Write(ref _started, value ? 1 : 0); }
    internal Task Drain;
    private Task? _closing;
    internal long Generation { get; }
    internal Task Preparation { get; }
    internal bool Alive => Volatile.Read(ref _alive) != 0;
    internal PairingView Last;
    public PairingRosterTarget Target { get; }
    public PairingView View => _host.View(this);
    public bool Active => _host.Current(this);
    public bool Busy => Volatile.Read(ref _busy) != 0;
    internal PairingDialogSession(PairingSessionHost host, PairingRosterTarget target, long generation, Task preparation)
    { _host = host; Target = target; Generation = generation; Preparation = preparation; Drain = preparation; Last = new(PairingStage.Closed, "not_started", RosterTarget: target); }
    internal void End() => Volatile.Write(ref _alive, 0);
    public async Task<PairingStep> BeginAsync(string? endpoint, int port, bool listen, CancellationToken cancellation = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new("busy");
        try { return await _host.Begin(this, endpoint, port, listen, cancellation).ConfigureAwait(false); } finally { Volatile.Write(ref _busy, 0); }
    }
    public async Task<PairingStep> ConfirmAsync(PairingConsent consent, CancellationToken cancellation = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new("busy");
        try { return await _host.Confirm(this, consent, cancellation).ConfigureAwait(false); } finally { Volatile.Write(ref _busy, 0); }
    }
    public Task<bool> RejectAsync(PairingConsent consent) => _host.Reject(this, consent);
    public ValueTask DisposeAsync() { lock (_closeGate) return new(_closing ??= _host.Close(this)); }
}
