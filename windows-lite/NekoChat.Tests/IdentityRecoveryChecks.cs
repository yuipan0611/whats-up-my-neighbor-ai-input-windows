using System.Diagnostics;
using System.Security.Cryptography;
using NekoChat.Core.V2;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;
using NekoChat.Windows.Identity;

namespace NekoChat.Tests;

internal static class IdentityRecoveryChecks
{
    private const string ChildMode = "--identity-recovery-transaction-child";
    internal static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 3) throw new ArgumentException("Invalid recovery transaction fixture.");
        void Checkpoint(string point)
        {
            if (point != args[2]) return;
            Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite);
        }
        using var store = ChatStore.Open(new(Path.Combine(args[1], "data")) { Faults = new KillFaults(Checkpoint) });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        store.RecoverLocalIdentityIfCurrent(store.GetLocalIdentitySnapshot(), V2Crypto.PeerId(publicKey), publicKey, "storage-only-synthetic-credential", () => true);
        throw new Exception("Recovery checkpoint was not reached.");
    }
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Identity recovery: " + name); }
        using (var f = new Fixture(provision: false))
        {
            Check(f.Flow.Inspect() is null && f.Protector.Creates == 0 && !Directory.Exists(f.Keys), "empty identity inspection never provisions");
            f.Store.EnsureLocalIdentity("legacy-without-key");
            Check(f.Flow.Inspect() is null && f.Protector.Creates == 0, "legacy unprovisioned identity cannot choose a policy through recovery");
        }
        using (var f = new Fixture())
        {
            Check(f.Flow.Inspect() is null && f.Protector.Creates == 1, "healthy identity cannot be replaced through this flow");
            f.Lose(); var expected = f.Store.GetLocalIdentitySnapshot(); var prompt = f.Flow.Inspect()!;
            Check(prompt is { IsMismatch: false } && prompt.PreviousIdPrefix == expected.Id![..8] && f.Protector.Creates == 1, "lost inspection exposes only a display prefix and does no writes");
            f.Flow.Cancel();
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 1 && f.Store.GetLocalIdentitySnapshot() == expected, "cancel before confirmation creates no key or DB change");
            var next = f.Flow.Inspect()!;
            Check(f.Flow.Confirm(next, () => false) is IdentityChangeDiscarded && f.Protector.Creates == 1, "lifecycle cancellation before work creates no key");
            Check(f.Flow.Confirm(next, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 1, "consumed confirmation cannot be replayed");
        }
        using (var f = new Fixture())
        {
            f.Lose(); var prompt = f.Flow.Inspect()!; var other = new IdentityRecoveryFlow(f.Store, f.Vault);
            Check(other.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 1, "ticket is scoped to the inspecting flow");
            f.Restore();
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded { Reason: "identity-no-longer-lost" } && f.Protector.Creates == 1, "putting original encrypted key back while dialog is open cancels replacement");
        }
        using (var f = new Fixture())
        {
            f.Lose(); var expected = f.Store.GetLocalIdentitySnapshot(); var prompt = f.Flow.Inspect()!;
            f.Execute("UPDATE LocalIdentity SET credentialRef='temporary-other-reference'; UPDATE LocalIdentity SET credentialRef='" + expected.CredentialRef + "'");
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 1 && f.Store.GetLocalIdentitySnapshot().Id == expected.Id, "captured durable local revision rejects credential-reference ABA");
        }
        using (var f = new Fixture())
        {
            var original = f.Store.GetLocalIdentitySnapshot(); f.SeedData(); f.Lose(); var prompt = f.Flow.Inspect()!;
            var result = f.Flow.Confirm(prompt, () => true);
            Check(result is IdentityReplaced { SupersededAttempts: 1, DroppedReceipts: 1 }, "explicit confirmation retires identity and supersedes old work");
            Check(f.Store.GetLocalIdentitySnapshot().Id != original.Id && new LocalIdentityCoordinator(f.Store, f.Vault).Inspect() is IdentityConsistent, "replacement has a fresh real signing key matching SQLite");
            Check(f.State("A") == PairingState.Suspended && f.State("B") == PairingState.Revoked && f.State("C") == PairingState.Unpaired, "only previously paired peers suspended, newer revocation is retained");
            Check(!f.Store.GetReadReceiptPolicy().Enabled && f.Number("SELECT count(*) FROM ReceiptOutbox") == 0, "recovery atomically resets outbound privacy and drops outbox");
            Check(f.Store.ListMessages(f.Conversation!).Items.Single().RawText == "incoming history" && f.Store.GetDraftState(f.Conversation!)?.RawText == "owned draft", "messages and per-conversation draft preserved");
            Check(f.Number("SELECT count(*) FROM LocalIdentityHistory") == 1 && File.Exists(f.HeldKey) && File.Exists(f.Settings), "retired identity recorded, old encrypted key and settings preserved");
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 2, "successful confirmation is single-use");
            using var reopened = ChatStore.Open(f.Options);
            Check(reopened.GetInboxSnapshot().Entries.Single(e => e.PeerId == "A").Pairing == PairingState.Suspended && new LocalIdentityCoordinator(reopened, f.Vault).Inspect() is IdentityConsistent, "reopen retains recovered identity and suspended trust");
        }
        using (var f = new Fixture())
        {
            using var unrelated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            f.Execute("UPDATE LocalIdentity SET publicKey=X'" + Convert.ToHexString(unrelated.ExportSubjectPublicKeyInfo()) + "'");
            var prompt = f.Flow.Inspect();
            Check(prompt is { IsMismatch: true } && f.Flow.Confirm(prompt, () => true) is IdentityReplaced, "mismatched public key also requires explicit one-use confirmation");
        }
        using (var f = new Fixture())
        {
            f.SeedData(); f.Lose(); var before = f.Store.GetLocalIdentitySnapshot(); var prompt = f.Flow.Inspect()!;
            f.Protector.BeforeCreate = f.Flow.Cancel;
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Store.GetLocalIdentitySnapshot() == before && f.State("A") == PairingState.Paired && f.Store.GetReadReceiptPolicy().Enabled, "cancellation during key encryption cannot alter identity, pairing or privacy");
            Check(f.Protector.Creates == 2 && Directory.GetFiles(f.Keys, "*.dpapi").Length == 1 && File.Exists(f.HeldKey), "canceled new encrypted credential is retained as an orphan, old key not deleted");
        }
        foreach (bool failWithIo in new[] { true, false })
        {
            using var f = new Fixture(); f.SeedData(); f.Lose(); var before = f.Store.GetLocalIdentitySnapshot(); var prompt = f.Flow.Inspect()!; bool live = true, threw = false;
            f.Faults.Before = op => { if (op != "replace-identity") return; if (failWithIo) throw new IOException("fixture commit fault"); live = false; };
            ReplaceIdentityResult? result = null;
            try { result = f.Flow.Confirm(prompt, () => live); } catch (Exception error) when (error is StoreException or IOException) { threw = true; }
            Check(failWithIo ? threw : result is IdentityChangeDiscarded, "failure or cancellation at final commit is rejected");
            Check(f.Store.GetLocalIdentitySnapshot() == before && f.State("A") == PairingState.Paired && f.Store.GetReadReceiptPolicy().Enabled && f.Number("SELECT count(*) FROM ReceiptOutbox") == 1 && f.Number("SELECT count(*) FROM LocalIdentityHistory") == 0 && f.Number("SELECT count(*) FROM ManualSendAttempt WHERE attemptState='sending'") == 1, "entire recovery transaction rolls back, including trust/privacy/attempt/history");
        }
        using (var f = new Fixture())
        {
            f.Lose(); var prompt = f.Flow.Inspect()!; var latest = f.Flow.Inspect()!;
            Check(f.Flow.Confirm(prompt, () => true) is IdentityChangeDiscarded && f.Protector.Creates == 1, "a newer inspection invalidates an old dialog");
            Check(f.Flow.Confirm(latest, () => true) is IdentityReplaced, "only current inspection can confirm");
        }
        using (var f = new Fixture())
        {
            f.SeedData(); f.Lose(); using var second = ChatStore.Open(f.Options); var other = new IdentityRecoveryFlow(second, f.Vault);
            var a = f.Flow.Inspect()!; var b = other.Inspect()!; using var atCreate = new Barrier(2);
            f.Protector.BeforeCreate = () => { if (!atCreate.SignalAndWait(TimeSpan.FromSeconds(5))) throw new Exception("Recovery concurrency barrier timed out."); };
            var outcomes = await Task.WhenAll(Task.Run(() => f.Flow.Confirm(a, () => true)), Task.Run(() => other.Confirm(b, () => true)));
            Check(outcomes.Count(r => r is IdentityReplaced) == 1 && outcomes.Count(r => r is IdentityChangeDiscarded) == 1, "two real SQLite handles with concurrently generated credentials commit exactly one recovery");
            Check(f.Number("SELECT count(*) FROM LocalIdentityHistory") == 1 && f.State("A") == PairingState.Suspended && !f.Store.GetReadReceiptPolicy().Enabled && new LocalIdentityCoordinator(f.Store, f.Vault).Inspect() is IdentityConsistent, "race winner retains one consistent identity and final security state");
        }
        foreach (string point in new[] { "before", "after" })
        {
            using var f = new Fixture(); f.SeedData(); var original = f.Store.GetLocalIdentitySnapshot();
            string executable = Environment.ProcessPath!;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(IdentityRecoveryChecks).Assembly.Location);
            foreach (string arg in new[] { ChildMode, f.Root, point }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new Exception("Recovery child did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { if (await child.StandardOutput.ReadLineAsync(deadline.Token) != "READY") throw new Exception("Recovery child failed: " + await child.StandardError.ReadToEndAsync(deadline.Token)); }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            using var reopened = ChatStore.Open(f.Options); bool committed = point == "after";
            Check((reopened.GetLocalIdentitySnapshot().Id != original.Id) == committed && reopened.GetPeerSecuritySnapshot("A").Pairing == (committed ? PairingState.Suspended : PairingState.Paired) && reopened.GetReadReceiptPolicy().Enabled != committed,
                "real process kill exposes identity, trust and privacy all together at " + point + " COMMIT");
            Check(f.Number("SELECT count(*) FROM LocalIdentityHistory") == (committed ? 1 : 0) && f.Number("SELECT count(*) FROM ReceiptOutbox") == (committed ? 0 : 1) &&
                f.Number("SELECT count(*) FROM ManualSendAttempt WHERE attemptState='superseded'") == (committed ? 1 : 0) && reopened.GetDraftState(f.Conversation!)?.RawText == "owned draft" && reopened.ListMessages(f.Conversation!).Items.Single().RawText == "incoming history",
                "real killed recovery transaction retains history/draft and has no partial attempt/outbox/history state");
        }
        Console.WriteLine($"Identity recovery: {checks} real encrypted-file, signature, SQLite consent/cancellation/rollback/race checks; two real killed storage-transaction children; DPAPI and WPF not executed.");
        return checks;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-recovery-" + Guid.NewGuid().ToString("N"));
        internal string Keys => Path.Combine(Root, "identity");
        internal string Settings => Path.Combine(Root, "settings.json");
        internal string HeldKey => OriginalKey + ".held";
        internal string OriginalKey = "";
        internal string? Conversation;
        internal Protector Protector { get; } = new();
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(Path.Combine(Root, "data")) { Faults = Faults };
        internal ChatStore Store { get; }
        internal IdentityFileVault Vault { get; }
        internal IdentityRecoveryFlow Flow { get; }
        internal Fixture(bool provision = true)
        {
            Directory.CreateDirectory(Root); File.WriteAllText(Settings, "settings sentinel"); Store = ChatStore.Open(Options); Vault = new(Keys, Protector); Flow = new(Store, Vault);
            if (provision) { new LocalIdentityCoordinator(Store, Vault).Provision(Store.GetLocalIdentitySnapshot(), () => true); OriginalKey = Directory.GetFiles(Keys, "*.dpapi").Single(); }
        }
        internal void Lose() => File.Move(OriginalKey, HeldKey);
        internal void Restore() => File.Move(HeldKey, OriginalKey);
        internal PairingState? State(string peer) => Store.GetPeerSecuritySnapshot(peer).Pairing;
        internal void SeedData()
        {
            string local = Store.GetLocalIdentitySnapshot().Id!;
            Conversation = ((PeerRegistered)Store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
            Store.RegisterPeer("B", PairingState.Revoked); Store.RegisterPeer("C", PairingState.Unpaired);
            var incoming = (Stored)Store.ReceiveVerifiedMessage(new("A", "incoming", local, "incoming history"));
            Store.SetReadReceiptPolicy(true); Store.MarkVisibleRead(Conversation, new[] { incoming.LocalSeq }, new(Conversation, true, true, false, true));
            Store.SaveDraft(Conversation, "owned draft", 0, 1); Store.BeginManualSend(Conversation, "owned draft", 0, 1);
        }
        internal void Execute(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        internal long Number(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        private SqliteConnection Connection() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); return c; }
        public void Dispose() { Store.Dispose(); Protector.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Protector : IIdentityProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        internal int Creates;
        internal Action? BeforeCreate;
        public byte[] Protect(byte[] input, byte[] entropy)
        {
            Interlocked.Increment(ref Creates); BeforeCreate?.Invoke();
            byte[] result = new byte[28 + input.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(result.AsSpan(0, 12), input, result.AsSpan(28), result.AsSpan(12, 16), entropy); return result;
        }
        public byte[] Unprotect(byte[] input, byte[] entropy)
        {
            byte[] raw = new byte[input.Length - 28]; using var aes = new AesGcm(_key, 16);
            try { aes.Decrypt(input.AsSpan(0, 12), input.AsSpan(28), input.AsSpan(12, 16), raw, entropy); return raw; }
            catch { CryptographicOperations.ZeroMemory(raw); throw; }
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
    private sealed class KillFaults(Action<string> checkpoint) : IStoreFaults
    {
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { if (op == "replace-identity") checkpoint("before"); }
        public void AfterCommit(string op) { if (op == "replace-identity") checkpoint("after"); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
