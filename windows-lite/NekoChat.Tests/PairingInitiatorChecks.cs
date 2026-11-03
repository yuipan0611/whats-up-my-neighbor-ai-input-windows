using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingInitiatorChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 initiator: " + label); }
        await using (var f = new Pair())
        {
            var begin = await f.I.Initiator.BeginAsync(f.R.Identity.PeerId);
            Check(begin.Code == "send_request" && f.I.Initiator.View is { Stage: PairingStage.Requested, Consent: null, Sas: null } && f.I.Number("SELECT count(*) FROM Peer") == 0, "outbound request alone creates no candidate or human consent");
            await f.R.Responder.OpenWindowAsync(f.I.Identity.PeerId); var commit = await f.R.Responder.HandleAsync(begin.Packet!, Pair.Source);
            var nonce = await f.I.Initiator.AdvanceAsync(commit.Body); var duplicate = await f.I.Initiator.AdvanceAsync(commit.Body);
            Check(nonce.Packet!.SequenceEqual(duplicate.Packet!) && f.I.Initiator.View.Consent is null, "idempotent commitment yields exact same nonce, never a new grinding opportunity");
            var reveal = await f.R.Responder.HandleAsync(nonce.Packet!, Pair.Source); var ready = await f.I.Initiator.AdvanceAsync(reveal.Body);
            Check(ready.Code == "awaiting_user" && f.I.Initiator.View.Sas == f.R.Responder.View.Sas && f.StateI == PairingState.Pending && f.StateR == PairingState.Pending, "real two-key transcript agrees while both stores remain pending");
            var ticket = f.I.Initiator.View.Consent!;
            Check((await f.I.Initiator.ConfirmLocalAsync(f.R.Responder.View.Consent!)).Code == "stale_consent" && !await f.R.Responder.ConfirmLocalAsync(ticket), "consent tickets cannot cross actor or side");
            Check((await f.I.Initiator.PollAsync()).Packet is null && f.StateI == PairingState.Pending, "no confirmation emitted before local human action");
            var confirmation = await f.I.Initiator.ConfirmLocalAsync(ticket); byte[] original = confirmation.Packet!.ToArray();
            Check(confirmation.Code == "send_confirmation" && f.StateI == PairingState.Pending && (await f.I.Initiator.ConfirmLocalAsync(ticket)).Code == "stale_consent", "user-confirmed initiator signature is emitted once without prematurely pairing");
            var pending = await f.R.Responder.HandleAsync(confirmation.Packet!, Pair.Source); var waiting = await f.I.Initiator.AdvanceAsync(pending.Body);
            Check(waiting.Code == "pending" && f.StateI == PairingState.Pending && f.StateR == PairingState.Pending, "responder's missing local consent keeps both pending");
            Check(await f.R.Responder.ConfirmLocalAsync(f.R.Responder.View.Consent!) && f.StateR == PairingState.Paired && f.StateI == PairingState.Pending, "responder completion has no reverse-channel effect on initiator");
            Check((await f.I.Initiator.PollAsync()).Packet is null, "confirmation polls do not exceed selected five-second interval");
            f.Clock.Advance(5); var poll = await f.I.Initiator.PollAsync(); poll.Packet![0] ^= 1;
            f.Clock.Advance(5); var next = await f.I.Initiator.PollAsync();
            Check(next.Packet!.SequenceEqual(original), "caller mutation cannot alter the cached identical confirmation retry");
            var ack = await f.R.Responder.HandleAsync(next.Packet!, Pair.Source);
            Check((await f.I.Initiator.AdvanceAsync(ack.Body)).Code == "paired" && f.StateI == PairingState.Paired && f.StateR == PairingState.Paired, "verified responder signature commits the genuine initiator store");
            Check(f.I.Number("SELECT count(*) FROM Message") == 0 && f.R.Number("SELECT count(*) FROM Message") == 0 && f.I.Number("SELECT count(*) FROM ManualSendAttempt") == 0 && (await f.I.Initiator.PollAsync()).Packet is null, "pairing control never invents private messages or starts another session");
        }
        await using (var f = new Pair())
        {
            await f.Handshake(); string sid = f.I.Initiator.View.SessionId!; var old = f.I.Initiator.View.Consent!;
            var cancel = await f.I.Initiator.RejectAsync(old);
            Check(cancel.Code == "user_rejected" && f.StateI == PairingState.Unpaired && f.StateR == PairingState.Pending && f.I.Initiator.View.Stage == PairingStage.Ended, "initiator mismatch rejection cleans local pending but does not pretend remote cancellation");
            Check((await f.R.Responder.HandleAsync(cancel.Packet!, Pair.Source)).StatusCode == 200 && f.StateR == PairingState.Unpaired, "best-effort forward cancel can clean the actual remote session");
            f.Clock.Advance(2); await f.Handshake();
            Check(f.I.Initiator.View.SessionId != sid && (await f.I.Initiator.ConfirmLocalAsync(old)).Code == "stale_consent" && (await f.I.Initiator.RejectAsync(old)).Code == "stale_consent", "old dialog cannot confirm or cancel the new session");
        }
        await using (var f = new Pair())
        {
            await f.Handshake(); var oldSid = f.I.Initiator.View.SessionId;
            var next = await f.I.Initiator.BeginAsync(f.R.Identity.PeerId);
            Check(next.PriorCancellation is not null && PairingWire.TryParse(next.PriorCancellation, out var packet) && packet!.SessionId == oldSid && packet.CancelReason == "superseded" && f.StateI == PairingState.Unpaired, "explicit superseding begin exposes old cancel frame before the new request");
        }
        await using (var f = new Pair())
        {
            await f.Handshake(); var confirmation = await f.I.Initiator.ConfirmLocalAsync(f.I.Initiator.View.Consent!);
            await f.R.Responder.HandleAsync(confirmation.Packet!, Pair.Source); await f.R.Responder.ConfirmLocalAsync(f.R.Responder.View.Consent!);
            f.Clock.Advance(120);
            Check((await f.I.Initiator.PollAsync()).Code == "result_unconfirmed" && f.I.Initiator.View.MayBeAsymmetric && f.StateI == PairingState.Unpaired && f.StateR == PairingState.Paired, "lost responder confirmation at exact deadline leaves honest asymmetric result");
            var lateAck = await f.R.Responder.HandleAsync(confirmation.Packet!, Pair.Source);
            Check((await f.I.Initiator.AdvanceAsync(lateAck.Body)).Code == "finished" && f.StateI == PairingState.Unpaired, "late historical ACK cannot revive expired initiator consent");
            f.Clock.Advance(2); await f.Handshake(); var canceled = await f.I.Initiator.RejectAsync(f.I.Initiator.View.Consent!); await f.R.Responder.HandleAsync(canceled.Packet!, Pair.Source);
            Check(f.StateR == PairingState.Paired && f.StateI == PairingState.Unpaired, "failed explicit re-pair never downgrades the responder's established pairing");
        }
        await using (var f = new Pair())
        {
            await f.R.Responder.OpenWindowAsync(); var begin = await f.I.Initiator.BeginAsync(); var commit = await f.R.Responder.HandleAsync(begin.Packet!, Pair.Source); var nonce = await f.I.Initiator.AdvanceAsync(commit.Body); var reveal = await f.R.Responder.HandleAsync(nonce.Packet!, Pair.Source);
            PairingWire.TryParse(reveal.Body, out var original); using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] forged = PairingWire.Reveal(original!.SessionId, Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()), original.Nonce!);
            Check((await f.I.Initiator.AdvanceAsync(forged)).Code == "commitment_mismatch" && f.I.Number("SELECT count(*) FROM Peer") == 0 && f.I.Initiator.View.Consent is null, "swapped revealed key is rejected before candidate creation or SAS consent");
        }
        await using (var f = new Pair())
        {
            await f.Handshake();
            byte[] premature = f.R.Identity.Confirmation(f.I.Initiator.View.SessionId!, PairingRole.Responder, f.Transcript!);
            Check((await f.I.Initiator.AdvanceAsync(premature)).Code == "invalid_response" && f.StateI == PairingState.Unpaired, "even valid responder proof cannot replace local human confirmation");
        }
        foreach (string attack in new[] { "wrong-role", "wrong-session", "wrong-key" })
        {
            await using var f = new Pair(); await f.Handshake(); var confirmation = await f.I.Initiator.ConfirmLocalAsync(f.I.Initiator.View.Consent!);
            PairingWire.TryParse(confirmation.Packet!, out var actual); byte[] forged;
            if (attack == "wrong-role") forged = PairingWire.ConfirmationSignature(actual!.SessionId, actual.SignatureBase64!);
            else
            {
                await f.R.Responder.HandleAsync(confirmation.Packet!, Pair.Source); await f.R.Responder.ConfirmLocalAsync(f.R.Responder.View.Consent!);
                var ack = await f.R.Responder.HandleAsync(confirmation.Packet!, Pair.Source); PairingWire.TryParse(ack.Body, out var valid);
                if (attack == "wrong-session") forged = PairingWire.ConfirmationSignature(PairingWire.NewNonce(), valid!.SignatureBase64!);
                else { using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256); forged = PairingWire.Confirmation(valid!.SessionId, PairingRole.Responder, f.Transcript!, attacker); }
            }
            Check((await f.I.Initiator.AdvanceAsync(forged)).Code == "invalid_response" && f.StateI == PairingState.Unpaired && f.I.Initiator.View.MayBeAsymmetric, "bad responder confirmation cannot pair: " + attack);
        }
        foreach (string change in new[] { "revoke", "local-aba" })
        {
            await using var f = new Pair(); await f.Handshake(); var local = await f.I.Initiator.ConfirmLocalAsync(f.I.Initiator.View.Consent!);
            await f.R.Responder.HandleAsync(local.Packet!, Pair.Source); await f.R.Responder.ConfirmLocalAsync(f.R.Responder.View.Consent!); var ack = await f.R.Responder.HandleAsync(local.Packet!, Pair.Source);
            if (change == "revoke") f.I.Store.SetPairingState(f.R.Identity.PeerId, PairingState.Revoked);
            else f.I.Execute("UPDATE LocalIdentity SET credentialRef='other'; UPDATE LocalIdentity SET credentialRef='fixture'");
            await f.I.Initiator.AdvanceAsync(ack.Body);
            Check(f.StateI != PairingState.Paired && f.I.Initiator.View is { Stage: PairingStage.Ended, Reason: "trust_changed" }, "freshness fences invalidate actual responder proof: " + change);
        }
        await using (var f = new Pair())
        {
            await f.Handshake(); var first = await f.I.Initiator.ConfirmLocalAsync(f.I.Initiator.View.Consent!);
            var quota = new V2TcpListener.SourceRateLimit(f.Clock, 20, 1024); quota.Allow(Pair.Source); quota.Allow(Pair.Source); quota.Allow(Pair.Source);
            bool bounded = true;
            for (int i = 0; i < 23; i++) { f.Clock.Advance(5); var next = await f.I.Initiator.PollAsync(); bounded &= next.Packet!.SequenceEqual(first.Packet!) && quota.Allow(Pair.Source); }
            Check(bounded, "five-second same-signature control polling fits actual unchanged20/min rolling source limiter");
            f.Clock.Advance(5); Check((await f.I.Initiator.PollAsync()).Packet is null && f.I.Initiator.View.Reason == "result_unconfirmed", "polling never extends the original120-second session");
        }
        await using (var f = new Pair())
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.I.Faults.Read = op => { if (op == "GetLocalIdentitySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Initiator read-completion barrier timeout."); } };
            var begin = f.I.Initiator.BeginAsync();
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("Initiator real SQL read-completion barrier missing.");
            var cancel = f.I.Initiator.CancelAsync(); release.Set();
            Check((await begin).Packet is null, "cancel during delayed completion of a real SQL identity read cannot issue an initiating request"); await cancel;
            Check(f.I.Number("SELECT count(*) FROM Peer") == 0 && f.I.Initiator.View.Stage != PairingStage.Requested, "canceled initiator readiness leaves no new session metadata");
        }
        Console.WriteLine($"Conditional P1 initiator: {checks} two-store real-P256 packet-driven consent/ACK-loss/attack/expiry checks; no TCP or WPF claim."); return checks;
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal const string Source = "10.0.0.1";
        internal Clock Clock { get; } = new();
        internal Node I { get; }
        internal Node R { get; }
        internal byte[]? Transcript;
        internal PairingState? StateI => I.Store.GetPeerSecuritySnapshot(R.Identity.PeerId).Pairing;
        internal PairingState? StateR => R.Store.GetPeerSecuritySnapshot(I.Identity.PeerId).Pairing;
        internal Pair() { I = new(Clock); R = new(Clock); }
        internal async Task Handshake()
        {
            if (!await R.Responder.OpenWindowAsync(I.Identity.PeerId)) throw new Exception("Responder window not opened.");
            var begin = await I.Initiator.BeginAsync(R.Identity.PeerId);
            if (begin.PriorCancellation is not null) await R.Responder.HandleAsync(begin.PriorCancellation, Source);
            var commit = await R.Responder.HandleAsync(begin.Packet!, Source); var nonce = await I.Initiator.AdvanceAsync(commit.Body);
            if (nonce.Packet is null) throw new Exception("Commit not accepted: " + nonce.Code);
            var reveal = await R.Responder.HandleAsync(nonce.Packet, Source); var ready = await I.Initiator.AdvanceAsync(reveal.Body);
            if (ready.Code != "awaiting_user") throw new Exception("Reveal not accepted: " + ready.Code);
            PairingWire.TryParse(nonce.Packet, out var n); PairingWire.TryParse(reveal.Body, out var r);
            V2Format.TryDecodeNonce22(n!.SessionId, out var sid); V2Format.TryDecodeNonce22(n.Nonce, out var na); V2Format.TryDecodeNonce22(r!.Nonce, out var nb);
            Transcript = PairingCrypto.Transcript(sid, I.Identity.PublicKey, R.Identity.PublicKey, na, nb);
        }
        public async ValueTask DisposeAsync() { await I.DisposeAsync(); await R.DisposeAsync(); }
    }
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-p1-initiator-" + Guid.NewGuid().ToString("N"));
        internal ChatStore Store { get; }
        internal PairingIdentity Identity { get; }
        internal PairingResponder Responder { get; }
        internal PairingInitiator Initiator { get; }
        internal Faults Faults { get; } = new();
        internal Node(Clock clock)
        {
            Store = ChatStore.Open(new(_root) { Faults = Faults }); var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); byte[] spki = key.ExportSubjectPublicKeyInfo();
            Store.EnsureLocalIdentity(V2Crypto.PeerId(spki), spki, "fixture"); Identity = new(Store, key); Responder = new(Identity, clock, false); Initiator = new(Identity, clock, false);
        }
        private SqliteConnection Connection() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); return c; }
        internal long Number(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        internal void Execute(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        public async ValueTask DisposeAsync() { await Initiator.DisposeAsync(); await Responder.DisposeAsync(); Identity.Dispose(); Store.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Read;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { }
        public void AfterRead(string op) => Read?.Invoke(op);
        public void AfterCommit(string op) { }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
}
