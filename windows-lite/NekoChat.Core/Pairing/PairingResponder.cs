using System.Security.Cryptography;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

/// <summary>Conditional P1 responder. Borrows identity/store; starts CLOSED. At most one physical storage/protocol job, plus bounded user/cleanup work.</summary>
public sealed class PairingResponder : IAsyncDisposable, IPairingEndpoint
{
    private readonly PairingIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _timer;
    private readonly Dictionary<string, Finished> _finished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sources = new(StringComparer.Ordinal);
    private PairingView _view = new(PairingStage.Closed);
    private Session? _session;
    private long _epoch, _windowStart, _intentSequence;
    private int _accepting, _desiredOpen, _reveals, _userPending, _opening, _stopped;
    private long _historySequence;
    private string? _expected;
    private Task? _closing, _disposal;
    public PairingView View => Volatile.Read(ref _view);
    public int RetainedSessions => Volatile.Read(ref _retained);
    private int _retained;
    public PairingResponder(PairingIdentity identity, TimeProvider? clock = null) : this(identity, clock ?? TimeProvider.System, true) { }
    internal PairingResponder(PairingIdentity identity, TimeProvider clock, bool startTimer)
    { _identity = identity ?? throw new ArgumentNullException(nameof(identity)); _clock = clock ?? throw new ArgumentNullException(nameof(clock)); _timer = startTimer ? Task.Run(Timer) : Task.CompletedTask; }
    public async Task<bool> OpenWindowAsync(string? expectedPeerId = null)
    {
        if (expectedPeerId is not null && !V2Format.IsPeerId(expectedPeerId)) throw new ArgumentException("Invalid expected peer.");
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _opening, 1, 0) != 0) return false;
        long intent, openedAt = _clock.GetTimestamp();
        lock (_lifecycle) { Volatile.Write(ref _desiredOpen, 1); intent = Interlocked.Increment(ref _intentSequence); Interlocked.Increment(ref _epoch); Volatile.Write(ref _accepting, 0); }
        try
        {
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => {
                    End("superseded", invalidate: false);
                    if (!_identity.IsCurrent()) return false;
                    // The metadata read may have waited behind SQL. Publish readiness atomically with the latest UI/lifecycle intent, after that wait.
                    lock (_lifecycle)
                    {
                        if (Volatile.Read(ref _desiredOpen) == 0 || intent != Volatile.Read(ref _intentSequence) || Volatile.Read(ref _stopped) != 0) return false;
                        _windowStart = openedAt; _reveals = 0; _expected = expectedPeerId;
                        if (_clock.GetElapsedTime(openedAt) >= PairingTiming.Window) { Publish(new(PairingStage.Ended, "timeout")); return false; }
                        Volatile.Write(ref _accepting, 1); Publish(new(PairingStage.Listening, Remaining: PairingTiming.Window - _clock.GetElapsedTime(openedAt))); return true;
                    }
                }).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        }
        finally { Volatile.Write(ref _opening, 0); }
    }
    public Task StopAsync() => CloseWindowAsync();
    public Task CloseWindowAsync() => InvalidateAsync("window_closed");
    public Task InvalidateTrustAsync() => InvalidateAsync("trust_changed");
    private Task InvalidateAsync(string reason)
    {
        lock (_lifecycle)
        {
            Volatile.Write(ref _desiredOpen, 0); Volatile.Write(ref _accepting, 0); Interlocked.Increment(ref _intentSequence); Interlocked.Increment(ref _epoch);
            if (_closing is { IsCompleted: false }) return _closing;
            return _closing = Task.Run(async () => {
                await _serial.WaitAsync().ConfigureAwait(false);
                try { if (Volatile.Read(ref _desiredOpen) == 0) { End(reason, invalidate: false); Publish(new(PairingStage.Closed, reason)); } }
                finally { _serial.Release(); }
            });
        }
    }
    public Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default)
    {
        if (Volatile.Read(ref _stopped) != 0) return Task.FromResult(PairingResponses.Error("receiver_not_ready"));
        if (source.Length > 15 || !LanInvitation.PrivateIPv4(source) || !PairingWire.TryParse(body.Span, out var packet)) return Task.FromResult(PairingResponses.Error("invalid_payload"));
        if (!_serial.Wait(0)) return Task.FromResult(PairingResponses.Error("pair_unavailable"));
        return Task.Run(() => {
            try { return Process(packet!, source, cancellation); }
            catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or CryptographicException or ObjectDisposedException)
            { End("storage_error"); return PairingResponses.Error("storage_unavailable"); }
            finally { _serial.Release(); }
        });
    }
    private V2HttpResponse Process(PairingPacket packet, string source, CancellationToken cancellation)
    {
        Expire();
        if (Volatile.Read(ref _stopped) != 0 || cancellation.IsCancellationRequested) return PairingResponses.Error("receiver_not_ready");
        if (!_identity.IsCurrent()) { End("trust_changed"); return PairingResponses.Error("receiver_not_ready"); }
        if (_finished.TryGetValue(packet.SessionId, out var done))
        {
            if (done.Source != source) return PairingResponses.Error("pair_unavailable");
            if (packet.Kind == PairingKind.Confirmation && done.Confirmation is not null)
                return packet.SignatureBase64 == done.RemoteSignature ? PairingResponses.Ok(done.Confirmation) : PairingResponses.Error("pair_unavailable");
            return PairingResponses.Error(packet.Kind == PairingKind.Request ? "pair_state" : "pair_cancelled");
        }
        if (packet.Kind == PairingKind.Request) return Request(packet, source, cancellation);
        var session = _session;
        if (session is null || session.Id != packet.SessionId) return PairingResponses.Error("pair_state");
        if (session.Source != source) return PairingResponses.Error("pair_unavailable");
        if (!Live(session) || !CurrentSecurity(session)) { End("trust_changed"); return PairingResponses.Error("pair_cancelled"); }
        if (++session.Requests > PairingTiming.MaxRequests) { End("too_many_requests"); return PairingResponses.Error("pair_cancelled"); }
        if (packet.Kind == PairingKind.Cancel) { End("remote_cancel"); return PairingResponses.Ok("{}"u8.ToArray()); }
        if (packet.Kind == PairingKind.Nonce)
        {
            if (session.Na is not null) return session.Na == packet.Nonce ? PairingResponses.Ok(session.Reveal!) : PairingResponses.Error("pair_state");
            if (_reveals >= PairingTiming.MaxReveals) { Volatile.Write(ref _accepting, 0); End("reveal_limit"); return PairingResponses.Error("pair_unavailable"); }
            session.Na = packet.Nonce!; _reveals++;
            V2Format.TryDecodeNonce22(session.Id, out var sid); V2Format.TryDecodeNonce22(session.Na, out var na); V2Format.TryDecodeNonce22(session.Nb, out var nb);
            session.Transcript = PairingCrypto.Transcript(sid, session.PublicKey, _identity.PublicKey, na, nb);
            session.Reveal = PairingWire.Reveal(session.Id, _identity.PublicSpkiBase64, session.Nb);
            session.Consent = new(this, session.Epoch, session.Id, session.Expected.PeerId, PairingCrypto.Sas(session.Transcript));
            PublishSession(session); return PairingResponses.Ok(session.Reveal);
        }
        if (packet.Kind != PairingKind.Confirmation || session.Transcript is null) return PairingResponses.Error("pair_state");
        long now = _clock.GetTimestamp();
        if (session.LastConfirm is { } last && _clock.GetElapsedTime(last, now) < PairingTiming.ConfirmSpacing) return PairingResponses.Error("pair_unavailable");
        session.LastConfirm = now;
        if (session.RemoteSignature is not null && session.RemoteSignature != packet.SignatureBase64 ||
            !PairingCrypto.Verify(session.PublicKey, PairingRole.Initiator, session.Transcript, Convert.FromBase64String(packet.SignatureBase64!)))
            return PairingResponses.Error("pair_unavailable");
        session.RemoteSignature = packet.SignatureBase64;
        if (session.LocalConfirmed) return Finalize(session, cancellation);
        PublishSession(session); return PairingResponses.Ok(PairingWire.Pending(session.Id));
    }
    private V2HttpResponse Request(PairingPacket packet, string source, CancellationToken cancellation)
    {
        if (_session is { } existing)
        {
            if (existing.Id == packet.SessionId && existing.Source == source && Convert.ToBase64String(existing.PublicKey) == packet.PublicSpkiBase64 && existing.Na is null && Live(existing) && CurrentSecurity(existing))
            { if (++existing.Requests > PairingTiming.MaxRequests) { End("too_many_requests"); return PairingResponses.Error("pair_cancelled"); } return PairingResponses.Ok(existing.Commitment); }
            return PairingResponses.Error("pair_unavailable");
        }
        if (Volatile.Read(ref _accepting) == 0 || Volatile.Read(ref _desiredOpen) == 0 || _clock.GetElapsedTime(_windowStart) >= PairingTiming.Window) return PairingResponses.Error("pair_unavailable");
        byte[] key = Convert.FromBase64String(packet.PublicSpkiBase64!); string peer = V2Crypto.PeerId(key);
        if (peer == _identity.PeerId || _expected is not null && _expected != peer) return PairingResponses.Error("pair_unavailable");
        long now = _clock.GetTimestamp();
        foreach (string expired in _sources.Where(p => _clock.GetElapsedTime(p.Value, now) >= TimeSpan.FromSeconds(2)).Select(p => p.Key).ToArray()) _sources.Remove(expired);
        if (_sources.ContainsKey(source) || _sources.Count >= 1024) return PairingResponses.Error("pair_unavailable");
        _sources[source] = now; long epoch = Volatile.Read(ref _epoch);
        bool LiveRequest() => epoch == Volatile.Read(ref _epoch) && Volatile.Read(ref _accepting) != 0 && Volatile.Read(ref _desiredOpen) != 0 && Volatile.Read(ref _stopped) == 0 && !cancellation.IsCancellationRequested && _clock.GetElapsedTime(_windowStart) < PairingTiming.Window;
        var candidate = _identity.Candidate(key, LiveRequest);
        if (!candidate.Ready) return PairingResponses.Error("pair_unavailable");
        string nb = PairingWire.NewNonce(); V2Format.TryDecodeNonce22(packet.SessionId, out var sid); V2Format.TryDecodeNonce22(nb, out var nbBytes);
        var session = new Session(packet.SessionId, source, key, nb, PairingWire.Commitment(packet.SessionId, PairingCrypto.Commitment(sid, key, _identity.PublicKey, nbBytes)), candidate.Current, epoch, now);
        _session = session;
        if (!LiveRequest()) { End("cancelled"); return PairingResponses.Error("pair_cancelled"); }
        PublishSession(session); return PairingResponses.Ok(session.Commitment);
    }
    public async Task<bool> ConfirmLocalAsync(PairingConsent consent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!ReferenceEquals(consent.Owner, this) || Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _userPending, 1, 0) != 0) return false;
        try
        {
            if (Interlocked.Exchange(ref consent.Consumed, 1) != 0) return false;
            await _serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => {
                    Expire(); var session = _session;
                    if (session is null || session.Consent != consent || !Live(session) || !CurrentSecurity(session) || cancellation.IsCancellationRequested) return false;
                    session.LocalConfirmed = true;
                    if (session.RemoteSignature is null) { PublishSession(session); return true; }
                    return Finalize(session, cancellation).StatusCode == 200 && View.Stage == PairingStage.Paired;
                }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or CryptographicException or ObjectDisposedException)
            { End("storage_error"); return false; }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) { await InvalidateAsync("cancelled").ConfigureAwait(false); return false; }
        finally { Volatile.Write(ref _userPending, 0); }
    }
    public Task<bool> RejectAsync(PairingConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!ReferenceEquals(consent.Owner, this) || Interlocked.CompareExchange(ref _epoch, consent.Epoch + 1, consent.Epoch) != consent.Epoch) return Task.FromResult(false);
        return Task.Run(async () => {
            await _serial.WaitAsync().ConfigureAwait(false);
            try { if (_session?.Consent != consent) return false; End("user_rejected"); return true; }
            finally { _serial.Release(); }
        });
    }
    private V2HttpResponse Finalize(Session session, CancellationToken cancellation)
    {
        if (!session.LocalConfirmed || session.RemoteSignature is null || session.Transcript is null) return PairingResponses.Error("pair_state");
        byte[] confirmation = _identity.Confirmation(session.Id, PairingRole.Responder, session.Transcript);
        session.FinalizationStarted = true;
        if (!_identity.Finalize(session.Expected, () => Live(session) && !cancellation.IsCancellationRequested))
        { End("stale_security"); return PairingResponses.Error("pair_cancelled"); }
        Remember(session, confirmation); _session = null; Interlocked.Increment(ref _epoch); Volatile.Write(ref _accepting, 0);
        Publish(new(PairingStage.Paired, SessionId: session.Id, PeerId: session.Expected.PeerId, Sas: session.Consent!.Sas, LocalConfirmed: true, RemoteConfirmed: true, MayBeAsymmetric: true));
        return PairingResponses.Ok(confirmation);
    }
    private bool Live(Session session) => Volatile.Read(ref _stopped) == 0 && session.Epoch == Volatile.Read(ref _epoch) &&
        _clock.GetElapsedTime(session.Start) < PairingTiming.Session && _clock.GetElapsedTime(_windowStart) < PairingTiming.Window;
    private bool CurrentSecurity(Session session) => _identity.IsCurrent() && _identity.Store.GetPeerSecuritySnapshot(session.Expected.PeerId) == session.Expected;
    private void PublishSession(Session session) => Publish(new(session.Na is null ? PairingStage.Committed : session.LocalConfirmed ? PairingStage.Waiting : PairingStage.SasShown,
        SessionId: session.Id, PeerId: session.Expected.PeerId, Sas: session.Consent?.Sas, LocalConfirmed: session.LocalConfirmed, RemoteConfirmed: session.RemoteSignature is not null,
        Consent: session.LocalConfirmed ? null : session.Consent, Remaining: Remaining(session)));
    private TimeSpan Remaining(Session session) => TimeSpan.FromSeconds(Math.Max(0, Math.Min((PairingTiming.Window - _clock.GetElapsedTime(_windowStart)).TotalSeconds, (PairingTiming.Session - _clock.GetElapsedTime(session.Start)).TotalSeconds)));
    private void Publish(PairingView view) => Volatile.Write(ref _view, view);
    private void Expire()
    {
        foreach (string expired in _finished.Where(p => _clock.GetElapsedTime(p.Value.When) >= PairingTiming.History).Select(p => p.Key).ToArray()) _finished.Remove(expired);
        Volatile.Write(ref _retained, _finished.Count);
        if (Volatile.Read(ref _accepting) != 0 && _clock.GetElapsedTime(_windowStart) >= PairingTiming.Window) Volatile.Write(ref _accepting, 0);
        if (_session is { } session)
        {
            if (!Live(session) || session.Na is null && _clock.GetElapsedTime(session.Start) >= PairingTiming.Idle) End("timeout");
            else if (!CurrentSecurity(session)) End("trust_changed"); else PublishSession(session);
        }
        else if (View.Stage == PairingStage.Listening && Volatile.Read(ref _accepting) == 0) Publish(new(PairingStage.Ended, "timeout"));
    }
    private void Remember(Session session, byte[]? confirmation)
    {
        if (_finished.Count >= PairingTiming.MaxHistory) _finished.Remove(_finished.OrderBy(p => p.Value.Order).First().Key);
        _finished[session.Id] = new(session.Source, session.RemoteSignature, confirmation?.ToArray(), _clock.GetTimestamp(), ++_historySequence); Volatile.Write(ref _retained, _finished.Count);
    }
    private void End(string reason, bool invalidate = true)
    {
        if (_session is not { } session) return;
        _session = null; if (invalidate) Interlocked.Increment(ref _epoch); Remember(session, null);
        try { _identity.Cleanup(session.Expected); } catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        Publish(new(PairingStage.Ended, reason, session.Id, session.Expected.PeerId, session.Consent?.Sas, session.LocalConfirmed, session.RemoteSignature is not null, MayBeAsymmetric: session.FinalizationStarted));
    }
    internal Task PulseAsync()
    {
        if (!_serial.Wait(0)) return Task.CompletedTask;
        return Task.Run(() => { try { Expire(); } catch (Exception e) when (e is StoreException or IOException or ObjectDisposedException) { End("storage_error"); } finally { _serial.Release(); } });
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
            Volatile.Write(ref _stopped, 1); Volatile.Write(ref _desiredOpen, 0); Volatile.Write(ref _accepting, 0); Interlocked.Increment(ref _intentSequence); Interlocked.Increment(ref _epoch); _shutdown.Cancel();
            return new(_disposal = Task.Run(async () => { await _timer.ConfigureAwait(false); await _serial.WaitAsync().ConfigureAwait(false); try { End("stopped"); Publish(new(PairingStage.Closed, "stopped")); } finally { _serial.Release(); _shutdown.Dispose(); } }));
        }
    }
    private sealed class Session(string id, string source, byte[] key, string nb, byte[] commitment, PeerSecuritySnapshot expected, long epoch, long start)
    {
        internal string Id = id, Source = source, Nb = nb;
        internal byte[] PublicKey = key, Commitment = commitment;
        internal PeerSecuritySnapshot Expected = expected;
        internal long Epoch = epoch, Start = start;
        internal string? Na, RemoteSignature;
        internal byte[]? Transcript, Reveal;
        internal PairingConsent? Consent;
        internal bool LocalConfirmed, FinalizationStarted;
        internal int Requests = 1;
        internal long? LastConfirm;
    }
    private sealed record Finished(string Source, string? RemoteSignature, byte[]? Confirmation, long When, long Order);
}
