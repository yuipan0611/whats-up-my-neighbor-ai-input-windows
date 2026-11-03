using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>
/// Real process kills at the exact transaction boundaries of the other V2 store paths that V2CrashChecks (receive) does not cover:
/// receipt application (apply-receipt) and ACK completion of a send (complete-attempt).
/// Selected from Tom fd562e9 per PMCM; the superseded pairing scenario remains only in the original branch.
/// The child opens a real SQLite store, builds its state with the public store API, stops at BeforeCommit or AfterCommit and waits to be killed;
/// the parent proves the on-disk state, reopens the product store and proves that replaying the same operation converges without duplicates.
/// Waits are bounded only by a generous hard cap (they end as soon as the child reaches the boundary); nothing sleeps for a fixed time.
/// </summary>
internal static class V2CrashMoreChecks
{
    internal const string ChildMode = "--v2-crash-store";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private sealed class Faults : IStoreFaults
    {
        internal string Op = ""; internal string Stage = ""; internal Action? Hit;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { if (op == Op && Stage == "before") Hit?.Invoke(); }
        public void AfterCommit(string op) { if (op == Op && Stage == "after") Hit?.Invoke(); }
    }
    private static string OpFor(string scenario) => scenario switch { "apply-receipt" => "apply-receipt", "complete-attempt" => "complete-attempt", _ => throw new ArgumentException("scenario") };

    public static async Task<bool> RunChild(string[] args)
    {
        if (args.Length != 4 || args[0] != ChildMode) return false;
        string scenario = args[1], data = args[2], stage = args[3];
        if (stage is not ("before" or "after")) throw new ArgumentException("stage");
        var faults = new Faults { Op = OpFor(scenario), Stage = stage };
        faults.Hit = () => { Console.WriteLine("BOUNDARY:" + scenario + ":" + stage); Console.Out.Flush(); using var wait = new ManualResetEventSlim(false); wait.Wait(TimeSpan.FromSeconds(120)); throw new TimeoutException("Parent did not kill the crash fixture."); };
        using var localKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var peerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] localSpki = localKey.ExportSubjectPublicKeyInfo(), peerSpki = peerKey.ExportSubjectPublicKeyInfo();
        string local = V2Crypto.PeerId(localSpki), peer = V2Crypto.PeerId(peerSpki);
        var options = new ChatStoreOptions(data) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None };
        using var s = ChatStore.Open(options with { Faults = faults });
        s.EnsureLocalIdentity(local, localSpki, "synthetic-crash-key");
        var registered = (PeerRegistered)s.RegisterPeer(peer, PairingState.Paired, peerSpki); string conversation = registered.Conversation.ConversationId;
        string raw = "crash " + scenario;
        s.SaveDraft(conversation, raw, 0, 1);
        var attempt = (AttemptStarted)s.BeginManualSend(conversation, raw, 0, 1); var prepared = s.GetPreparedSend(attempt.MessageId)!;
        var snap = s.GetPeerSecuritySnapshot(peer);
        if (scenario == "complete-attempt")
        {
            s.CompleteV2Send(new V2VerifiedAck("stored", prepared.MessageId, local, peer, prepared.FingerprintHex), snap, () => true);
            return true;
        }
        // apply-receipt: the outgoing message must already be confirmed; arm the fault only for the receipt operation (it is a different op name, so no extra arming needed)
        s.CompleteAttempt(prepared.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, Convert.FromHexString(prepared.FingerprintHex)), prepared.TryCount);
        s.ApplyV2Receipt(new V2VerifiedReceipt(prepared.MessageId, peer, local, "AAAAAAAAAAAAAAAAAAAAAA"), s.GetPeerSecuritySnapshot(peer), () => true);
        return await Task.FromResult(true);
    }

    public static async Task<int> Run()
    {
        int checks = 0, killed = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("V2 crash (more): " + name); }
        foreach (string scenario in new[] { "apply-receipt", "complete-attempt" })
            foreach (string stage in new[] { "before", "after" })
            {
                string root = Path.Combine(Path.GetTempPath(), "neko-v2-crash-more-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    using var child = Start(scenario, root, stage); Task<string> stderr = child.StandardError.ReadToEndAsync();
                    try
                    {
                        string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(Patience);
                        Check(line == "BOUNDARY:" + scenario + ":" + stage, $"{scenario}/{stage}: child reaches the exact transaction boundary (got {line})");
                        child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(Patience); killed++;
                        Check(child.ExitCode != 0 && (await stderr).Length == 0, $"{scenario}/{stage}: child is truly killed and printed only the boundary marker");
                    }
                    finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(Patience); } _ = await stderr; }

                    string db = Path.Combine(root, "nekochat.db"); bool committed = stage == "after";
                    switch (scenario)
                    {
                        case "apply-receipt":
                            Check(Count(db, "Message") == 1 && Count(db, "Message WHERE remoteReadAt IS NOT NULL") == (committed ? 1 : 0), $"{scenario}/{stage}: on-disk read state matches the boundary");
                            using (var store = ChatStore.Open(new(root) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None }))
                            {
                                string id = Text(db, "SELECT messageId FROM Message")!, peer = Text(db, "SELECT id FROM Peer")!, local = Text(db, "SELECT id FROM LocalIdentity")!;
                                var first = store.ApplyV2Receipt(new V2VerifiedReceipt(id, peer, local, "AAAAAAAAAAAAAAAAAAAAAA"), store.GetPeerSecuritySnapshot(peer), () => true);
                                Check(first.Changed == !committed, $"{scenario}/{stage}: replay applies exactly when the first attempt did not commit");
                                var second = store.ApplyV2Receipt(new V2VerifiedReceipt(id, peer, local, "AAAAAAAAAAAAAAAAAAAAAA"), store.GetPeerSecuritySnapshot(peer), () => true);
                                Check(!second.Changed && Count(db, "Message") == 1 && Count(db, "Message WHERE remoteReadAt IS NOT NULL") == 1, $"{scenario}/{stage}: further replays are idempotent, one message, read state set once");
                            }
                            break;
                        case "complete-attempt":
                            Check(Count(db, "Message") == (committed ? 1 : 0), $"{scenario}/{stage}: confirmed history exists only if the completion committed");
                            using (var store = ChatStore.Open(new(root) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None }))
                            {
                                string peer = Text(db, "SELECT id FROM Peer")!, local = Text(db, "SELECT id FROM LocalIdentity")!;
                                // Reopening demotes an interrupted 'sending' attempt to 'unconfirmed' (crash recovery), so a late verified ACK is what completes it.
                                string messageId = Text(db, "SELECT messageId FROM ManualSendAttempt")!, fingerprint = Text(db, "SELECT lower(hex(contentFingerprint)) FROM ManualSendAttempt")!;
                                var again = store.CompleteV2Send(new V2VerifiedAck("stored", messageId, local, peer, fingerprint), store.GetPeerSecuritySnapshot(peer), () => true);
                                Check((committed ? again is AlreadyConfirmed : again is Confirmed) && Count(db, "Message") == 1, $"{scenario}/{stage}: replaying the same verified ACK converges to exactly one confirmed message");
                                var third = store.CompleteV2Send(new V2VerifiedAck("duplicate", messageId, local, peer, fingerprint), store.GetPeerSecuritySnapshot(peer), () => true);
                                Check(Count(db, "Message") == 1 && third is AlreadyConfirmed, $"{scenario}/{stage}: a later duplicate ACK adds nothing");
                            }
                            break;
                    }
                }
                finally { try { Directory.Delete(root, true); } catch (IOException) { } }
            }
        Check(killed == 4, "four receipt/send-completion store processes were really killed");
        Console.WriteLine($"Product V2 store crashes: {checks} checks; {killed} killed processes (apply-receipt, complete-attempt x before/after)."); return checks;
    }

    private static Process Start(string scenario, string data, string stage)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(V2CrashMoreChecks).Assembly.Location);
        info.ArgumentList.Add(ChildMode); info.ArgumentList.Add(scenario); info.ArgumentList.Add(data); info.ArgumentList.Add(stage);
        return Process.Start(info) ?? throw new InvalidOperationException("Child did not start.");
    }
    private static object? Query(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static long Count(string db, string table) => Convert.ToInt64(Query(db, "SELECT count(*) FROM " + table));
    private static string? Text(string db, string sql) => Query(db, sql) as string;
}
