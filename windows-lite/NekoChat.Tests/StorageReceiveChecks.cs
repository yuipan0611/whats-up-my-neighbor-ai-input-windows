using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageReceiveChecks
{
    private const string ChildMode = "--storage-receive-child";
    private const string CrashId = "33333333-3333-4333-8333-333333333333";

    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after" or "concurrent")) throw new ArgumentException("Invalid storage fixture arguments.");
        string stage = args[2];
        var faults = new Faults();
        void Stop(string operation)
        {
            if (operation != "receive") return;
            Console.WriteLine("READY:" + stage); Console.Out.Flush();
            using var wait = new ManualResetEventSlim(false);
            wait.Wait(TimeSpan.FromSeconds(15));
            throw new TimeoutException("Parent did not stop the fixture child.");
        }
        if (stage == "before") faults.Before = Stop; else faults.After = Stop;
        if (stage == "concurrent") faults.After = null;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        if (stage == "concurrent")
        {
            Console.WriteLine("READY:concurrent"); Console.Out.Flush();
            if (Console.ReadLine() != "GO") throw new InvalidOperationException("Missing parent start signal.");
            var result = store.ReceiveVerifiedMessage(new("A", CrashId, "Local", "concurrent-child"));
            Console.WriteLine(result is Stored ? "stored" : result is Duplicate ? "duplicate" : "unexpected");
            return true;
        }
        store.ReceiveVerifiedMessage(new("A", CrashId, "Local", "child-boundary", SourceEndpoint: "10.0.0.1"));
        return true;
    }

    public static async Task<int> Run()
    {
        int checks = 0, childCount = 0;
        void Check(bool condition, string description)
        {
            checks++;
            if (!condition) throw new Exception($"Storage receive #{checks}: {description}");
        }
        Check(Convert.ToHexString(MessageFingerprint.Compute("8PeCzL6WJCM8KuWVMRrzf-HG0cBeJZ4xEJp7FdA7I_s",
            "你好 😀\n```csharp\nint a = 1; // \"引號\" <tag> & 'x'\n```\r\n")).ToLowerInvariant() ==
            "77a2804473a00b1605fde29220bbac0299e2d63a5fd7428632ed0a3c91e0b58c", "fingerprint agrees with frozen N002T-v3 M-01/A-01 protocol vector");
        using (var f = new Fixture())
        {
            int generated = 0;
            string firstId = Guid.NewGuid().ToString("D"), secondId = Guid.NewGuid().ToString("D"), recoveredId = Guid.NewGuid().ToString("D");
            using var store = ChatStore.Open(f.Options with { NewId = () => ++generated switch { 1 => firstId, 2 => secondId, 3 => firstId, _ => recoveredId } });
            Prepare(store); Execute(f.Db, "INSERT INTO Peer(id,pairingState,createdUtc) VALUES('C','paired','t')");
            Check(store.ReceiveVerifiedMessage(new("C", "collision", "Local", "body")) is Stored && generated == 4 &&
                Text(f.Db, "SELECT id FROM Conversation WHERE peerId='C'") == recoveredId && Number(f.Db, "SELECT count(*) FROM Message") == 1,
                "one bounded retry recovers a generated conversation UUID collision without overwriting old conversations");
        }
        using (var f = new Fixture())
        {
            int generated = 0; string firstId = Guid.NewGuid().ToString("D"), secondId = Guid.NewGuid().ToString("D");
            using var store = ChatStore.Open(f.Options with { NewId = () => ++generated == 2 ? secondId : firstId });
            Prepare(store); Execute(f.Db, "INSERT INTO Peer(id,pairingState,createdUtc) VALUES('C','paired','t')");
            StoreException? error = null;
            try { store.ReceiveVerifiedMessage(new("C", "collision", "Local", "body")); } catch (StoreException e) { error = e; }
            Check(error is { Kind: StoreErrorKind.ConstraintViolation } && generated == 4 && Number(f.Db, "SELECT count(*) FROM Conversation WHERE peerId='C'") == 0 &&
                Number(f.Db, "SELECT count(*) FROM Message") == 0, "permanent id collision stops after two attempts and rolls back all new content");
        }
        using (var f = new Fixture())
        {
            DateTime clock = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using var store = ChatStore.Open(f.Options with { UtcNow = () => clock });
            var (a, b) = Prepare(store);
            string body = "你好🐈\r\n  x=1\t";
            var message = new IncomingMessage("A", "m1", "Local", body, Reaction: "unknown-reaction", SourceEndpoint: "10.0.0.1");
            var result = store.ReceiveVerifiedMessage(message);
            Check(result is Stored { LocalSeq: > 0, UnreadCountForConversation: 1 }, "successful receive returns committed sequence and unread count");
            Check(Text(f.Db, "SELECT lower(hex(contentFingerprint)) FROM Message") == "a2f80f1a3f129a854ff30d85531654fa5b0be8dc1b1e559253f21d8ba4e97fdc",
                "content fingerprint matches independent Python golden UTF8/CRLF/reaction fixture");
            Check(Text(f.Db, "SELECT rawText FROM Message") == body && Text(f.Db, "SELECT fingerprintAlg FROM Message") == "nekochat-v2-content",
                "original body and exact fingerprint algorithm retained");
            Check(Number(f.Db, "SELECT count(*) FROM Message WHERE direction='incoming' AND localReadAt IS NULL AND remoteStoredAt IS NULL AND remoteReadAt IS NULL") == 1 &&
                Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0, "receiving neither reads locally nor queues a read receipt");
            Check(Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "appVerified" && Text(f.Db, "SELECT lastSeenEndpoint FROM Peer WHERE id='A'") == "10.0.0.1",
                "insert and first installation evidence commit together");
            string seen = Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'"), installed = Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'");
            clock = clock.AddMinutes(1);
            Check(store.ReceiveVerifiedMessage(message with { SourceEndpoint = "10.0.0.2" }) is Duplicate { LocalSeq: > 0 }, "same sender/id/body is duplicate even from another endpoint");
            Check(Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'") == seen && Text(f.Db, "SELECT lastSeenEndpoint FROM Peer WHERE id='A'") == "10.0.0.1",
                "duplicate never refreshes evidence or endpoint");
            foreach (var changed in new[] { message with { RawText = body + "changed" }, message with { Reaction = "future-reaction" }, message with { Kind = "code" } })
                Check(store.ReceiveVerifiedMessage(changed) is Conflict, "body, reaction or local kind conflict cannot overwrite existing content");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && Text(f.Db, "SELECT rawText FROM Message") == body && store.GetUnreadCount(a) == 1,
                "duplicates and conflicts retain one unread original");
            Check(store.ReceiveVerifiedMessage(message with { VerifiedSenderPeerId = "B", SourceEndpoint = null }) is Stored { UnreadCountForConversation: 1 }, "same message id from another sender is a distinct message");
            Check(store.GetUnreadCount(a) == 1 && store.GetUnreadCount(b) == 1 && Number(f.Db, "SELECT count(*) FROM Message") == 2, "conversations isolate unread counts");
            using (var observer = Connect(f.Db))
            {
                long version = Number(observer, "PRAGMA data_version");
                Check(store.GetUnreadCount(a) == 1 && store.GetUnreadCount(b) == 1 && Number(observer, "PRAGMA data_version") == version,
                    "unread queries never mark a conversation read");
            }
            Check(store.GetUnreadCount(Guid.NewGuid().ToString("D")) == 0 && Number(f.Db, "SELECT count(*) FROM Conversation") == 2,
                "querying missing conversation does not create it");
            Check(store.ReceiveVerifiedMessage(message with { RecipientId = "Other" }) is WrongRecipient, "wrong recipient rejected before lookup");
            store.ReplaceLocalIdentity("Local2", null, null);
            Check(store.ReceiveVerifiedMessage(message) is WrongRecipient, "retired recipient never accepted");
            Check(store.ReceiveVerifiedMessage(message with { RecipientId = "Local2" }) is Conflict, "same key for a new valid recipient conflicts with old durable message");
            Check(Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'") == installed, "first installation timestamp set once");
        }

        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); Prepare(store);
            var good = new IncomingMessage("A", "valid", "Local", "body");
            foreach (var state in new[] { PairingState.Unpaired, PairingState.Pending, PairingState.Suspended, PairingState.Revoked })
            {
                string peer = "untrusted_" + state;
                store.RegisterPeer(peer, state);
                Check(store.ReceiveVerifiedMessage(good with { VerifiedSenderPeerId = peer }) is UntrustedPeer, "all non-paired states rejected");
            }
            Check(store.ReceiveVerifiedMessage(good with { VerifiedSenderPeerId = "absent" }) is UntrustedPeer && Number(f.Db, "SELECT count(*) FROM Peer WHERE id='absent'") == 0,
                "unknown sender never auto-registered");
            Execute(f.Db, "UPDATE Peer SET pairingState='future-state' WHERE id='A'");
            Check(store.ReceiveVerifiedMessage(good) is UntrustedPeer, "unknown persisted state fails closed");
            store.SetPairingState("A", PairingState.Paired);
            foreach (var bad in new[] { good with { MessageId = "x\n" }, good with { MessageId = new string('x',65) },
                good with { VerifiedSenderPeerId = "bad\n" }, good with { RawText = " " }, good with { RawText = "\ud800" },
                good with { RawText = "\udc00" }, good with { RawText = "a\0b" }, good with { RawText = "a\u007fb" },
                good with { RawText = "a\u0085b" }, good with { Kind = "" }, good with { Kind = "text\n" },
                good with { Reaction = "" }, good with { Reaction = "Future" }, good with { Reaction = "foo\n" }, good with { Reaction = new string('a',33) } })
                Check(store.ReceiveVerifiedMessage(bad) is InvalidPayload, "invalid metadata/text returns a fixed error without writing");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 0 && Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'") == "",
                "all invalid inputs leave message and evidence untouched");
            Check(store.ReceiveVerifiedMessage(good with { RawText = "x\t\r\ny\u200b\u202e" }) is Stored, "allowed TAB/newlines and format characters preserved");
            store.SetPairingState("A", PairingState.Revoked);
            Check(store.ReceiveVerifiedMessage(good) is UntrustedPeer, "revocation checked even for an existing id");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options with { TextValidator = text => Encoding.UTF8.GetByteCount(text) <= 4 }); Prepare(store);
            Check(store.ReceiveVerifiedMessage(new("A", "large", "Local", "你好")) is InvalidPayload &&
                store.ReceiveVerifiedMessage(new("A", "fits", "Local", "你")) is Stored, "text-size policy is injected and counted in UTF8 bytes");
        }

        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            using var ready = new CountdownEvent(32); using var start = new ManualResetEventSlim(false);
            var message = new IncomingMessage("A", "concurrent", "Local", "one", SourceEndpoint: "10.0.0.1");
            var work = Enumerable.Range(0, 32).Select(_ => Task.Factory.StartNew(() => {
                ready.Signal(); if (!start.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("start gate");
                return store.ReceiveVerifiedMessage(message);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            bool allReady = ready.Wait(TimeSpan.FromSeconds(10)); start.Set();
            Check(allReady, "32 actual worker threads reach the concurrent start gate");
            var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));
            Check(results.Count(r => r is Stored) == 1 && results.Count(r => r is Duplicate) == 31, "32 concurrent receives commit exactly once");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && store.GetUnreadCount(a) == 1, "concurrency leaves one unread native row");
        }
        using (var f = new Fixture())
        {
            using var first = ChatStore.Open(f.Options); Prepare(first); using var second = ChatStore.Open(f.Options);
            var message = new IncomingMessage("A", "two-handles", "Local", "one");
            var work = Enumerable.Range(0, 16).Select(i => Task.Run(() => (i % 2 == 0 ? first : second).ReceiveVerifiedMessage(message))).ToArray();
            var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));
            Check(results.Count(r => r is Stored) == 1 && results.Count(r => r is Duplicate) == 15 && Number(f.Db, "SELECT count(*) FROM Message") == 1,
                "SQLite serializes independent store writer handles");
        }

        using (var f = new Fixture())
        {
            DateTime clock = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using var store = ChatStore.Open(f.Options with { UtcNow = () => clock }); Prepare(store);
            Check(store.TouchPeerSeen("A", "10.0.0.1").Written && Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "", "handshake evidence does not mark installation");
            Check(store.ApplyPresenceEvidence("A", "10.0.0.1", true).Written && Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "appVerified",
                "first proven install bypasses probe throttle");
            string installed = Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'");
            clock = clock.AddMilliseconds(59_900); Check(!store.ApplyPresenceEvidence("A", "10.0.0.1", true).Written, "probe at 59.9s throttled");
            clock = clock.AddMilliseconds(100); Check(store.ApplyPresenceEvidence("A", "10.0.0.1", true).Written, "probe at exactly60s writes");
            Check(store.ApplyPresenceEvidence("A", "10.0.0.2", true).Written, "changed endpoint bypasses probe throttle");
            clock = clock.AddHours(-1); Check(store.ApplyPresenceEvidence("A", "10.0.0.2", true).Written, "backward clock does not freeze probe evidence");
            Check(Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'") == installed, "subsequent proofs preserve first-install timestamp");
            store.SetPairingState("A", PairingState.Suspended);
            Check(!store.ApplyPresenceEvidence("A", "10.0.0.3", true).Written, "suspended peer cannot gain presence evidence");
        }
        using (var f = new Fixture())
        {
            DateTime clock = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using var store = ChatStore.Open(f.Options with { UtcNow = () => clock }); Prepare(store);
            store.TouchPeerSeen("A", "10.0.0.1");
            Check(store.ReceiveVerifiedMessage(new("A", "first", "Local", "one", SourceEndpoint: "10.0.0.1")) is Stored &&
                Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "appVerified", "first message installation evidence bypasses handshake throttle");
            string installed = Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'"), seen = Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'");
            clock = clock.AddMilliseconds(29_900);
            Check(store.ReceiveVerifiedMessage(new("A", "second", "Local", "two", SourceEndpoint: "10.0.0.1")) is Stored &&
                Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'") == seen, "new message persists at29.9s without unnecessary evidence write");
            clock = clock.AddMilliseconds(100);
            Check(store.ReceiveVerifiedMessage(new("A", "third", "Local", "three", SourceEndpoint: "10.0.0.1")) is Stored &&
                Text(f.Db, "SELECT lastSeenUtc FROM Peer WHERE id='A'") != seen, "message evidence writes at exactly30s");
            Check(Text(f.Db, "SELECT installEvidenceUtc FROM Peer WHERE id='A'") == installed, "message evidence preserves first install timestamp");
        }

        foreach (bool afterCommit in new[] { false, true })
        {
            using var f = new Fixture(); var faults = new Faults();
            using var store = ChatStore.Open(f.Options with { Faults = faults }); var (a, _) = Prepare(store);
            void Fail(string operation) { if (operation == "receive") throw new IOException("synthetic commit boundary"); }
            if (afterCommit) faults.After = Fail; else faults.Before = Fail;
            bool failed = false;
            try { store.ReceiveVerifiedMessage(new("A", "fault", "Local", "one", SourceEndpoint: "10.0.0.1")); } catch (IOException) { failed = true; }
            faults.After = faults.Before = null;
            Check(failed && Number(f.Db, "SELECT count(*) FROM Message") == (afterCommit ? 1 : 0) && store.GetUnreadCount(a) == (afterCommit ? 1 : 0),
                "before/after-commit exceptions reflect actual durable fact");
            Check((Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "appVerified") == afterCommit,
                "installation evidence commits or rolls back with the message");
            Check(afterCommit ? store.ReceiveVerifiedMessage(new("A", "fault", "Local", "one")) is Duplicate :
                store.ReceiveVerifiedMessage(new("A", "fault", "Local", "one")) is Stored, "retry after boundary error neither loses nor duplicates content");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options with { CommandTimeoutSeconds = 1 }); var (a, _) = Prepare(store);
            Execute(f.Db, $"INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES('{a}','preserved-draft','t')");
            using (var held = Connect(f.Db)) using (var transaction = held.BeginTransaction(deferred: false))
            {
                StoreException? error = null;
                try { store.ReceiveVerifiedMessage(new("A", "busy", "Local", "SYNTHETIC-NOT-LOGGED")); } catch (StoreException e) { error = e; }
                Check(error?.Kind == StoreErrorKind.StorageBusy && !error.ToString().Contains("SYNTHETIC-NOT-LOGGED", StringComparison.Ordinal), "busy failure typed without leaking body");
                Check(Number(f.Db, "SELECT count(*) FROM Message") == 0 && Text(f.Db, "SELECT rawText FROM Draft") == "preserved-draft", "busy cannot discard draft or report stored");
            }
            Check(store.ReceiveVerifiedMessage(new("A", "busy", "Local", "SYNTHETIC-NOT-LOGGED")) is Stored, "explicit receive retry after lock release succeeds");
        }
        using (var f = new Fixture())
        {
            using (var store = ChatStore.Open(f.Options))
            {
                var (a, _) = Prepare(store); store.ReceiveVerifiedMessage(new("A", "kept", "Local", "kept"));
                Execute(f.Db, $"INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES('{a}','preserved-draft','t')");
            }
            int pages = (int)Number(f.Db, "PRAGMA page_count");
            using var limited = ChatStore.Open(f.Options with { MaxPageCount = pages + 1 });
            string failedBody = "SYNTHETIC-NOT-LOGGED" + new string('x', 256 * 1024);
            StoreException? error = null;
            try { limited.ReceiveVerifiedMessage(new("A", "full", "Local", failedBody, SourceEndpoint: "10.0.0.1")); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.DiskFull && !error.ToString().Contains("SYNTHETIC-NOT-LOGGED", StringComparison.Ordinal), "real FULL classified without leaking body");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && Text(f.Db, "SELECT rawText FROM Message") == "kept" && Text(f.Db, "SELECT rawText FROM Draft") == "preserved-draft",
                "FULL keeps prior message and draft, adds no partial row");
            Check(Text(f.Db, "SELECT lastSeenEndpoint FROM Peer WHERE id='A'") == "" && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0,
                "failed insert cannot refresh presence or queue a receipt");
        }

        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); string a;
            using (var setup = ChatStore.Open(f.Options))
            {
                (a, _) = Prepare(setup);
                Execute(f.Db, $"INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES('{a}','preserved-crash-draft','t')");
            }
            using var child = StartChild(f.Data, stage); childCount++;
            try
            {
                string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
                Check(ready == "READY:" + stage, "child reaches the exact product receive commit boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode != 0, "child terminated rather than cleanly disposing store");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            using var recovered = ChatStore.Open(f.Options);
            Check(Number(f.Db, "SELECT count(*) FROM Message") == (stage == "after" ? 1 : 0) && recovered.GetUnreadCount(a) == (stage == "after" ? 1 : 0),
                "native DB after SIGKILL proves commit boundary");
            Check(Text(f.Db, "SELECT rawText FROM Draft") == "preserved-crash-draft" &&
                (Text(f.Db, "SELECT installEvidence FROM Peer WHERE id='A'") == "appVerified") == (stage == "after"),
                "killed receive preserves draft and atomically commits installation evidence");
            var retry = recovered.ReceiveVerifiedMessage(new("A", CrashId, "Local", "child-boundary", SourceEndpoint: "10.0.0.1"));
            Check(stage == "after" ? retry is Duplicate : retry is Stored, "explicit retry after killed process is safe");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && recovered.GetUnreadCount(a) == 1, "crash retry converges to exactly one unread row");
        }
        using (var f = new Fixture())
        {
            string a; using (var setup = ChatStore.Open(f.Options)) { (a, _) = Prepare(setup); }
            using var first = StartChild(f.Data, "concurrent"); using var second = StartChild(f.Data, "concurrent"); childCount += 2;
            var children = new[] { first, second };
            try
            {
                var ready = await Task.WhenAll(children.Select(p => p.StandardOutput.ReadLineAsync())).WaitAsync(TimeSpan.FromSeconds(12));
                Check(ready.All(s => s == "READY:concurrent"), "two independent processes open their own SQLite stores");
                foreach (var child in children) { await child.StandardInput.WriteLineAsync("GO"); await child.StandardInput.FlushAsync(); }
                var output = await Task.WhenAll(children.Select(p => p.StandardOutput.ReadToEndAsync())).WaitAsync(TimeSpan.FromSeconds(12));
                await Task.WhenAll(children.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(5));
                Check(children.All(p => p.ExitCode == 0) && output.Count(s => s.Trim() == "stored") == 1 && output.Count(s => s.Trim() == "duplicate") == 1,
                    "cross-process race commits exactly once and returns one duplicate");
            }
            finally
            {
                foreach (var child in children) if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            using var store = ChatStore.Open(f.Options);
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 1 && store.GetUnreadCount(a) == 1, "cross-process race leaves exactly one native unread row");
        }
        Check(childCount == 4, "exactly four bounded children, two killed and two normal, no recursive suite launch");
        Console.WriteLine($"Storage receive: {checks} transaction, concurrency and crash checks passed; 2 killed + 2 concurrent children.");
        return checks;
    }

    private static Process StartChild(string data, string stage)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(StorageReceiveChecks).Assembly.Location);
        info.ArgumentList.Add(ChildMode); info.ArgumentList.Add(data); info.ArgumentList.Add(stage);
        return Process.Start(info) ?? throw new InvalidOperationException("Child did not start.");
    }
    private static (string A, string B) Prepare(ChatStore store)
    {
        store.EnsureLocalIdentity("Local");
        var a = (PeerRegistered)store.RegisterPeer("A", PairingState.Paired);
        var b = (PeerRegistered)store.RegisterPeer("B", PairingState.Paired);
        return (a.Conversation.ConversationId, b.Conversation.ConversationId);
    }
    private static SqliteConnection Connect(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        c.Open(); using var command = c.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON"; command.ExecuteNonQuery(); return c;
    }
    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static long Number(string path, string sql) { using var c = Connect(path); return Number(c, sql); }
    private static string Text(string path, string sql)
    {
        using var c = Connect(path); using var command = c.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    private static void Execute(string path, string sql) { using var c = Connect(path); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-receive-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { TextValidator = text => Encoding.UTF8.GetByteCount(text) <= 1_000_000 };
        internal Fixture() => Directory.CreateDirectory(Root);
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
