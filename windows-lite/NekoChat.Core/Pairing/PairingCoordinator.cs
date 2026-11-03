using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

public enum PairingSessionRole { None, Initiator, Switching, Responder }

/// <summary>Optional explicit P1 session owner. Owns actors/client, borrows identity/store. Peer claims decide roles only, never consent.</summary>
public sealed class PairingCoordinator : IPairingEndpoint, IAsyncDisposable
{
    private readonly PairingIdentity _identity;
    private readonly PairingInitiator _initiator;
    private readonly PairingResponder _responder;
    private readonly PairingLanClient _client;
    private readonly TimeProvider _clock;
    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _actions = new(1, 1), _inbound = new(1, 1);
    private Intent? _intent;
    private PairingView _terminal = new(PairingStage.Closed);
    private Task _closing = Task.CompletedTask;
    private Task? _disposal;
    private long _generation;
    private int _stopped;
    public PairingSessionRole Role => Volatile.Read(ref _intent)?.Role ?? PairingSessionRole.None;
    public PairingView View
    {
        get
        {
            var intent = Volatile.Read(ref _intent);
            if (intent is null) return Volatile.Read(ref _terminal);
            var view = intent.Role switch {
                PairingSessionRole.Initiator => _initiator.View,
                PairingSessionRole.Responder => _responder.View,
                _ => new PairingView(PairingStage.Ended, "superseded")
            };
            if (view.Stage == PairingStage.Paired) return view;
            if (!Current(intent)) return new(PairingStage.Ended, "scope_ended", MayBeAsymmetric: view.MayBeAsymmetric);
            TimeSpan left = PairingTiming.Window - _clock.GetElapsedTime(intent.Start);
            if (intent.Role == PairingSessionRole.Initiator && view is { Stage: PairingStage.Ended, Reason: "remote_ended" })
                return new(PairingStage.Listening, "awaiting_cross_request", PeerId: intent.ExpectedPeer, Remaining: left);
            return view with { Remaining = TimeSpan.FromTicks(Math.Max(0, Math.Min(view.Remaining.Ticks, left.Ticks))) };
        }
    }
    public PairingCoordinator(PairingIdentity identity, TimeProvider? clock = null) : this(identity, clock ?? TimeProvider.System, true) { }
    internal PairingCoordinator(PairingIdentity identity, TimeProvider clock, bool startTimers)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity)); _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _initiator = new(identity, clock, startTimers); _responder = new(identity, clock, startTimers); _client = new(_initiator);
    }
    /// <param name="scopeCurrent">Fast, thread-safe, no-I/O predicate, also called inside final SQLite guards.</param>
    public async Task<PairingStep> BeginAsync(string ip, int port, string? expectedPeerId, Func<bool> scopeCurrent, CancellationToken cancellation = default)
    {
        PairingLanClient.ValidateEndpoint(ip, port); Validate(expectedPeerId, scopeCurrent);
        if (!_actions.Wait(0)) return new("busy");
        try
        {
            Intent intent; Task preparation;
            lock (_lifecycle)
            {
                if (_stopped != 0) return new("stopped");
                preparation = CloseLocked("superseded");
                intent = new(Interlocked.Increment(ref _generation), _clock.GetTimestamp(), ip, expectedPeerId, scopeCurrent, cancellation, PairingSessionRole.Initiator);
                Volatile.Write(ref _intent, intent);
            }
            await preparation.ConfigureAwait(false);
            if (!Current(intent, PairingSessionRole.Initiator)) return new("role_changed");
            intent.OutgoingReady = true;
            var result = await _client.BeginAsync(ip, port, expectedPeerId, () => Current(intent, PairingSessionRole.Initiator), cancellation).ConfigureAwait(false);
            if (!Current(intent, PairingSessionRole.Initiator)) return new("role_changed");
            if (result.Code == "awaiting_user") intent.RevealedPeer = _initiator.View.PeerId;
            // A crossed request may arrive just after the smaller peer's403. Retain only that explicit bounded intent, no automatic outgoing retry.
            if (result.Code is not ("awaiting_user" or "remote_ended"))
            {
                lock (_lifecycle) if (ReferenceEquals(_intent, intent)) { intent.Ended = true; Volatile.Write(ref _terminal, _initiator.View); Volatile.Write(ref _intent, null); }
            }
            return result;
        }
        finally { _actions.Release(); }
    }
    public async Task<bool> OpenWindowAsync(string? expectedPeerId, Func<bool> scopeCurrent, CancellationToken cancellation = default)
    {
        Validate(expectedPeerId, scopeCurrent);
        if (!_actions.Wait(0)) return false;
        try
        {
            Intent intent; Task preparation;
            lock (_lifecycle)
            {
                if (_stopped != 0) return false;
                preparation = CloseLocked("superseded");
                intent = new(Interlocked.Increment(ref _generation), _clock.GetTimestamp(), null, expectedPeerId, scopeCurrent, cancellation, PairingSessionRole.Responder);
                Volatile.Write(ref _intent, intent);
            }
            await preparation.ConfigureAwait(false);
            if (!Current(intent, PairingSessionRole.Responder)) return false;
            return await _responder.OpenWindowAsync(expectedPeerId, intent.Start, () => Current(intent, PairingSessionRole.Responder)).ConfigureAwait(false);
        }
        finally { _actions.Release(); }
    }
    public async Task<PairingStep> ConfirmAsync(PairingConsent consent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(consent); var intent = Volatile.Read(ref _intent);
        if (intent is null || !Current(intent) || View.Consent != consent) return new("stale_consent");
        if (intent.Role == PairingSessionRole.Initiator && ReferenceEquals(consent.Owner, _initiator)) return await _client.ConfirmAsync(consent, cancellation).ConfigureAwait(false);
        if (intent.Role == PairingSessionRole.Responder && ReferenceEquals(consent.Owner, _responder))
        {
            if (!await _responder.ConfirmLocalAsync(consent, cancellation).ConfigureAwait(false)) return new("stale_consent");
            return new(_responder.View.Stage == PairingStage.Paired ? "paired" : "local_confirmed");
        }
        return new("stale_consent");
    }
    public Task<bool> RejectAsync(PairingConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (_lifecycle)
        {
            var intent = _intent;
            if (intent is null || !Current(intent) || View.Consent != consent) return Task.FromResult(false);
            if (Interlocked.Exchange(ref consent.Consumed, 1) != 0) return Task.FromResult(false);
            return FinishReject(CloseLocked("user_rejected"));
        }
    }
    private static async Task<bool> FinishReject(Task close) { await close.ConfigureAwait(false); return true; }
    public Task StopAsync() => CancelAsync();
    public Task CancelAsync() { lock (_lifecycle) return _stopped == 0 ? CloseLocked("user_cancelled") : _disposal ?? _closing; }
    public Task InvalidateAsync() { lock (_lifecycle) return _stopped == 0 ? CloseLocked("scope_changed") : _disposal ?? _closing; }
    private Task CloseLocked(string reason)
    {
        if (_intent is { } old) old.Ended = true;
        Volatile.Write(ref _intent, null); Interlocked.Increment(ref _generation); Volatile.Write(ref _terminal, new(PairingStage.Closed, reason));
        if (!_closing.IsCompleted) return _closing;
        // Both calls invalidate actor intent synchronously before returning their drain task. Their callbacks never acquire this coordinator lock.
        return _closing = Task.WhenAll(_client.CancelAsync(), _responder.CloseWindowAsync());
    }
    public async Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default)
    {
        if (Volatile.Read(ref _stopped) != 0) return PairingResponses.Error("receiver_not_ready");
        if (!_inbound.Wait(0)) return PairingResponses.Error("pair_unavailable");
        try
        {
            if (cancellation.IsCancellationRequested) return PairingResponses.Error("receiver_not_ready");
            if (source.Length <= 15 && LanInvitation.PrivateIPv4(source) && PairingWire.TryParse(body.Span, out var packet) && packet!.Kind == PairingKind.Request)
            {
                Intent? yielded = null; Task<PairingStep>? stop = null;
                string peer = V2Crypto.PeerId(Convert.FromBase64String(packet.PublicSpkiBase64!));
                lock (_lifecycle)
                {
                    var intent = _intent; string? knownPeer = intent?.ExpectedPeer ?? intent?.RevealedPeer;
                    if (intent is not null && intent.OutgoingReady && Current(intent, PairingSessionRole.Initiator) && intent.Ip == source && peer != _identity.PeerId &&
                        (knownPeer is null || knownPeer == peer))
                    {
                        if (string.CompareOrdinal(_identity.PeerId, peer) < 0) return PairingResponses.Error("pair_unavailable");
                        intent.Role = PairingSessionRole.Switching; yielded = intent;
                        stop = _client.YieldAsync();
                    }
                }
                if (yielded is not null)
                {
                    await stop!.ConfigureAwait(false); // Actual old I work drains, not just its socket. No opposite-HTTP wait cycle.
                    lock (_lifecycle)
                    {
                        if (!Current(yielded, PairingSessionRole.Switching)) return PairingResponses.Error("pair_cancelled");
                        yielded.Role = PairingSessionRole.Responder;
                    }
                    if (!await _responder.OpenWindowAsync(peer, yielded.Start, () => Current(yielded, PairingSessionRole.Responder)).ConfigureAwait(false)) return PairingResponses.Error("pair_cancelled");
                }
            }
            return await _responder.HandleAsync(body, source, cancellation).ConfigureAwait(false);
        }
        finally { _inbound.Release(); }
    }
    private bool Current(Intent intent, PairingSessionRole? role = null)
    {
        try { return Volatile.Read(ref _stopped) == 0 && ReferenceEquals(Volatile.Read(ref _intent), intent) && intent.Generation == Interlocked.Read(ref _generation) &&
            !intent.Ended && !intent.Cancellation.IsCancellationRequested && (role is null || intent.Role == role) && _clock.GetElapsedTime(intent.Start) < PairingTiming.Window && intent.ScopeCurrent(); }
        catch { return false; }
    }
    private static void Validate(string? peer, Func<bool> scope)
    { ArgumentNullException.ThrowIfNull(scope); if (peer is not null && !V2Format.IsPeerId(peer)) throw new ArgumentException("Invalid expected peer."); }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            Volatile.Write(ref _stopped, 1); var closing = CloseLocked("stopped");
            return new(_disposal = Task.Run(async () => {
                await closing.ConfigureAwait(false);
                await _actions.WaitAsync().ConfigureAwait(false); await _inbound.WaitAsync().ConfigureAwait(false);
                try { await _client.DisposeAsync().ConfigureAwait(false); await _initiator.DisposeAsync().ConfigureAwait(false); await _responder.DisposeAsync().ConfigureAwait(false); }
                finally { _inbound.Release(); _actions.Release(); }
            }));
        }
    }
    private sealed class Intent(long generation, long start, string? ip, string? expectedPeer, Func<bool> scopeCurrent, CancellationToken cancellation, PairingSessionRole role)
    {
        internal readonly long Generation = generation, Start = start;
        internal readonly string? Ip = ip, ExpectedPeer = expectedPeer;
        internal readonly Func<bool> ScopeCurrent = scopeCurrent;
        internal readonly CancellationToken Cancellation = cancellation;
        private int _role = (int)role, _ended, _outgoingReady;
        private string? _revealedPeer;
        internal bool OutgoingReady { get => Volatile.Read(ref _outgoingReady) != 0; set => Volatile.Write(ref _outgoingReady, value ? 1 : 0); }
        internal string? RevealedPeer { get => Volatile.Read(ref _revealedPeer); set => Volatile.Write(ref _revealedPeer, value); }
        internal PairingSessionRole Role { get => (PairingSessionRole)Volatile.Read(ref _role); set => Volatile.Write(ref _role, (int)value); }
        internal bool Ended { get => Volatile.Read(ref _ended) != 0; set => Volatile.Write(ref _ended, value ? 1 : 0); }
    }
}
