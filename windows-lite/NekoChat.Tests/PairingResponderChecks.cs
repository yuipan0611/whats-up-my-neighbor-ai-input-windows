using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingResponderChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("P1 responder: " + name); }
        await using (var f = new Fixture())
        {
            Check((await f.Request()).StatusCode == 403 && f.Number("SELECT count(*) FROM Peer") == 0 && f.Actor.View.Stage == PairingStage.Closed, "closed-by-default rejects request without creating metadata");
            await f.Start();
            Check(f.Actor.View is { Stage: PairingStage.SasShown, LocalConfirmed: false, RemoteConfirmed: false, Consent: not null } && f.State == PairingState.Pending && f.Number("SELECT count(*) FROM Message") == 0, "verified commitment/reveal shows SAS but neither consent is assumed");
            var remote = await f.Remote();
            Check(Is(remote, PairingKind.Pending) && f.State == PairingState.Pending && f.Actor.View is { RemoteConfirmed: true, LocalConfirmed: false }, "real initiator signature alone cannot pair");
            var ticket = f.Actor.View.Consent!;
            Check(await f.Actor.ConfirmLocalAsync(ticket) && f.State == PairingState.Paired && f.Actor.View is { Stage: PairingStage.Paired, LocalConfirmed: true, RemoteConfirmed: true, MayBeAsymmetric: true }, "second human-consent condition commits local pairing, remote persistence still unknown");
            Check(!await f.Actor.ConfirmLocalAsync(ticket), "local confirmation ticket is single use");
            var ack = await f.Remote(); byte[] exact = ack.Body.ToArray();
            Check(Is(ack, PairingKind.Confirmation) && PairingWire.TryParse(ack.Body, out var packet) && PairingCrypto.Verify(f.Identity.PublicKey, PairingRole.Responder, f.Transcript!, Convert.FromBase64String(packet!.SignatureBase64!)), "lost ACK is recovered with actual role-bound responder signature");
            long revision = f.Store.GetPeerSecuritySnapshot(f.PeerId).PeerRevision!.Value; ack.Body[0] ^= 1;
            Check((await f.Remote()).Body.SequenceEqual(exact) && f.Store.GetPeerSecuritySnapshot(f.PeerId).PeerRevision == revision, "returned byte mutation cannot poison cache; duplicate never rewrites trust");
            f.Store.SetPairingState(f.PeerId, PairingState.Revoked);
            Check((await f.Remote()).Body.SequenceEqual(exact) && f.State == PairingState.Revoked, "historical ACK retry does not resurrect revoked trust");
            PairingWire.TryParse(f.RemotePacket!, out var originalPacket); byte[] alternate = Convert.FromBase64String(originalPacket!.SignatureBase64!);
            var order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);
            byte[] opposite = (order - new BigInteger(alternate.AsSpan(32), isUnsigned: true, isBigEndian: true)).ToByteArray(isUnsigned: true, isBigEndian: true);
            Array.Clear(alternate, 32, 32); opposite.CopyTo(alternate.AsSpan(64 - opposite.Length));
            var different = PairingWire.ConfirmationSignature(f.Sid, Convert.ToBase64String(alternate));
            Check(PairingCrypto.Verify(f.Public, PairingRole.Initiator, f.Transcript!, alternate) && (await f.Actor.HandleAsync(different, Fixture.Source)).StatusCode == 403 && f.State == PairingState.Revoked, "different valid randomized signature cannot replace the recorded confirmation");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var ticket = f.Actor.View.Consent!;
            Check(await f.Actor.ConfirmLocalAsync(ticket) && f.State == PairingState.Pending && f.Actor.View is { Stage: PairingStage.Waiting, LocalConfirmed: true, RemoteConfirmed: false, Consent: null }, "local-first confirmation alone cannot pair");
            Check(Is(await f.Remote(), PairingKind.Confirmation) && f.State == PairingState.Paired, "remote signature arriving second commits exactly once");
        }
        await using (var f = new Fixture())
        {
            f.Store.RegisterPeer(f.PeerId, PairingState.Paired, f.Public); var original = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            await f.Actor.OpenWindowAsync(); await f.Request();
            Check((await f.Actor.HandleAsync(PairingWire.Cancel(f.Sid, "error"), Fixture.Source)).StatusCode == 200 && f.Store.GetPeerSecuritySnapshot(f.PeerId) == original, "spoofed public-key request then unauthenticated cancel cannot downgrade established pairing");
            f.Clock.Advance(2); await f.Start(); await f.Remote();
            Check(await f.Actor.ConfirmLocalAsync(f.Actor.View.Consent!) && f.Store.GetPeerSecuritySnapshot(f.PeerId) == original && f.Actor.View.Stage == PairingStage.Paired, "unchanged paired CAS is a genuine idempotent success after both consents");
        }
        foreach (PairingState state in new[] { PairingState.Revoked, PairingState.Suspended })
        {
            await using var f = new Fixture(); await f.Start(); await f.Remote(); var oldTicket = f.Actor.View.Consent!;
            f.Store.SetPairingState(f.PeerId, state);
            Check(!await f.Actor.ConfirmLocalAsync(oldTicket) && f.State == state && f.Actor.View.Stage == PairingStage.Ended, "revocation/suspension before second consent is preserved: " + state);
            Check(!await f.Actor.RejectAsync(oldTicket) && f.State == state, "late rejection cannot undo newer trust state");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var old = f.Actor.View.Consent!;
            f.Store.SetPairingState(f.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.PeerId, PairingState.Pending);
            Check((await f.Remote()).StatusCode == 409 && !await f.Actor.ConfirmLocalAsync(old) && f.State == PairingState.Pending, "revoke→pending ABA invalidates both remote and local old confirmation");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var old = f.Actor.View.Consent!;
            f.Execute("UPDATE LocalIdentity SET credentialRef='other'; UPDATE LocalIdentity SET credentialRef='fixture-key'");
            Check(!await f.Actor.ConfirmLocalAsync(old) && f.State != PairingState.Paired && f.Actor.View.Stage == PairingStage.Ended, "local credential ABA cannot pair through an obsolete signer");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var old = f.Actor.View.Consent!;
            Check(await f.Actor.RejectAsync(old) && f.State == PairingState.Unpaired && f.Actor.View.Reason == "user_rejected", "explicit mismatch rejection cleans only the owned pending state");
            f.Clock.Advance(2); await f.Start();
            Check(!await f.Actor.RejectAsync(old) && f.Actor.View.Stage == PairingStage.SasShown, "old dialog cannot reject a newer session");
            Check(!await f.Actor.ConfirmLocalAsync(old) && f.State == PairingState.Unpaired, "old dialog cannot confirm a newer session");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var first = f.Actor.View.Consent!;
            Check(await f.Actor.OpenWindowAsync() && f.Actor.View.Stage == PairingStage.Listening && f.State == PairingState.Unpaired && !await f.Actor.ConfirmLocalAsync(first), "explicit new window supersedes old pending session with a new consent generation");
            await Task.WhenAll(f.Actor.CloseWindowAsync(), f.Actor.CloseWindowAsync(), f.Actor.CloseWindowAsync());
            Check(f.Actor.View.Stage == PairingStage.Closed, "coalesced repeated close drains cleanup and leaves no accepting window");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); var wrong = PairingWire.Confirmation(f.Sid, PairingRole.Responder, f.Transcript!, f.PeerKey);
            Check((await f.Actor.HandleAsync(wrong, Fixture.Source)).StatusCode == 403 && !f.Actor.View.RemoteConfirmed && f.State == PairingState.Pending, "reflected responder-role signature is not initiator consent");
            f.Clock.Advance(1); var valid = await f.Remote();
            Check(Is(valid, PairingKind.Pending) && (await f.Remote()).StatusCode == 403 && f.State == PairingState.Pending, "one-second confirm spacing rejects too-fast repetition without pairing");
            Check((await f.Actor.HandleAsync(f.RemotePacket!, "127.0.0.2")).StatusCode == 403 && f.State == PairingState.Pending, "session source endpoint cannot be silently changed");
        }
        foreach (string point in new[] { "idle", "window" })
        {
            await using var f = new Fixture(); await f.Actor.OpenWindowAsync(); await f.Request();
            if (point != "idle") await f.Reveal();
            var ticket = f.Actor.View.Consent;
            f.Clock.Advance(point == "idle" ? 20 : 120);
            Check((await f.Actor.HandleAsync(point == "idle" ? PairingWire.Nonce(f.Sid, f.Na) : f.RemotePacketForCurrent(), Fixture.Source)).StatusCode == 409 && f.State == PairingState.Unpaired,
                "exact monotonic deadline expires before transition: " + point);
            if (ticket is not null) Check(!await f.Actor.ConfirmLocalAsync(ticket), "expired ticket cannot pair");
        }
        await using (var f = new Fixture())
        {
            await f.Actor.OpenWindowAsync(); f.Clock.Advance(118); await f.Request(); await f.Reveal();
            Check(f.Actor.View.Remaining == TimeSpan.FromSeconds(2), "late request does not restart the explicit user-window lifetime");
            f.Clock.Advance(2); await f.Actor.PulseAsync();
            Check(f.State == PairingState.Unpaired && f.Actor.View.Stage == PairingStage.Ended, "original window deadline wins over a later per-session deadline");
        }
        await using (var f = new Fixture())
        {
            await f.Actor.OpenWindowAsync(); var first = await f.Request();
            Check((await f.Request()).Body.SequenceEqual(first.Body), "identical request is idempotent without a new commitment");
            await f.Reveal(); var firstReveal = (await f.Actor.HandleAsync(PairingWire.Nonce(f.Sid, f.Na), Fixture.Source)).Body;
            Check((await f.Actor.HandleAsync(PairingWire.Nonce(f.Sid, f.Na), Fixture.Source)).Body.SequenceEqual(firstReveal), "same nonce replays the exact reveal without consuming another reveal");
            for (int i = 0; i < 3; i++)
            {
                if (i != 0) { f.Clock.Advance(2); f.NewSession(); await f.Request(); await f.Reveal(); }
                if (i < 2) await f.Actor.RejectAsync(f.Actor.View.Consent!);
            }
            await f.Actor.RejectAsync(f.Actor.View.Consent!); f.Clock.Advance(2); f.NewSession(); await f.Request();
            Check((await f.Actor.HandleAsync(PairingWire.Nonce(f.Sid, f.Na), Fixture.Source)).StatusCode == 403 && f.Actor.View.Stage == PairingStage.Ended, "fourth fresh reveal in one explicit window is refused");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); V2HttpResponse? last = null;
            for (int i = 0; i < 201; i++) last = await f.Actor.HandleAsync(PairingWire.Nonce(f.Sid, f.Na), Fixture.Source);
            Check(last!.StatusCode == 409 && f.State == PairingState.Unpaired && f.Actor.View.Reason == "too_many_requests", "matching-session request cap includes repeated nonce requests");
        }
        foreach (bool afterCommit in new[] { false, true })
        {
            await using var f = new Fixture(); await f.Start(); await f.Remote(); bool once = false;
            void Fail(string op) { if (op == "security-change" && !once) { once = true; throw new IOException("fixture failed transaction boundary"); } }
            if (afterCommit) f.Faults.After = Fail; else f.Faults.Before = Fail;
            Check(!await f.Actor.ConfirmLocalAsync(f.Actor.View.Consent!) && f.Actor.View is { Stage: PairingStage.Ended, MayBeAsymmetric: true }, "uncertain storage completion never claims definite non-pairing");
            Check(f.State == (afterCommit ? PairingState.Paired : PairingState.Unpaired) && f.Number("SELECT count(*) FROM Message") == 0, "actual storage boundary determines preserved trust without any message history");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await f.Remote(); var ticket = f.Actor.View.Consent!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int once = 0;
            f.Faults.Before = op => { if (op == "security-change" && Interlocked.Increment(ref once) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Blocked finalize deadline."); } };
            var confirm = f.Actor.ConfirmLocalAsync(ticket);
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("Finalize did not enter the actual SQLite boundary.");
            var close = f.Actor.CloseWindowAsync();
            var refused = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Actor.HandleAsync(f.RemotePacket!, Fixture.Source)));
            Check(!close.IsCompleted && refused.All(r => r.StatusCode == 403), "close intent is immediate while physical DB job stays occupied and rejects extra jobs");
            release.Set();
            Check(!await confirm, "cancellation during real SQL boundary invalidates final paired write"); await close;
            Check(f.State == PairingState.Unpaired && f.Actor.View.Stage == PairingStage.Closed, "canceled physical work drains before cleanup completes");
        }
        foreach (bool expireOpen in new[] { false, true })
        {
            await using var f = new Fixture(); await f.Start(); await f.Remote();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int once = 0;
            f.Faults.Before = op => { if (op == "security-change" && Interlocked.Increment(ref once) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("New-window boundary timed out."); } };
            var oldConfirm = f.Actor.ConfirmLocalAsync(f.Actor.View.Consent!);
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("New-window boundary was not entered.");
            var opened = f.Actor.OpenWindowAsync(); f.Clock.Advance(expireOpen ? 120 : 1); release.Set();
            Check(!await oldConfirm && await opened == !expireOpen && f.State == PairingState.Unpaired, "new explicit window cancels old finalization and preserves its own latest intent/clock budget");
            Check(f.Actor.View.Stage == (expireOpen ? PairingStage.Ended : PairingStage.Listening), "waiting for old SQL work never restarts an expired user window");
        }
        await using (var f = new Fixture())
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Faults.Read = op => { if (op == "GetLocalIdentitySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Readiness completion barrier timeout."); } };
            var open = f.Actor.OpenWindowAsync();
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("Real SQL read-completion barrier missing.");
            var close = f.Actor.CloseWindowAsync(); release.Set();
            Check(!await open, "cancel during delayed completion of a real SQL identity read cannot publish a ready window"); await close;
            Check(f.Actor.View.Stage == PairingStage.Closed && f.Number("SELECT count(*) FROM Peer") == 0, "canceled readiness drains without candidates");
        }
        for (int i = 0; i < 4; i++)
        {
            await using var f = new Fixture(); await f.Start(); var ticket = f.Actor.View.Consent!; var before = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            var user = f.Actor.ConfirmLocalAsync(ticket); var remote = f.Remote(); await Task.WhenAll(user, remote);
            if (f.State != PairingState.Paired) { f.Clock.Advance(1); await f.Remote(); }
            Check(f.State == PairingState.Paired && f.Store.GetPeerSecuritySnapshot(f.PeerId).PeerRevision == before.PeerRevision + 1, "genuinely concurrent user/remote consent commits paired exactly once");
        }
        await using (var f = new Fixture())
        {
            string? first = null, last = null;
            for (int i = 0; i < 70; i++)
            {
                f.Clock.Advance(2); f.NewSession(); first ??= f.Sid; last = f.Sid;
                await f.Actor.OpenWindowAsync(); await f.Request(); await f.Actor.HandleAsync(PairingWire.Cancel(f.Sid, "error"), Fixture.Source);
            }
            Check(f.Actor.RetainedSessions == 64, "ended-session cache has exact bounded capacity");
            f.Clock.Advance(2); await f.Actor.OpenWindowAsync();
            Check((await f.Actor.HandleAsync(PairingWire.Request(last!, f.PublicBase64), Fixture.Source)).StatusCode == 409 &&
                Is(await f.Actor.HandleAsync(PairingWire.Request(first!, f.PublicBase64), Fixture.Source), PairingKind.Commitment), "only the newest64 ended session IDs are retained");
            await f.Actor.CloseWindowAsync(); f.Clock.Advance(600); await f.Actor.PulseAsync();
            Check(f.Actor.RetainedSessions == 0, "exact ten-minute retention boundary evicts finished records");
        }
        await using (var f = new Fixture(timer: true))
        {
            await f.Start(); f.Clock.Advance(120); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (f.Actor.View.Stage != PairingStage.Ended) await Task.Delay(20, deadline.Token);
            Check(f.State == PairingState.Unpaired, "actual background timer expires inactive window and cleans pending state");
        }
        Console.WriteLine($"Conditional P1 responder: {checks} real signature/SQLite consent, cancellation, cache, expiry, resource and concurrency checks; no TCP or WPF claim."); return checks;
    }
    private static bool Is(V2HttpResponse response, PairingKind kind) => response.StatusCode == 200 && PairingWire.TryParse(response.Body, out var packet) && packet!.Kind == kind;
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Source = "127.0.0.1";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-p1-responder-" + Guid.NewGuid().ToString("N"));
        internal ECDsa PeerKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal byte[] Public => PeerKey.ExportSubjectPublicKeyInfo();
        internal string PublicBase64 => Convert.ToBase64String(Public);
        internal string PeerId => V2Crypto.PeerId(Public);
        internal string Sid = PairingWire.NewNonce(), Na = PairingWire.NewNonce();
        internal byte[]? Transcript, RemotePacket;
        internal ChatStore Store { get; }
        internal PairingIdentity Identity { get; }
        internal PairingResponder Actor { get; }
        internal Clock Clock { get; } = new();
        internal Faults Faults { get; } = new();
        internal PairingState? State => Store.GetPeerSecuritySnapshot(PeerId).Pairing;
        internal Fixture(bool timer = false)
        {
            Store = ChatStore.Open(new(Root) { Faults = Faults }); var local = ECDsa.Create(ECCurve.NamedCurves.nistP256); byte[] key = local.ExportSubjectPublicKeyInfo();
            Store.EnsureLocalIdentity(V2Crypto.PeerId(key), key, "fixture-key"); Identity = new(Store, local); Actor = new(Identity, Clock, timer);
        }
        internal void NewSession() { Sid = PairingWire.NewNonce(); Na = PairingWire.NewNonce(); Transcript = RemotePacket = null; }
        internal Task<V2HttpResponse> Request() => Actor.HandleAsync(PairingWire.Request(Sid, PublicBase64), Source);
        internal async Task Start() { NewSession(); await Actor.OpenWindowAsync(); var reply = await Request(); if (!Is(reply, PairingKind.Commitment)) throw new Exception("Fixture request rejected."); await Reveal(); }
        internal async Task Reveal()
        {
            var reply = await Actor.HandleAsync(PairingWire.Nonce(Sid, Na), Source);
            if (!PairingWire.TryParse(reply.Body, out var reveal) || reveal!.Kind != PairingKind.Reveal) throw new Exception("Fixture reveal rejected.");
            V2Format.TryDecodeNonce22(Sid, out var sid); V2Format.TryDecodeNonce22(Na, out var na); V2Format.TryDecodeNonce22(reveal.Nonce, out var nb);
            Transcript = PairingCrypto.Transcript(sid, Public, Identity.PublicKey, na, nb);
            if (Actor.View.Sas != PairingCrypto.Sas(Transcript)) throw new Exception("Actual responder SAS mismatch.");
        }
        internal byte[] RemotePacketForCurrent() => RemotePacket ??= PairingWire.Confirmation(Sid, PairingRole.Initiator, Transcript!, PeerKey);
        internal Task<V2HttpResponse> Remote() => Actor.HandleAsync(RemotePacketForCurrent(), Source);
        internal long Number(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        internal void Execute(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        private SqliteConnection Connection() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); return c; }
        public async ValueTask DisposeAsync() { await Actor.DisposeAsync(); Identity.Dispose(); Store.Dispose(); PeerKey.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After, Read;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
        public void AfterRead(string op) => Read?.Invoke(op);
    }
}
