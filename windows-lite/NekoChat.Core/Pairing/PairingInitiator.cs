using System.Security.Cryptography;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

/// <summary>Output for a transport driver. Packets are control frames, never user messages or proof of remote persistence.</summary>
public sealed record PairingStep(string Code, byte[]? Packet = null, byte[]? PriorCancellation = null);

/// <summary>Conditional P1 initiating actor. No sockets, provisioning or automatic new sessions. Borrows the identity/store.</summary>
public sealed class PairingInitiator : IAsyncDisposable
{
    private readonly PairingIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _timer;
    private PairingView _view = new(PairingStage.Closed);
    private Session? _session;
    private long _epoch, _cancelEpoch;
    private string _cancelReason = "user_cancelled";
    private int _stopped, _userPending;
    private Task<PairingStep>? _cancel;
    private Task? _disposal;
    public PairingView View => Volatile.Read(ref _view);
    public PairingInitiator(PairingIdentity identity, TimeProvider? clock = null) : this(identity, clock ?? TimeProvider.System, true) { }
    internal PairingInitiator(PairingIdentity identity, TimeProvider clock, bool startTimer)
    { _identity = identity ?? throw new ArgumentNullException(nameof(identity)); _clock = clock ?? throw new ArgumentNullException(nameof(clock)); _timer = startTimer ? Task.Run(Timer) : Task.CompletedTask; }
    public Task<PairingStep> BeginAsync(string? expectedPeerId = null) => BeginAsync(expectedPeerId, () => true);
    /// <param name="sessionCurrent">Fast thread-safe generation predicate, including inside SQLite commit guards. Must not perform I/O or acquire a database lock.</param>
    public Task<PairingStep> BeginAsync(string? expectedPeerId, Func<bool> sessionCurrent)
    {
        ArgumentNullException.ThrowIfNull(sessionCurrent);
        if (expectedPeerId is not null && !V2Format.IsPeerId(expectedPeerId)) throw new ArgumentException("Invalid expected peer.");
        if (Volatile.Read(ref _stopped) != 0) return Task.FromResult(new PairingStep("stopped"));
        if (!_serial.Wait(0)) return Task.FromResult(new PairingStep("busy"));
        long epoch = Interlocked.Increment(ref _epoch);
        return Work(() => {
            byte[]? cancel = _session is { } previous ? PairingWire.Cancel(previous.Id, "superseded") : null;
            End("superseded", invalidate: false);
            if (Volatile.Read(ref _stopped) != 0 || !_identity.IsCurrent()) return new("stale_security");
            string sid = PairingWire.NewNonce(); byte[] request = PairingWire.Request(sid, _identity.PublicSpkiBase64);
            // Do not emit a stale request after an identity read that waited behind SQL. Cancel and readiness publish share this short boundary.
            lock (_lifecycle)
            {
                if (epoch != Volatile.Read(ref _epoch) || Volatile.Read(ref _stopped) != 0 || !sessionCurrent()) return new("stale_security");
                _session = new(sid, expectedPeerId, epoch, _clock.GetTimestamp(), sessionCurrent);
                Publish(_session, PairingStage.Requested); return new("send_request", request, cancel);
            }
        });
    }
    public Task<PairingStep> AdvanceAsync(ReadOnlyMemory<byte> response, CancellationToken cancellation = default)
    {
        bool valid = PairingWire.TryParse(response.Span, out var packet);
        if (Volatile.Read(ref _stopped) != 0) return Task.FromResult(new PairingStep("stopped"));
        if (!_serial.Wait(0)) return Task.FromResult(new PairingStep("busy"));
        return Work(() => {
            Expire(); var session = _session;
            if (session is null) return new("finished");
            if (cancellation.IsCancellationRequested) { End("result_unconfirmed"); return new("result_unconfirmed"); }
            if (!valid || packet!.SessionId != session.Id) { End("invalid_response"); return new("invalid_response"); }
            if (View.Stage == PairingStage.Requested && packet.Kind == PairingKind.Commitment)
            {
                PairingWire.TryDecodeHash(packet.Commitment, out var commitment); session.Commitment = commitment;
                // The initiator nonce is generated only after receipt of the responder's immutable commitment.
                session.Na = PairingWire.NewNonce(); session.NoncePacket = PairingWire.Nonce(session.Id, session.Na);
                Publish(session, PairingStage.Committed); return new("send_nonce", session.NoncePacket.ToArray());
            }
            if (View.Stage == PairingStage.Committed && packet.Kind == PairingKind.Commitment)
            {
                if (PairingWire.TryDecodeHash(packet.Commitment, out var duplicate) && PairingCrypto.MatchesCommitment(session.Commitment!, duplicate)) return new("send_nonce", session.NoncePacket!.ToArray());
                End("commitment_mismatch"); return new("commitment_mismatch");
            }
            if (View.Stage == PairingStage.Committed && packet.Kind == PairingKind.Reveal)
            {
                byte[] peerKey = Convert.FromBase64String(packet.PublicSpkiBase64!); string peer = V2Crypto.PeerId(peerKey);
                if (peer == _identity.PeerId || session.ExpectedPeer is not null && session.ExpectedPeer != peer) { End("peer_mismatch"); return new("peer_mismatch"); }
                V2Format.TryDecodeNonce22(session.Id, out var sid); V2Format.TryDecodeNonce22(session.Na, out var na); V2Format.TryDecodeNonce22(packet.Nonce, out var nb);
                if (!PairingCrypto.MatchesCommitment(session.Commitment!, PairingCrypto.Commitment(sid, _identity.PublicKey, peerKey, nb)))
                { End("commitment_mismatch"); return new("commitment_mismatch"); }
                session.PublicKey = peerKey; session.Transcript = PairingCrypto.Transcript(sid, _identity.PublicKey, peerKey, na, nb);
                var candidate = _identity.Candidate(peerKey, () => Live(session) && !cancellation.IsCancellationRequested);
                if (!candidate.Ready) { End("stale_security"); return new("pair_unavailable"); }
                session.Expected = candidate.Current;
                if (!Live(session) || cancellation.IsCancellationRequested) { End("cancelled"); return new("cancelled"); }
                session.Consent = new(this, session.Epoch, session.Id, peer, PairingCrypto.Sas(session.Transcript));
                Publish(session, PairingStage.SasShown); return new("awaiting_user");
            }
            if (View.Stage == PairingStage.Waiting && session.LocalConfirmed && packet.Kind == PairingKind.Pending)
            { Publish(session, PairingStage.Waiting); return new("pending"); }
            if (View.Stage == PairingStage.Waiting && session.LocalConfirmed && packet.Kind == PairingKind.Confirmation &&
                PairingCrypto.Verify(session.PublicKey!, PairingRole.Responder, session.Transcript!, Convert.FromBase64String(packet.SignatureBase64!)))
            {
                session.FinalizationStarted = true;
                if (!_identity.Finalize(session.Expected!, () => Live(session) && !cancellation.IsCancellationRequested)) { End("stale_security"); return new("stale_security"); }
                _session = null; Interlocked.Increment(ref _epoch);
                Volatile.Write(ref _view, new(PairingStage.Paired, SessionId: session.Id, PeerId: session.Expected!.PeerId, Sas: session.Consent!.Sas, LocalConfirmed: true, RemoteConfirmed: true, MayBeAsymmetric: true));
                return new("paired");
            }
            End("invalid_response"); return new("invalid_response");
        });
    }
    public async Task<PairingStep> ConfirmLocalAsync(PairingConsent consent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!ReferenceEquals(consent.Owner, this) || Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _userPending, 1, 0) != 0) return new("stale_consent");
        try
        {
            if (Interlocked.Exchange(ref consent.Consumed, 1) != 0) return new("stale_consent");
            await _serial.WaitAsync(cancellation).ConfigureAwait(false);
            return await Work(() => {
                Expire(); var session = _session;
                if (session is null || session.Consent != consent || !Live(session) || cancellation.IsCancellationRequested) return new("stale_consent");
                session.LocalConfirmed = true; session.Confirmation = _identity.Confirmation(session.Id, PairingRole.Initiator, session.Transcript!);
                session.LastIssue = _clock.GetTimestamp(); Publish(session, PairingStage.Waiting); return new("send_confirmation", session.Confirmation.ToArray());
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await CancelAsync().ConfigureAwait(false); return new("cancelled"); }
        finally { Volatile.Write(ref _userPending, 0); }
    }
    /// <summary>Only repeats the identical user-approved confirmation, at least5s apart. Never begins another session.</summary>
    public Task<PairingStep> PollAsync()
    {
        if (Volatile.Read(ref _stopped) != 0) return Task.FromResult(new PairingStep("stopped"));
        if (!_serial.Wait(0)) return Task.FromResult(new PairingStep("busy"));
        return Work(() => {
            Expire(); var session = _session;
            if (session is null) return new(View.Reason == "result_unconfirmed" ? "result_unconfirmed" : "finished");
            if (!session.LocalConfirmed || session.Confirmation is null || session.LastIssue is not { } last || _clock.GetElapsedTime(last) < PairingTiming.Poll) return new("wait");
            session.LastIssue = _clock.GetTimestamp(); return new("send_confirmation", session.Confirmation.ToArray());
        });
    }
    public Task<PairingStep> CancelAsync() => Cancel(null, "user_cancelled");
    public Task<PairingStep> AbortAsync(PairingFailure failure) => Cancel(null, failure switch {
        PairingFailure.Transport => "transport_error", PairingFailure.RemoteEnded => "remote_ended", PairingFailure.InvalidResponse => "invalid_response",
        PairingFailure.ScopeChanged => "scope_changed", PairingFailure.Superseded => "superseded", _ => throw new ArgumentOutOfRangeException(nameof(failure)) });
    public Task<PairingStep> RejectAsync(PairingConsent consent) { ArgumentNullException.ThrowIfNull(consent); return Cancel(consent, "user_rejected"); }
    private Task<PairingStep> Cancel(PairingConsent? consent, string reason)
    {
        lock (_lifecycle)
        {
            if (consent is not null && (!ReferenceEquals(consent.Owner, this) || consent.Epoch != Volatile.Read(ref _epoch))) return Task.FromResult(new PairingStep("stale_consent"));
            _cancelEpoch = Interlocked.Increment(ref _epoch); _cancelReason = reason;
            if (_cancel is { IsCompleted: false }) return _cancel;
            return _cancel = Task.Run(async () => {
                await _serial.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_cancelEpoch != Volatile.Read(ref _epoch)) return new PairingStep("superseded");
                    var session = _session;
                    if (session is null && View.Stage == PairingStage.Paired) return new PairingStep("already_complete");
                    byte[]? packet = session is null ? null : PairingWire.Cancel(session.Id, _cancelReason == "user_rejected" || _cancelReason == "user_cancelled" ? "user_rejected" : _cancelReason == "superseded" ? "superseded" : "error");
                    End(_cancelReason, invalidate: false); return new PairingStep(_cancelReason, packet);
                }
                finally { _serial.Release(); }
            });
        }
    }
    private Task<PairingStep> Work(Func<PairingStep> action) => Task.Run(() => {
        try { return action(); }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or CryptographicException or ObjectDisposedException)
        { End("storage_error"); return new("result_unconfirmed"); }
        finally { _serial.Release(); }
    });
    private bool Live(Session session) => session.Epoch == Volatile.Read(ref _epoch) && Volatile.Read(ref _stopped) == 0 && _clock.GetElapsedTime(session.Start) < PairingTiming.Session && session.ScopeCurrent();
    private void Publish(Session session, PairingStage stage) => Volatile.Write(ref _view, new(stage, SessionId: session.Id, PeerId: session.Expected?.PeerId ?? session.ExpectedPeer,
        Sas: session.Consent?.Sas, LocalConfirmed: session.LocalConfirmed, Consent: session.LocalConfirmed ? null : session.Consent,
        Remaining: PairingTiming.Session - _clock.GetElapsedTime(session.Start), MayBeAsymmetric: session.Confirmation is not null));
    private void Expire()
    {
        if (_session is not { } session) return;
        if (!Live(session)) End(session.Confirmation is null ? "timeout" : "result_unconfirmed");
        else if (!_identity.IsCurrent() || session.Expected is { } expected && _identity.Store.GetPeerSecuritySnapshot(expected.PeerId) != expected) End("trust_changed");
        else Publish(session, View.Stage);
    }
    private void End(string reason, bool invalidate = true)
    {
        if (_session is not { } session) return;
        _session = null; if (invalidate) Interlocked.Increment(ref _epoch);
        if (session.Expected is { } expected)
            try { _identity.Cleanup(expected); } catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        Volatile.Write(ref _view, new(PairingStage.Ended, reason, session.Id, session.Expected?.PeerId ?? session.ExpectedPeer, session.Consent?.Sas,
            session.LocalConfirmed, MayBeAsymmetric: session.Confirmation is not null || session.FinalizationStarted));
    }
    internal Task PulseAsync()
    {
        if (!_serial.Wait(0)) return Task.CompletedTask;
        return Work(() => { Expire(); return new("pulse"); });
    }
    private async Task Timer()
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500)); while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false)) await PulseAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            Volatile.Write(ref _stopped, 1); Interlocked.Increment(ref _epoch); _shutdown.Cancel();
            return new(_disposal = Task.Run(async () => { await _timer.ConfigureAwait(false); await _serial.WaitAsync().ConfigureAwait(false); try { End("stopped"); } finally { _serial.Release(); _shutdown.Dispose(); } }));
        }
    }
    private sealed class Session(string id, string? expectedPeer, long epoch, long start, Func<bool> scopeCurrent)
    {
        internal readonly Func<bool> ScopeCurrent = scopeCurrent;
        internal string Id = id;
        internal string? ExpectedPeer = expectedPeer, Na;
        internal long Epoch = epoch, Start = start;
        internal PeerSecuritySnapshot? Expected;
        internal byte[]? Commitment, NoncePacket, Transcript, PublicKey, Confirmation;
        internal PairingConsent? Consent;
        internal bool LocalConfirmed, FinalizationStarted;
        internal long? LastIssue;
    }
}
