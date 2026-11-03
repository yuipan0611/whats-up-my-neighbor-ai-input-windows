using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>NCM1f: GetClassroomInboxSnapshot on real SQLite. Focused; no WPF.</summary>
internal static class ClassroomInboxChecks
{
    private const string X = "10.0.0.1", Z = "10.0.0.9", W = "10.0.0.7";
    private static readonly DateTime Clock = new(2026, 11, 3, 3, 4, 5, DateTimeKind.Utc);

    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Classroom inbox #{checks}: {name}"); }
        using var f = new Fixture();
        string a, b, c; ClassroomPrincipalKey pa;
        using (var store = ChatStore.Open(f.Options))
        {
            Check(store.GetClassroomInboxSnapshot().Entries.Count == 0 && store.GetClassroomInboxSnapshot().TotalUnread == 0, "empty database: empty inbox");
            a = Add(store, "甲", "2", X, true); b = Add(store, "乙", "10", null, false); c = Add(store, "丙", "3", "10.0.0.5", false);
            pa = ClassroomPrincipalKey.ForRosterEntry(a);
            var empty = store.GetClassroomInboxSnapshot();
            Check(empty.Entries.Select(e => e.RosterEntryId).SequenceEqual([a, c, b]) && empty.Entries.All(e => e.Draft is { RawText: null, Epoch: 0, Revision: 0 } && e.Attempt is null && e.LatestText is null && e.TotalUnread == 0),
                "roster rows with no history are listed in roster order (teacher first, then 3, 10) with an empty draft and no attempt");
            Check(empty.Entries.Single(e => e.RosterEntryId == a) is { CanSend: true, Endpoint: X, IsTeacher: true, Label: "甲", LegacyScope: { RosterEntryId: var la } } && la == a &&
                empty.Entries.Single(e => e.RosterEntryId == b) is { CanSend: false, Endpoint: null }, "endpoint and CanSend come from the roster row (no address, no sending)");

            Rx(store, X, "c1", "classroom one"); Rx(store, X, "c2", "classroom two");
            store.ReceiveLegacyMessage(new("legacy one", null), store.GetLegacyReceiveContext(X), () => true);
            store.ReceiveLegacyMessage(new("legacy two", null), store.GetLegacyReceiveContext(X), () => true);
            store.ReceiveLegacyMessage(new("legacy three", null), store.GetLegacyReceiveContext(X), () => true);
            store.SaveClassroomDraft(ClassroomPrincipalKey.ForSourceIp(Z), "typed to a stranger", 0, 1);
            var pw = ClassroomPrincipalKey.ForSourceIp(W);
            store.SaveClassroomDraft(pw, "will be unconfirmed", 0, 1);
            var begin = (AttemptStarted)store.BeginClassroomSend(pw, "will be unconfirmed", 0, 1);
            Check(store.RecordClassroomRoute(begin.MessageId, 1, ClassroomRoute.Legacy, Clock).Recorded, "fixture: route recorded");
            var pq = ClassroomPrincipalKey.ForSourceIp("10.0.0.20");   // attempt only: the draft is emptied after the send began
            store.SaveClassroomDraft(pq, "attempt without a draft", 0, 1);
            var onlyAttempt = (AttemptStarted)store.BeginClassroomSend(pq, "attempt without a draft", 0, 1);
            store.SaveClassroomDraft(pq, "", 0, 2);
            string hashBefore = Hash(f.Db);
            var snap = store.GetClassroomInboxSnapshot();
            Check(snap.Entries.Single(e => e.RosterEntryId == a) is { ClassroomUnread: 2, LegacyUnread: 3, TotalUnread: 5 }, "classroom and v1 unread of the same row are both counted, once each");
            Check(snap.Entries.Single(e => e.Principal.Value == "ip:" + Z) is { Label: "名單外・10.0.0.9", Draft: { RawText: "typed to a stranger", Epoch: 0, Revision: 1 }, Attempt: null, LatestText: null, CanSend: true, RosterEntryId: null, LegacyScope: { RosterEntryId: null, UnknownSourceIp: Z } },
                "a draft-only 名單外 conversation is listed with its draft epoch, revision and text");
            Check(snap.Entries.Single(e => e.Principal.Value == "ip:" + W) is { Attempt: { State: "sending", RawSnapshot: "will be unconfirmed", TryCount: 1, Route: ClassroomRoute.Legacy }, Draft.RawText: "will be unconfirmed" },
                "an attempt-only 名單外 conversation is listed with the attempt, its try count and its recorded route");
            Check(snap.Entries.Single(e => e.Principal == pq) is { Draft.RawText: null, Attempt: { State: "sending", RawSnapshot: "attempt without a draft" }, LatestText: null } && onlyAttempt.TryCount == 1, "a conversation with only an attempt (no draft, no message) is listed");
            Check(snap.Entries.Count == 6 && snap.Entries.Take(3).All(e => e.RosterEntryId is not null) && snap.Entries.Skip(3).All(e => e.Principal.SourceIp is not null), "roster rows first, then 名單外 conversations");
            Check(Hash(f.Db) == hashBefore, "reading the inbox does not change the database file");
        }
        using (var store = ChatStore.Open(f.Options))
        {
            var w = store.GetClassroomInboxSnapshot().Entries.Single(e => e.Principal.Value == "ip:" + W);
            Check(w.Attempt is { State: "unconfirmed", Route: ClassroomRoute.Legacy } && w.Draft.RawText == "will be unconfirmed" && w.LatestText is null, "after a restart the attempt is unconfirmed and the draft is still there, with no confirmed message");

            var preview = store.PreviewRosterDelete(a);
            Check(store.DeleteRosterEntry(a, Token(store), preview.ToConfirmation()).Reason == "deleted", "fixture: row deleted after confirmation");
            string again = Add(store, "甲", "2", X, true); var pAgain = ClassroomPrincipalKey.ForRosterEntry(again);
            store.ReceiveLegacyMessage(new("legacy after re-add", null), store.GetLegacyReceiveContext(X), () => true);
            var snap = store.GetClassroomInboxSnapshot();
            var removed = snap.Entries.Single(e => e.Principal == pa);
            Check(removed is { Removed: true, DisplayName: "甲", CanSend: false, Label: "甲（已從名單移除）", Endpoint: null, LegacyScope: null, ClassroomUnread: 2, LegacyUnread: 0, LatestText: "classroom two" } && removed.RosterEntryId == a,
                "a deleted row's conversation is listed with the name at deletion, readable, not sendable, and without the v1 rows it lost");
            Check(snap.Entries.Single(e => e.Principal.Value == "ip:" + X) is { LegacyUnread: 3, ClassroomUnread: 0, Label: "名單外・10.0.0.1" } &&
                snap.Entries.Single(e => e.Principal == pAgain) is { LegacyUnread: 1, ClassroomUnread: 0, CanSend: true }, "v1 rows that lost their owner appear once, under 名單外; the re-added row counts only its own");
            long expected = Count(f.Db, "SELECT (SELECT count(*) FROM ClassroomMessage WHERE direction='incoming' AND localReadAt IS NULL)+(SELECT count(*) FROM LegacyMessage WHERE localReadAt IS NULL)");
            Check(snap.TotalUnread == expected && expected == 6, "the total equals the unread rows in the database: nothing counted twice");
            Check(snap.Entries.Select(e => e.Principal.Value).Distinct().Count() == snap.Entries.Count, "every principal appears exactly once");
        }
        using (var store = ChatStore.Open(f.Options))
        {
            var pz = ClassroomPrincipalKey.ForSourceIp(Z);
            Rx(store, Z, "z1", new string('字', 450));
            var e = store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == pz);
            Check(e.LatestText!.EnumerateRunes().Count() == 201 && e.LatestText.EndsWith('…') && !e.LatestIsLegacy && e.Draft.RawText == "typed to a stranger", "the summary is the newest message, cut to 200 characters; the draft stays alongside");
            store.ReceiveLegacyMessage(new("newer v1 text", null), store.GetLegacyReceiveContext(Z), () => true);
            Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == pz) is { LatestText: "newer v1 text", LatestIsLegacy: true, LegacyUnread: 1, ClassroomUnread: 1, LegacyScope.UnknownSourceIp: Z }, "a newer v1 message becomes the summary and both unread sets are shown");
            var begin = (AttemptStarted)store.BeginClassroomSend(pz, "typed to a stranger", 0, 1);
            store.RecordClassroomRoute(begin.MessageId, 1, ClassroomRoute.Classroom, Clock);
            Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == pz).Attempt is { State: "sending", Route: ClassroomRoute.Classroom }, "the sending attempt is shown");
            store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, ChatStore.ClassroomFingerprint("typed to a stranger", null)), Z);
            var done = store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == pz);
            Check(done.Attempt is null && done.Draft.RawText is null, "a confirmed attempt is not shown as actionable and its draft is cleared");
            var snapshot = store.GetClassroomInboxSnapshot();
            store.Dispose();
            Check(snapshot.Entries.Count > 0 && snapshot.Entries[0].Label.Length > 0, "the snapshot is fully materialized and usable after the store is closed");

            // Pure function tests: ClassroomInboxSnapshot.ToInboxSnapshot() and ClassroomInboxEntry.StateDescription
            var inboxSnapshot = snapshot.ToInboxSnapshot();
            Check(inboxSnapshot.Entries.Count == snapshot.Entries.Count, "ToInboxSnapshot maps all entries");
            Check(inboxSnapshot.TotalUnread == snapshot.TotalUnread, "ToInboxSnapshot preserves TotalUnread");

            bool foundRemovedCanSendFalse = false;
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                var src = snapshot.Entries[i];
                var dst = inboxSnapshot.Entries[i];
                Check(dst.Id == src.Principal.Value, $"entry[{i}] Id equals principal value");
                Check(dst.RosterId == src.RosterEntryId, $"entry[{i}] RosterId matches");
                Check(dst.DisplayName == src.Label, $"entry[{i}] DisplayName equals label");
                Check(dst.StudentNumber == src.StudentNumber, $"entry[{i}] StudentNumber matches");
                Check(dst.IsTeacher == src.IsTeacher, $"entry[{i}] IsTeacher matches");
                Check(dst.Endpoint == src.Endpoint, $"entry[{i}] Endpoint matches");
                Check(dst.ConversationId == src.Principal.Value, $"entry[{i}] ConversationId matches");
                Check(dst.Unread == src.ClassroomUnread, $"entry[{i}] Unread matches ClassroomUnread");
                Check(dst.LegacyUnread == src.LegacyUnread, $"entry[{i}] LegacyUnread matches");
                Check(dst.TotalUnread == src.TotalUnread, $"entry[{i}] TotalUnread matches");
                Check(dst.LastText == src.LatestText, $"entry[{i}] LastText matches");
                Check(dst.LastUtc == src.LatestUtc, $"entry[{i}] LastUtc matches");
                Check(dst.LastIsLegacy == src.LatestIsLegacy, $"entry[{i}] LastIsLegacy matches");
                Check(dst.Draft == src.Draft, $"entry[{i}] Draft matches");
                Check(dst.Removed == src.Removed, $"entry[{i}] Removed matches");
                Check(dst.CanSend == src.CanSend, $"entry[{i}] CanSend matches");

                if (src.Removed && !src.CanSend)
                {
                    foundRemovedCanSendFalse = true;
                    Check(dst.Removed && !dst.CanSend, "removed entry has Removed=true and CanSend=false");
                }
            }
            Check(foundRemovedCanSendFalse, "snapshot dataset contains at least one entry with Removed=true and CanSend=false");


            // ClassroomNotificationNames pure function tests (item 5)
            Check(ClassroomNotificationNames.Format("192.168.1.50", "同學甲", false) == "同學甲", "Notification name for valid roster contact");
            Check(ClassroomNotificationNames.Format("192.168.1.50", null, false) == "未驗證來源・192.168.1.50", "Notification name for off-roster source has prefix 未驗證來源・");
            Check(ClassroomNotificationNames.Format("192.168.1.50", "", false) == "未驗證來源・192.168.1.50", "Notification name for empty roster name has prefix 未驗證來源・");
            Check(ClassroomNotificationNames.Format("192.168.1.50", "同學甲", true) == "未驗證來源・192.168.1.50", "Notification name for removed contact has prefix 未驗證來源・");

            // StateDescription pure logic checks
            var dummyKey = ClassroomPrincipalKey.ForSource("192.168.1.100")!.Value;
            var removedEntry = new ClassroomInboxEntry(dummyKey, "r1", "同學A", "101", false, true, "192.168.1.100", false, 0, 0, null, null, false, new DraftState(null, 0, 0), null, null);
            Check(removedEntry.StateDescription == "已從名單移除，不能發送，可查看對話", "StateDescription for removed entry");
            var noEndpointEntry = new ClassroomInboxEntry(dummyKey, "r2", "同學B", "102", false, false, null, false, 0, 0, null, null, false, new DraftState(null, 0, 0), null, null);
            Check(noEndpointEntry.StateDescription == "未設定位址", "StateDescription for missing endpoint");
            var normalEntry = new ClassroomInboxEntry(dummyKey, "r3", "同學C", "103", false, false, "192.168.1.101", true, 0, 0, null, null, false, new DraftState(null, 0, 0), null, null);
            Check(normalEntry.StateDescription == "在線狀態待確認", "StateDescription for normal entry");
            var legacyUnreadEntry = new ClassroomInboxEntry(dummyKey, "r4", "同學D", "104", false, false, "192.168.1.102", true, 0, 2, null, null, false, new DraftState(null, 0, 0), null, null);
            Check(legacyUnreadEntry.StateDescription == "舊版訊息・在線狀態待確認", "StateDescription with legacy unread");
        }
        Console.WriteLine($"Classroom inbox: {checks} roster-without-history, draft-only, attempt-only, restart, removed, unread de-duplication and read-only checks passed on real SQLite.");
        return checks;
    }

    private static string Add(ChatStore store, string name, string number, string? ip, bool teacher)
    {
        var r = store.SaveManualRosterEntry(null, new(name, number, teacher, ip), Token(store));
        if (!r.Changed) throw new Exception("Classroom inbox fixture: " + r.Reason); return r.EntryId!;
    }
    private static string Token(ChatStore s) => RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot());
    private static void Rx(ChatStore s, string ip, string id, string text)
    { if (s.ReceiveClassroomMessage(ip, new(id, text), () => true) is not ClassroomStored) throw new Exception("Classroom inbox fixture: not stored"); }
    private static long Count(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static string Hash(string path) { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }

    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-classroom-inbox-" + Guid.NewGuid().ToString("N"));
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => Clock };
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
}
