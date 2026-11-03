using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingCandidateChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Pairing candidate: " + name); }
        using (var f = new Fixture())
        {
            var before = f.Store.GetPeerSecuritySnapshot(f.PeerId); var result = f.Store.BeginPairingCandidateIfCurrent(before, f.Public, () => true);
            Check(result is { Ready: true, Current.Pairing: PairingState.Pending } && result.Current.KeySha256 == Convert.ToHexString(SHA256.HashData(f.Public)).ToLowerInvariant(), "unknown peer becomes pinned pending candidate, never paired");
            Check(f.Number("SELECT count(*) FROM Peer") == 1 && f.Number("SELECT count(*) FROM Conversation") == 1 && f.Number("SELECT count(*) FROM Message") == 0 && f.Number("SELECT count(*) FROM ManualSendAttempt") == 0, "candidate creates only peer/conversation metadata, no message/attempt/history");
            Check(f.Store.BeginPairingCandidateIfCurrent(before, f.Public, () => true) is { Ready: false, Reason: "stale-security" }, "original absent-peer snapshot cannot be reused after creation");
            var fresh = f.Store.BeginPairingCandidateIfCurrent(result.Current, f.Public, () => true);
            Check(fresh.Ready && fresh.Current == result.Current, "same pending pin readiness does not churn security revisions");
        }
        foreach (PairingState state in Enum.GetValues<PairingState>())
        {
            using var f = new Fixture(); f.Store.RegisterPeer(f.PeerId, state, f.Public); var before = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            var result = f.Store.BeginPairingCandidateIfCurrent(before, f.Public, () => true);
            bool allowed = state is PairingState.Unpaired or PairingState.Pending or PairingState.Paired;
            Check(result.Ready == allowed && result.Current == before && f.Store.GetPeerSecuritySnapshot(f.PeerId) == before, "existing " + state + " eligibility checked without changing established trust");
        }
        foreach (PairingState state in new[] { PairingState.Suspended, PairingState.Revoked })
        {
            using var f = new Fixture(); f.Store.RegisterPeer(f.PeerId, PairingState.Unpaired); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            f.Store.SetPairingState(f.PeerId, state);
            Check(f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true) is { Ready: false, Reason: "stale-security" } && f.Store.GetPeerSecuritySnapshot(f.PeerId) is { KeySha256: null } && f.Store.GetPeerSecuritySnapshot(f.PeerId).Pairing == state,
                "revocation/suspension between initial inspection and candidate creation cannot be captured as consent: " + state);
        }
        using (var f = new Fixture())
        {
            f.Store.RegisterPeer(f.PeerId, PairingState.Unpaired); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            f.Store.SetPairingState(f.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.PeerId, PairingState.Unpaired);
            Check(!f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true).Ready && f.Store.GetPeerSecuritySnapshot(f.PeerId).KeySha256 is null, "revoke→unpaired ABA invalidates the old candidate intent");
        }
        using (var f = new Fixture())
        {
            var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            f.Execute("UPDATE LocalIdentity SET credentialRef='temporary'; UPDATE LocalIdentity SET credentialRef='fixture-key'");
            Check(!f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true).Ready && f.Number("SELECT count(*) FROM Peer") == 0, "local credential reference ABA cannot seed a candidate");
        }
        using (var f = new Fixture())
        {
            f.Store.RegisterPeer(f.PeerId, PairingState.Paired); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            var result = f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true);
            Check(result.Ready && result.Current.Pairing == PairingState.Paired && result.Current.KeySha256 is not null && result.Current.PeerRevision > expected.PeerRevision, "first pin on existing peer is atomic and retains explicit pairing state");
        }
        using (var f = new Fixture())
        {
            using var different = ECDsa.Create(ECCurve.NamedCurves.nistP256); f.Store.RegisterPeer(f.PeerId, PairingState.Pending, different.ExportSubjectPublicKeyInfo()); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            Check(f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true) is { Ready: false, Reason: "key-mismatch" } && f.Store.GetPeerSecuritySnapshot(f.PeerId) == expected, "candidate cannot overwrite an existing conflicting immutable pin");
        }
        using (var f = new Fixture())
        {
            var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            Check(!f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => false).Ready && f.Number("SELECT count(*) FROM Peer") == 0, "initial session cancellation creates no rows");
            bool live = true; f.Faults.Before = op => { if (op == "pair-candidate") live = false; };
            Check(!f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => live).Ready && f.Number("SELECT count(*) FROM Peer") == 0 && f.Number("SELECT count(*) FROM Conversation") == 0, "final in-transaction cancellation rolls back candidate and conversation");
        }
        using (var f = new Fixture())
        {
            f.Faults.Before = op => { if (op == "pair-candidate") throw new IOException("fixture failed write"); }; bool rejected = false;
            try { f.Store.BeginPairingCandidateIfCurrent(f.Store.GetPeerSecuritySnapshot(f.PeerId), f.Public, () => true); } catch (IOException) { rejected = true; }
            Check(rejected && f.Number("SELECT count(*) FROM Peer") == 0 && f.Number("SELECT count(*) FROM Conversation") == 0, "I/O fault before candidate commit leaves no half-created rows");
        }
        using (var f = new Fixture())
        {
            var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId); bool rejected = false;
            try { f.Store.BeginPairingCandidateIfCurrent(expected, new byte[91], () => true); } catch (ArgumentException) { rejected = true; }
            Check(rejected && f.Number("SELECT count(*) FROM Peer") == 0, "noncanonical SPKI rejected before database writes");
            using var different = ECDsa.Create(ECCurve.NamedCurves.nistP256); rejected = false;
            try { f.Store.BeginPairingCandidateIfCurrent(expected, different.ExportSubjectPublicKeyInfo(), () => true); } catch (ArgumentException) { rejected = true; }
            Check(rejected && f.Number("SELECT count(*) FROM Peer") == 0, "PeerId must derive from exact candidate public key");
            Check(!f.Store.BeginPairingCandidateIfCurrent(f.Store.GetPeerSecuritySnapshot(f.LocalId), f.LocalPublic, () => true).Ready && f.Number("SELECT count(*) FROM Peer") == 0, "self-pairing cannot create a candidate");
        }
        foreach (string mode in new[] { "empty", "legacy", "id-mismatch" })
        {
            using var f = new Fixture(local: false);
            if (mode == "legacy") f.Store.EnsureLocalIdentity("legacy");
            if (mode == "id-mismatch") f.Store.EnsureLocalIdentity("not-derived", f.LocalPublic, "fixture-key");
            Check(f.Store.BeginPairingCandidateIfCurrent(f.Store.GetPeerSecuritySnapshot(f.PeerId), f.Public, () => true) is { Ready: false, Reason: "identity-not-provisioned" } && f.Number("SELECT count(*) FROM Peer") == 0, "invalid local identity cannot begin P1: " + mode);
        }
        using (var f = new Fixture())
        {
            using var other = ChatStore.Open(f.Options); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId);
            using var start = new Barrier(2);
            async Task<PairingCandidateResult> Begin(ChatStore store) => await Task.Run(() => { if (!start.SignalAndWait(TimeSpan.FromSeconds(5))) throw new Exception("Candidate race barrier."); return store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true); });
            var results = await Task.WhenAll(Begin(f.Store), Begin(other));
            Check(results.Count(r => r.Ready) == 1 && results.Count(r => !r.Ready && r.Reason == "stale-security") == 1 && f.Number("SELECT count(*) FROM Peer") == 1 && f.Number("SELECT count(*) FROM Conversation") == 1, "two actual SQLite handles serialize capture and create exactly one candidate");
        }
        using (var f = new Fixture())
        {
            f.Store.RegisterPeer(f.PeerId, PairingState.Pending); f.Execute("UPDATE PeerSecurityRevision SET revision=9223372036854775807"); var expected = f.Store.GetPeerSecuritySnapshot(f.PeerId); bool failed = false;
            try { f.Store.BeginPairingCandidateIfCurrent(expected, f.Public, () => true); } catch (StoreException) { failed = true; }
            Check(failed && f.Store.GetPeerSecuritySnapshot(f.PeerId) == expected && f.Store.GetPeerVerificationContext(f.PeerId).PeerSpkiBase64 is null, "exhausted revision counter rolls back first pin");
        }
        Console.WriteLine($"Conditional P1 candidates: {checks} actual SQLite key/state/ABA/cancel/race checks; candidate preparation never grants trust."); return checks;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-pair-candidate-" + Guid.NewGuid().ToString("N"));
        private readonly ECDsa _local = ECDsa.Create(ECCurve.NamedCurves.nistP256), _peer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal byte[] LocalPublic => _local.ExportSubjectPublicKeyInfo();
        internal string LocalId => V2Crypto.PeerId(LocalPublic);
        internal byte[] Public => _peer.ExportSubjectPublicKeyInfo();
        internal string PeerId => V2Crypto.PeerId(Public);
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(Root) { Faults = Faults };
        internal ChatStore Store { get; }
        internal Fixture(bool local = true) { Store = ChatStore.Open(Options); if (local) Store.EnsureLocalIdentity(LocalId, LocalPublic, "fixture-key"); }
        private SqliteConnection Connection() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); connection.Open(); return connection; }
        internal long Number(string sql) { using var connection = Connection(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        internal void Execute(string sql) { using var connection = Connection(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public void Dispose() { Store.Dispose(); _local.Dispose(); _peer.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
