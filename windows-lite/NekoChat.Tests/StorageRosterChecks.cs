using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageRosterChecks
{
    private const string Mode = "--storage-roster-child";
    private const string SwapCsv = "student_number,name,ip\n1,Updated One,10.0.0.2\n2,Updated Two,10.0.0.1\n3,Third,10.0.0.3";
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != Mode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after")) throw new ArgumentException("Invalid roster fixture.");
        var faults = new Faults();
        void Stop(string op)
        {
            if (op != "apply-roster") return;
            Console.WriteLine("READY:" + args[2]); Console.Out.Flush();
            using var gate = new ManualResetEventSlim(); gate.Wait(TimeSpan.FromSeconds(15)); throw new TimeoutException("Roster fixture not stopped.");
        }
        if (args[2] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        store.ApplyRosterPreview(Preview(store, SwapCsv)); return true;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage roster #{checks}: {name}"); }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            Check(store.GetRosterSnapshot().Count == 0, "new roster empty");
            var first = Add(store, new(" One ", " 01 ", true, " 10.0.0.1 "));
            Check(first.Changed && Guid.TryParseExact(first.EntryId, "D", out _) && store.GetRosterSnapshot().Single().Entry is { DisplayName: "One", StudentNumber: "01", IsTeacher: true, PeerId: null }, "manual labels normalized and new id allocated without binding");
            Check(Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM Conversation") == 0, "manual roster does not create peers or conversations");
            Check(Add(store, new("Two", "1")).Reason == "duplicate-student-number" && Add(store, new("Two", "2", false, "10.0.0.1")).Reason == "duplicate-endpoint", "canonical school number and endpoint unique");
            Check(Add(store, new("\u0001bad")).Reason == "invalid-name" && Add(store, new("Two", "2a")).Reason == "invalid-student-number" && Add(store, new("Two", null, false, "127.0.0.1")).Reason == "invalid-endpoint", "manual invalid fields rejected without content in errors");
            var stale = store.SaveManualRosterEntry(first.EntryId, new("Changed"), new string('0', 64));
            Check(stale.Reason == "roster_changed" && store.GetRosterSnapshot().Single().Entry.DisplayName == "One", "manual edit requires current snapshot");
            store.EnsureLocalIdentity("Local"); store.RegisterPeer("A", PairingState.Unpaired);
            Check(store.BindPairedRosterPeer(first.EntryId!, "A", Token(store)).Reason == "peer-not-paired", "candidate alone cannot bind");
            store.SetPairingState("A", PairingState.Paired); var bound = store.BindPairedRosterPeer(first.EntryId!, "A", Token(store));
            Check(bound.Changed && store.GetRosterSnapshot().Single().Entry.PeerId == "A", "explicit paired selection binds");
            var message = (Stored)store.ReceiveVerifiedMessage(new("A", "history", "Local", "keep history"));
            string conversation = store.ListMessages(((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId).Items.Single().ConversationId;
            Check(store.SaveManualRosterEntry(first.EntryId, new("Edited", "01", false, "10.0.0.2"), Token(store)).Changed && store.GetRosterSnapshot().Single().Entry.PeerId == "A" && store.GetUnreadCount(conversation) == 1, "manual edit preserves binding and conversation");
            Check(store.DeleteRosterEntry(first.EntryId!, Token(store)).Changed && store.GetRosterSnapshot().Count == 0 && Number(f.Db, "SELECT count(*) FROM Peer") == 1 && store.ListMessages(conversation).Items.Single().LocalSeq == message.LocalSeq,
                "deleting roster label preserves peer, conversation and message");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store); string oldToken = Token(store); var preview = Preview(store, SwapCsv);
            Check(Token(store) == oldToken && !Directory.Exists(Path.Combine(f.Data, "backup")), "preview only, no writes or backup");
            var result = store.ApplyRosterPreview(preview);
            Check(result is { Applied: true, Added: 1, Updated: 2, Skipped: 0 }, "accepted swap and add commit together");
            var rows = store.GetRosterSnapshot();
            Check(rows.Single(r => r.Entry.StudentNumberKey == "1").Entry is { Endpoint: "10.0.0.2", PeerId: "A" } &&
                rows.Single(r => r.Entry.StudentNumberKey == "2").Entry.Endpoint == "10.0.0.1" && rows.Single(r => r.Entry.StudentNumberKey == "3").Entry.PeerId is null,
                "two-phase endpoint swap preserves old binding and leaves added row unbound");
            Check(rows.All(r => r.Source == "csv") && rows.Select(r => r.ImportBatchId).Distinct().Count() == 1 && Guid.TryParseExact(rows[0].ImportBatchId, "D", out _), "provenance batch shared by actual changes");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && Number(f.Db, "SELECT count(*) FROM Peer") == 1 && Number(f.Db, "SELECT count(*) FROM Conversation") == 1, "CSV never creates peer or moves history");
            string backup = Directory.GetFiles(Path.Combine(f.Data, "backup")).Single();
            Check(Number(backup, "SELECT count(*) FROM RosterEntry") == 2 && Text(backup, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='1'") == "10.0.0.1" && Number(backup, "SELECT count(*) FROM Message") == 1 &&
                Number(backup, "PRAGMA user_version") == 15, "real pre-import backup contains original roster and existing chat");
            Check(Text(backup, "PRAGMA journal_mode") == "delete" && !File.Exists(backup + "-wal") && !File.Exists(backup + "-shm"), "finalized backup remains standalone after read-only inspection");
            Check(store.ApplyRosterPreview(preview).Reason == "roster_changed" && Directory.GetFiles(Path.Combine(f.Data, "backup")).Length == 1, "stale preview refuses before another backup");
            Check(store.ApplyRosterPreview(Preview(store, SwapCsv)) is { Applied: true, Reason: "no-changes", Unchanged: 3 } && Directory.GetFiles(Path.Combine(f.Data, "backup")).Length == 1, "no-op import makes no backup or provenance rewrite");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store);
            var partial = Preview(store, "student_number,name,ip\n1,Conflict,10.0.0.2\n2,\u0001invalid,10.0.0.1\n3,Valid,10.0.0.3");
            Check(partial.Items.Count(i => i.Action == RosterImportAction.Error) == 2 && partial.Changes.Count == 1, "failed row still owns endpoint; conflict fixed point excludes dependent change");
            // CM-03 (engineering default): an address collision blocks the whole apply; nothing is partially accepted.
            Check(!partial.CanApply && store.ApplyRosterPreview(partial) is { Applied: false, Reason: "ip_conflict" } && Text(f.Db, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='1'") == "10.0.0.1" &&
                Text(f.Db, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='2'") == "10.0.0.2" && Number(f.Db, "SELECT count(*) FROM RosterEntry") == 2, "address conflict blocks apply and changes nothing");
            var invalidOnly = Preview(store, "student_number,name,ip\n2,\u0001invalid,10.0.0.2\n3,Valid,10.0.0.3");
            Check(invalidOnly.CanApply && store.ApplyRosterPreview(invalidOnly) is { Applied: true, Added: 1, Updated: 0, Skipped: 1 } &&
                Text(f.Db, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='2'") == "10.0.0.2", "non-address errors still skip only that row and never clear its endpoint");
            Add(store, new("Same")); Add(store, new("Same"));
            var ambiguous = Preview(store, "student_number,name\n,Same");
            Check(store.ApplyRosterPreview(ambiguous) is { Applied: true, Reason: "no-changes", Skipped: 1 }, "ambiguous row skipped by default");
            var choice = RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name\n,Same")), [new(2, null)]);
            Check(store.ApplyRosterPreview(choice).Added == 1 && store.GetRosterSnapshot().Count(r => r.Entry.DisplayName == "Same") == 3, "explicit add choice does not steal an existing peer binding");
        }
        foreach (string update in new[] { "id='11111111-1111-4111-8111-111111111111'", "displayName='Changed'", "studentNumber='001'", "isTeacher=1", "endpoint='10.0.0.9'", "source='other'", "importBatchId='changed'", "peerId=NULL" })
        {
            using var f = new Fixture(); using var store = ChatStore.Open(f.Options); Seed(store); var preview = Preview(store, SwapCsv);
            Execute(f.Db, "UPDATE RosterEntry SET " + update + " WHERE studentNumberKey='1'"); string changed = Token(store);
            Check(store.ApplyRosterPreview(preview).Reason == "roster_changed" && Token(store) == changed && !Directory.Exists(Path.Combine(f.Data, "backup")), "any stored field change invalidates preview without side effects");
        }
        foreach (bool delete in new[] { false, true })
        {
            using var f = new Fixture(); using var store = ChatStore.Open(f.Options); Seed(store); var preview = Preview(store, SwapCsv);
            if (delete) store.DeleteRosterEntry(store.GetRosterSnapshot()[0].Entry.Id, Token(store)); else Add(store, new("Added meanwhile", "9"));
            string changed = Token(store);
            Check(store.ApplyRosterPreview(preview).Reason == "roster_changed" && Token(store) == changed, "add or delete invalidates whole-roster preview");
        }
        using (var f = new Fixture())
        {
            string[] excluded = []; using var store = ChatStore.Open(f.Options with { RosterExcludedEndpoints = () => excluded }); Seed(store);
            var preview = Preview(store, SwapCsv); excluded = ["10.0.0.3"];
            Check(store.ApplyRosterPreview(preview).Reason == "endpoint-or-row-no-longer-valid" && Number(f.Db, "SELECT count(*) FROM RosterEntry") == 2 && !Directory.Exists(Path.Combine(f.Data, "backup")), "current local/broadcast exclusions rechecked when applying");
            Check(Add(store, new("Self", null, false, "10.0.0.3")).Reason == "invalid-endpoint", "manual endpoint honors same adapter exclusions");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); Seed(store); string before = Token(store);
            bool hidden = false;
            faults.Before = op => { if (op == "apply-roster") { hidden = Text(f.Db, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='1'") == "10.0.0.1"; throw new IOException("injected rollback"); } };
            bool failed = false; try { store.ApplyRosterPreview(Preview(store, SwapCsv)); } catch (IOException) { failed = true; } faults.Before = null;
            Check(failed && hidden && Token(store) == before && Number(f.Db, "SELECT count(*) FROM RosterEntry") == 2, "intermediate null/endpoints invisible and failed import rolls back all rows");
            Check(Directory.GetFiles(Path.Combine(f.Data, "backup")).Length == 1, "rollback keeps a readable pre-import backup");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store); string before = Token(store); File.WriteAllText(Path.Combine(f.Data, "backup"), "protected sentinel");
            StoreException? error = null; try { store.ApplyRosterPreview(Preview(store, SwapCsv)); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.CannotOpen && Token(store) == before && File.ReadAllText(Path.Combine(f.Data, "backup")) == "protected sentinel", "backup failure aborts import without overwriting existing file");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store); string directory = Path.Combine(f.Data, "backup"); Directory.CreateDirectory(directory);
            string collision = Path.Combine(directory, "nekochat-roster-20260102T030405Z.db"); File.WriteAllText(collision, "preserve-existing");
            Check(store.ApplyRosterPreview(Preview(store, SwapCsv)).Applied && File.ReadAllText(collision) == "preserve-existing" &&
                Number(Directory.GetFiles(directory).Single(p => p != collision), "SELECT count(*) FROM RosterEntry") == 2, "backup reservation never overwrites a same-second collision");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store); string before = Token(store), external = Path.Combine(f.Data, "external"); Directory.CreateDirectory(external);
            Directory.CreateSymbolicLink(Path.Combine(f.Data, "backup"), external);
            StoreException? error = null; try { store.ApplyRosterPreview(Preview(store, SwapCsv)); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.CannotOpen && Token(store) == before && Directory.GetFiles(external).Length == 0, "linked backup root rejected without writing through link");
        }
        using (var f = new Fixture())
        {
            string? forced = null; int allocations = 0;
            using var store = ChatStore.Open(f.Options with { NewId = () => { allocations++; return forced ?? Guid.NewGuid().ToString("D"); } }); Seed(store);
            var preview = Preview(store, SwapCsv); string before = Token(store); forced = store.GetRosterSnapshot()[0].Entry.Id;
            int initial = allocations; bool failed = false; try { store.ApplyRosterPreview(preview); } catch (InvalidOperationException) { failed = true; }
            Check(failed && allocations - initial == 4 && Token(store) == before && Number(f.Db, "SELECT count(*) FROM RosterEntry") == 2,
                "three colliding row ids after batch allocation roll back endpoint changes and provenance");
            initial = allocations; failed = false; try { Add(store, new("Fresh", "4")); } catch (InvalidOperationException) { failed = true; }
            Check(failed && allocations - initial == 3 && Token(store) == before, "manual allocation collision bounded without changing roster");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Seed(store); using var other = ChatStore.Open(f.Options);
            var preview = Preview(store, SwapCsv); using var start = new ManualResetEventSlim();
            var work = new[] { store, other }.Select(s => Task.Factory.StartNew(() => { start.Wait(TimeSpan.FromSeconds(8)); return s.ApplyRosterPreview(preview); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.Set(); var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12));
            Check(results.Count(r => r.Applied) == 1 && results.Count(r => r.Reason == "roster_changed") == 1 && Directory.GetFiles(Path.Combine(f.Data, "backup")).Length == 1, "two writer handles cannot both apply the same snapshot");
        }
        int children = 0;
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); using (var setup = ChatStore.Open(f.Options)) Seed(setup);
            using var child = StartChild(f.Data, stage); children++;
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY:" + stage, "import child reaches product commit boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            await Task.Delay(100);
            using var recovered = ChatStore.Open(f.Options); bool after = stage == "after";
            Check(recovered.GetRosterSnapshot().Count == (after ? 3 : 2) && Text(f.Db, "SELECT endpoint FROM RosterEntry WHERE studentNumberKey='1'") == (after ? "10.0.0.2" : "10.0.0.1") &&
                Text(f.Db, "SELECT peerId FROM RosterEntry WHERE studentNumberKey='1'") == "A" && Number(f.Db, "SELECT count(*) FROM Message") == 1, "kill commits whole import or none, preserving binding and chat");
            Check(Number(Directory.GetFiles(Path.Combine(f.Data, "backup")).Single(), "SELECT count(*) FROM RosterEntry") == 2, "pre-import backup survives both kill positions");
        }
        Check(children == 2, "exactly two bounded roster children");
        Console.WriteLine($"Storage roster: {checks} manual, snapshot, import, backup and crash checks passed; 2 killed children."); return checks;
    }

    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static RosterMutationResult Add(ChatStore store, RosterEdit edit) => store.SaveManualRosterEntry(null, edit, Token(store));
    private static RosterImportPreview Preview(ChatStore store, string csv) => RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes(csv)));
    private static void Seed(ChatStore store)
    {
        store.EnsureLocalIdentity("Local"); store.RegisterPeer("A", PairingState.Paired); store.ReceiveVerifiedMessage(new("A", "history", "Local", "protected chat"));
        string one = Add(store, new("One", "01", false, "10.0.0.1")).EntryId!; Add(store, new("Two", "2", false, "10.0.0.2"));
        store.BindPairedRosterPeer(one, "A", Token(store));
    }
    private static object? Query(string db, string sql)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
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
        return finalCmd.ExecuteScalar();
    }
    private static long Number(string db, string sql) => Convert.ToInt64(Query(db, sql));
    private static string Text(string db, string sql) => Convert.ToString(Query(db, sql)) ?? "";
    private static void Execute(string db, string sql)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + db + ";Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
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
        using var finalConn = new SqliteConnection("Data Source=" + db + ";Pooling=False");
        finalConn.Open();
        using var finalCmd = finalConn.CreateCommand();
        finalCmd.CommandText = sql;
        finalCmd.ExecuteNonQuery();
    }
    private static Process StartChild(string data, string stage)
    {
        string exe = Environment.ProcessPath!; var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(StorageRosterChecks).Assembly.Location);
        foreach (string arg in new[] { Mode, data, stage }) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Roster fixture failed to start.");
    }
    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-roster-" + Guid.NewGuid().ToString("N")); internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
