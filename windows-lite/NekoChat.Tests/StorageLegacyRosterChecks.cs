using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageLegacyRosterChecks
{
    private const string Mode = "--storage-legacy-roster-child";
    private static readonly LegacyRosterContact[] Valid = [new(" One ", " 10.0.0.1 "), new("Teacher candidate", "10.0.0.9")];
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != Mode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after")) throw new ArgumentException("Invalid legacy roster fixture.");
        var faults = new Faults();
        void Stop(string op)
        {
            if (op != "legacy-roster") return;
            Console.WriteLine("READY:" + args[2]); Console.Out.Flush();
            using var gate = new ManualResetEventSlim(); gate.Wait(TimeSpan.FromSeconds(15)); throw new TimeoutException("Legacy fixture not stopped.");
        }
        if (args[2] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults }); store.ImportLegacyRoster(Valid); return true;
    }
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage legacy roster #{checks}: {name}"); }
        using (var f = new Fixture())
        {
            string settingsHash = Hash(f.Settings);
            using (var store = ChatStore.Open(f.Options with { RosterExcludedEndpoints = () => new[] { "10.0.0.7" } }))
            {
                Check(!store.LegacyRosterImportCompleted(), "fresh marker is absent");
                LegacyRosterContact[] input = [.. Valid, new("Duplicate A", "10.0.0.2"), new("Duplicate B", "10.0.0.2"), new("Loop", "127.0.0.1"), new("\u0001invalid", "10.0.0.4"), new("Self", "10.0.0.7"), null!];
                Check(store.ImportLegacyRoster(input) is { AlreadyCompleted: false, Added: 2, Skipped: 6 } && store.LegacyRosterImportCompleted(), "valid labels imported once; duplicates/invalid/self skipped without picking a winner");
                var rows = store.GetRosterSnapshot();
                Check(rows.Count == 2 && rows.All(r => r.Source == "legacySettings" && r.Entry.PeerId is null && !r.Entry.IsTeacher && r.Entry.StudentNumber is null && r.ImportBatchId is null), "legacy IP/name and teacher-IP candidate grant no binding, role or school number");
                Check(rows.Any(r => r.Entry.DisplayName == "One" && r.Entry.Endpoint == "10.0.0.1") && Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM Conversation") == 0,
                    "normalized labels only, no identity or conversation created");
                store.DeleteRosterEntry(rows[0].Entry.Id, Token(store));
                Check(store.ImportLegacyRoster([.. Valid, new("New legacy entry", "10.0.0.8")]) is { AlreadyCompleted: true, Added: 0, Skipped: 0 } && store.GetRosterSnapshot().Count == 1,
                    "repeated startup never resurrects deleted labels or follows later changes in old settings");
            }
            using var reopened = ChatStore.Open(f.Options);
            Check(reopened.LegacyRosterImportCompleted() && reopened.ImportLegacyRoster(Valid).AlreadyCompleted && reopened.GetRosterSnapshot().Count == 1, "durable marker survives reopen");
            Check(Hash(f.Settings) == settingsHash, "settings bytes untouched");
        }
        using (var f = new Fixture())
        {
            using (var v3 = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1, Migration.V2, Migration.V3 } }))
            {
                v3.EnsureLocalIdentity("Local"); var peer = (PeerRegistered)v3.RegisterPeer("A", PairingState.Paired);
                var message = (Stored)v3.ReceiveVerifiedMessage(new("A", "history", "Local", "protected"));
                v3.SetReadReceiptPolicy(true); v3.MarkVisibleRead(peer.Conversation.ConversationId, [message.LocalSeq], new(peer.Conversation.ConversationId, true, true, false, true));
                string id = v3.SaveManualRosterEntry(null, new("Keep manual label", "01", true, "10.0.0.1"), Token(v3)).EntryId!;
                v3.BindPairedRosterPeer(id, "A", Token(v3));
            }
            using var current = ChatStore.Open(f.Options);
            Check(!current.LegacyRosterImportCompleted() && Number(f.Db, "PRAGMA user_version") == 13, "real v3-to-current migration leaves bootstrap pending");
            Check(current.GetReadReceiptPolicy() is { Enabled: true, Generation: 1 } && current.ListPendingReadReceipts().Single().MessageId == "history",
                "current migration preserves the existing privacy preference and allowed receipt");
            Check(current.ImportLegacyRoster(Valid) is { Added: 1, Skipped: 1 }, "existing endpoint wins without overwrite");
            var old = current.GetRosterSnapshot().Single(r => r.Entry.Endpoint == "10.0.0.1");
            Check(old.Entry is { DisplayName: "Keep manual label", StudentNumber: "01", IsTeacher: true, PeerId: "A" } && old.Source == "manual" && Number(f.Db, "SELECT count(*) FROM Message") == 1,
                "old roster provenance, binding and history preserved");
            string conversation = ((PeerRegistered)current.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
            current.ClearConversation(conversation);
            Check(current.LegacyRosterImportCompleted() && current.GetReadReceiptPolicy().Enabled && current.ListPendingReadReceipts().Count == 0,
                "conversation clear keeps bootstrap marker and preference while removing its receipt");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            Check(store.ImportLegacyRoster([]) is { AlreadyCompleted: false, Added: 0 } && store.ImportLegacyRoster(Valid).AlreadyCompleted && store.GetRosterSnapshot().Count == 0,
                "successful empty source is still a completed one-time bootstrap");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); bool rejected = false;
            try { store.ImportLegacyRoster(Enumerable.Repeat(Valid[0], 2001).ToArray()); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && !store.LegacyRosterImportCompleted() && store.GetRosterSnapshot().Count == 0, "oversized legacy batch rejects before marker or rows");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults });
            faults.Before = op => { if (op == "legacy-roster") throw new IOException("injected before commit"); };
            bool failed = false; try { store.ImportLegacyRoster(Valid); } catch (IOException) { failed = true; } faults.Before = null;
            Check(failed && !store.LegacyRosterImportCompleted() && store.GetRosterSnapshot().Count == 0, "marker and rows roll back together");
            Check(store.ImportLegacyRoster(Valid).Added == 2 && store.LegacyRosterImportCompleted(), "failed bootstrap can retry without duplicates");
        }
        using (var f = new Fixture())
        {
            using var first = ChatStore.Open(f.Options); using var second = ChatStore.Open(f.Options); using var start = new ManualResetEventSlim();
            var work = new[] { first, second }.Select(s => Task.Factory.StartNew(() => { start.Wait(TimeSpan.FromSeconds(8)); return s.ImportLegacyRoster(Valid); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.Set(); var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12));
            Check(results.Sum(r => r.Added) == 2 && results.Count(r => r.AlreadyCompleted) == 1 && first.GetRosterSnapshot().Count == 2, "two handles share one committed bootstrap");
        }
        int children = 0;
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); using (var setup = ChatStore.Open(f.Options)) { }
            using var child = StartChild(f.Data, stage); children++;
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY:" + stage, "bootstrap child reaches product commit boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            await Task.Delay(100);
            using var recovered = ChatStore.Open(f.Options); bool after = stage == "after";
            Check(recovered.LegacyRosterImportCompleted() == after && recovered.GetRosterSnapshot().Count == (after ? 2 : 0), "kill leaves whole bootstrap or none");
            var retry = recovered.ImportLegacyRoster(Valid);
            Check(retry.AlreadyCompleted == after && recovered.GetRosterSnapshot().Count == 2 && recovered.GetRosterSnapshot().All(r => r.Entry.PeerId is null), "recovery retry converges to two unbound labels");
        }
        Check(children == 2, "exactly two bounded legacy children");
        Console.WriteLine($"Storage legacy roster: {checks} bootstrap, preservation and crash checks passed; 2 killed children."); return checks;
    }
    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static long Number(string db, string sql)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqliteException ex) when (attempt < 4 && (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 10))
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        using var finalConn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
        finalConn.Open();
        using var finalCmd = finalConn.CreateCommand();
        finalCmd.CommandText = sql;
        return Convert.ToInt64(finalCmd.ExecuteScalar());
    }
    private static string Hash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }
    private static Process StartChild(string data, string stage)
    {
        string exe = Environment.ProcessPath!; var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(StorageLegacyRosterChecks).Assembly.Location);
        foreach (string arg in new[] { Mode, data, stage }) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Legacy fixture failed.");
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-legacy-roster-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data"); internal string Db => Path.Combine(Data, "nekochat.db"); internal string Settings => Path.Combine(Root, "settings.json");
        internal ChatStoreOptions Options => new(Data);
        internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Settings, "{\"TeacherIp\":\"10.0.0.9\",\"Contacts\":[{\"Ip\":\"10.0.0.1\",\"Name\":\"One\"}]}"); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
