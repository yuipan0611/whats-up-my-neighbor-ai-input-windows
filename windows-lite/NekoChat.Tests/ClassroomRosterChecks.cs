using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>
/// TC-04 (classroom mode, CM-01～05) Core part: real SQLite, injected source IP (the same two calls the listener makes)
/// and a real TCP loopback delivery. No key, pairing or number comparison is used anywhere in this file.
/// Roster A=x, B=y; C is an off-roster source z.
/// </summary>
internal static class ClassroomRosterChecks
{
    private const string X = "10.0.0.1", Y = "10.0.0.2", Z = "10.0.0.9", W = "10.0.0.7";

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Classroom roster #{checks}: {name}"); }

        // ① messages from x / y belong to A / B and show the roster name; ② z is out-of-roster and never auto-added.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, new("甲", "1", false, X)), b = Add(store, new("乙", "2", false, Y));
            Receive(store, X, "from-x"); Receive(store, Y, "from-y"); Receive(store, Z, "from-z-1"); Receive(store, Z, "from-z-2");
            Check(Texts(store, new(a, null)).SequenceEqual(["from-x"]) && Texts(store, new(b, null)).SequenceEqual(["from-y"]), "① x and y land in A and B");
            var names = store.GetRosterSnapshot().ToDictionary(r => r.Entry.Id, r => r.Entry.DisplayName);
            var histories = store.ListLegacyHistories();
            Check(histories.Count == 3 && names[histories.Single(h => h.Scope.RosterEntryId == a).Scope.RosterEntryId!] == "甲" &&
                names[histories.Single(h => h.Scope.RosterEntryId == b).Scope.RosterEntryId!] == "乙", "① history groups resolve to the roster names");
            Check(histories.Single(h => h.Scope.RosterEntryId is null) is { Scope.UnknownSourceIp: Z, Messages: 2 } &&
                RosterEntryView.OutOfRosterLabel(Z) == "名單外・10.0.0.9", "② z is its own out-of-roster conversation");
            Check(store.GetRosterSnapshot().Count == 2 && Number(f.Db, "SELECT count(*) FROM RosterEntry WHERE endpoint='" + Z + "'") == 0, "② an unknown source is never added to the roster");
            Check(Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM Message") == 0 &&
                store.GetLocalIdentitySnapshot() is { PublicSpkiBase64: null, CredentialRef: null }, "⑦ receiving and attributing needs no key, peer or pairing");

            // ② manual add: preview first (writes nothing), confirmation merges, a stale confirmation moves nothing.
            var edit = new RosterEdit("丙", "3", false, Z);
            string before = Token(store);
            var preview = store.PreviewManualRosterEntry(null, edit);
            Check(preview is { CanSave: true, UnknownMessagesToMerge: 2, MergeNotice: "此 IP 的 2 則名單外訊息會併入" } && Token(store) == before &&
                Number(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL") == 2, "② preview states the merge count and writes nothing");
            Receive(store, Z, "from-z-3");
            var stale = store.SaveManualRosterEntry(null, edit, Token(store), preview.ToMergeConsent());
            Check(stale.Mutation is { Changed: false, Reason: "legacy_merge_changed" } && store.GetRosterSnapshot().Count == 2 &&
                Number(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL") == 3, "② count changed after the preview: nothing is saved or merged");
            var mismatch = store.SaveManualRosterEntry(null, edit, Token(store), new LegacyMergeConsent(Y, 3));
            Check(mismatch.Mutation.Reason == "legacy_merge_mismatch" && store.GetRosterSnapshot().Count == 2, "② consent for another address is refused");
            var fresh = store.PreviewManualRosterEntry(null, edit);
            var saved = store.SaveManualRosterEntry(null, edit, Token(store), fresh.ToMergeConsent());
            Check(saved is { Mutation: { Changed: true, Reason: "added" }, MergedMessages: 3 } && Number(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL") == 0 &&
                Texts(store, new(saved.Mutation.EntryId, null)).SequenceEqual(["from-z-1", "from-z-2", "from-z-3"]), "② confirmed merge moves all three, in order, into the new row");
            Receive(store, Z, "from-z-4");
            Check(Texts(store, new(saved.Mutation.EntryId, null)).Count == 4, "② later messages from z now belong to the new row");
        }

        // ② without confirmation the roster row is added but the old out-of-roster history is not silently re-attributed.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            Receive(store, Z, "old-1"); Receive(store, Z, "old-2");
            var added = store.SaveManualRosterEntry(null, new("丙", null, false, Z), Token(store), null);
            Check(added.Mutation.Changed && added.MergedMessages == 0 && Number(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL") == 2, "② no consent: history stays out-of-roster");
            Check(store.SaveManualRosterEntry(null, new("丁", null, false, W), Token(store), new LegacyMergeConsent(Y, 1)).Mutation.Reason == "legacy_merge_mismatch", "② consent never applies to a different address");
        }

        // ③ B changes from y to w: old messages stay with B, w now maps to B, y becomes out-of-roster.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, new("甲", "1", false, X)), b = Add(store, new("乙", "2", false, Y));
            Receive(store, Y, "b-before");
            var change = store.SaveManualRosterEntry(b, new("乙", "2", false, W), Token(store));
            Check(change.Changed && store.GetRosterSnapshot().Single(r => r.Entry.Id == b).Entry.Endpoint == W, "③ B's address changed to w");
            Receive(store, W, "b-after"); Receive(store, Y, "y-stranger");
            Check(Texts(store, new(b, null)).SequenceEqual(["b-before", "b-after"]), "③ B keeps the old message and receives from w");
            Check(Texts(store, new(null, Y)).SequenceEqual(["y-stranger"]) && Texts(store, new(a, null)).Count == 0, "③ y is now out-of-roster y");
            Check(store.GetRosterSnapshot().Single(r => r.Entry.Id == b).Entry.PeerId is null && Number(f.Db, "SELECT count(*) FROM Peer") == 0, "③ moving an address creates or moves no peer identity");
        }

        // ④ one address, two rows: blocked in manual edit, in CSV and in CSV against an existing row; fixing the file unblocks.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, new("甲", "1", false, X)), b = Add(store, new("乙", "2", false, Y));
            var manual = store.PreviewManualRosterEntry(b, new("乙", "2", false, X));
            Check(manual is { CanSave: false, Reason: "duplicate-endpoint", ConflictEntryId: var c1, ConflictName: "甲" } && c1 == a, "④ manual edit preview names the row that already owns the address");
            Check(store.SaveManualRosterEntry(b, new("乙", "2", false, X), Token(store)) is { Changed: false, Reason: "duplicate-endpoint" } &&
                store.GetRosterSnapshot().Single(r => r.Entry.Id == b).Entry.Endpoint == Y, "④ manual save refuses and keeps the old address");

            var inFile = Preview(store, "student_number,name,ip\n1,甲,10.0.0.5\n2,乙,10.0.0.5\n3,丙,10.0.0.6");
            Check(!inFile.CanApply && inFile.IpConflictLines.SequenceEqual([2, 3]), "④ the same address on two CSV lines is flagged on both lines");
            var blockedResult = store.ApplyRosterPreview(inFile);
            Check(blockedResult is { Applied: false, Reason: "ip_conflict" } && store.GetRosterSnapshot().Count == 2 && !Directory.Exists(Path.Combine(f.Data, "backup")) &&
                store.GetRosterSnapshot().Single(r => r.Entry.Id == a).Entry.Endpoint == X, "④ blocked preview cannot be applied: no row, address or backup changes, even for the valid line");

            // VCM1a: every line conflicting must not look like a successful empty import, and no backup may be made.
            var allConflict = Preview(store, "student_number,name,ip\n1,甲,10.0.0.5\n2,乙,10.0.0.5");
            Check(allConflict.Changes.Count == 0 && !allConflict.CanApply && store.ApplyRosterPreview(allConflict) is { Applied: false, Reason: "ip_conflict" } &&
                !Directory.Exists(Path.Combine(f.Data, "backup")) && store.GetRosterSnapshot().Single(r => r.Entry.Id == a).Entry.Endpoint == X, "④ all lines conflicting: refused, zero changes, no backup");

            var againstRow = Preview(store, "student_number,name,ip\n3,丙,10.0.0.1");
            Check(!againstRow.CanApply && againstRow.Items.Single().ErrorCodes.Contains("endpoint_conflict") && store.ApplyRosterPreview(againstRow).Reason == "ip_conflict",
                "④ an address owned by a row outside the file also blocks");

            var fixedFile = Preview(store, "student_number,name,ip\n1,甲,10.0.0.5\n2,乙,10.0.0.6\n3,丙,10.0.0.8");
            Check(fixedFile.CanApply && store.ApplyRosterPreview(fixedFile) is { Applied: true, Added: 1, Updated: 2 }, "④ after correcting the file it applies");

            var swap = Preview(store, "student_number,name,ip\n1,甲,10.0.0.6\n2,乙,10.0.0.5");
            Check(swap.CanApply && store.ApplyRosterPreview(swap).Applied, "④ a complete address swap is not a conflict");
        }

        // ⑤ CSV without an ip column keeps addresses; an empty ip cell clears them and the preview says so.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, new("甲", "1", false, X)), b = Add(store, new("乙", "2", false, Y));
            Receive(store, X, "kept-history");
            var noColumn = Preview(store, "student_number,name\n1,甲改名\n2,乙\n3,丙");
            Check(noColumn.ClearedAddresses.Count == 0 && noColumn.Items.Select(i => i.Action).SequenceEqual([RosterImportAction.Update, RosterImportAction.Unchanged, RosterImportAction.Add]) &&
                noColumn.Items.All(i => i.AddressChange == RosterAddressChange.None && i.AddressChangeText is null), "⑤ no ip column: nothing is announced as cleared and untouched rows stay unchanged");
            Check(store.ApplyRosterPreview(noColumn) is { Applied: true, Added: 1, Updated: 1 }, "⑤ no ip column applies");
            var rows = store.GetRosterSnapshot();
            Check(rows.Single(r => r.Entry.Id == a).Entry is { DisplayName: "甲改名", Endpoint: X } && rows.Single(r => r.Entry.Id == b).Entry.Endpoint == Y &&
                rows.Single(r => r.Entry.StudentNumberKey == "3").Entry.Endpoint is null, "⑤ no ip column keeps the existing addresses (the reported bug)");

            var emptyCell = Preview(store, "student_number,name,ip\n1,甲改名,\n2,乙,10.0.0.2\n3,丙,");
            Check(emptyCell.ClearedAddresses.Count == 1 && emptyCell.ClearedAddresses[0] is { Line: 2, AddressChange: RosterAddressChange.Cleared, AddressChangeText: "清除位址", TargetId: var cleared } && cleared == a &&
                emptyCell.Items.Single(i => i.Line == 4).AddressChange == RosterAddressChange.None, "⑤ empty ip cell: the preview lists exactly the row whose address would be cleared as 清除位址");
            Check(store.ApplyRosterPreview(emptyCell).Applied && store.GetRosterSnapshot().Single(r => r.Entry.Id == a).Entry.Endpoint is null &&
                store.GetRosterSnapshot().Single(r => r.Entry.Id == b).Entry.Endpoint == Y, "⑤ empty ip cell clears only that row");
            Check(Texts(store, new(a, null)).SequenceEqual(["kept-history"]), "⑤ clearing an address keeps the person's history");

            var manualClear = store.PreviewManualRosterEntry(b, new("乙", "2", false, null));
            Check(manualClear is { CanSave: true, AddressChange: RosterAddressChange.Cleared, AddressChangeText: "清除位址" }, "⑤ manual edit announces 清除位址 too");

            // A column present but another column order and the role column must behave the same.
            var withRole = Preview(store, "role,name,student_number\nstudent,乙改名,2");
            Check(store.ApplyRosterPreview(withRole).Applied && store.GetRosterSnapshot().Single(r => r.Entry.Id == b).Entry is { DisplayName: "乙改名", Endpoint: Y }, "⑤ no ip column with other column order keeps the address");
        }

        // ⑥ a row without an address: 未設定位址, cannot send, history still readable; clearing disables sending at the moment of use.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string none = Add(store, new("丁", "4", false, null)), has = Add(store, new("甲", "1", false, X));
            var view = store.GetRosterSnapshot().Single(r => r.Entry.Id == none).Entry;
            Check(!view.HasAddress && !view.CanSend && view.AddressStatus == "未設定位址", "⑥ no address: status 未設定位址 and not sendable");
            Check(store.ResolveRosterSendTarget(none) is { CanSend: false, Reason: "no-address", Endpoint: null } &&
                store.ResolveRosterSendTarget(has) is { CanSend: true, Endpoint: X } && store.ResolveRosterSendTarget(Guid.NewGuid().ToString()).Reason == "not-found", "⑥ the store gate refuses a row without address");
            Receive(store, X, "hello");
            Check(store.GetRosterSnapshot().Single(r => r.Entry.Id == has).Entry.CanSend, "⑥ stale list: the row was sendable");
            var clear = Preview(store, "student_number,name,ip\n1,甲,\n4,丁,");
            store.ApplyRosterPreview(clear);
            Check(!store.ResolveRosterSendTarget(has).CanSend && store.ResolveRosterSendTarget(has).Reason == "no-address" && Texts(store, new(has, null)).SequenceEqual(["hello"]),
                "⑥ after the address is cleared the gate closes but the history stays readable");
            Check(Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM Conversation") == 0, "⑦ the whole flow created no peer, key or pairing record");
        }

        // ① with a real TCP delivery: the address the listener observes decides the owner.
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            Execute(f.Db, "INSERT INTO RosterEntry(id,displayName,endpoint,source) VALUES('11111111-1111-4111-8111-111111111111','本機同學','127.0.0.1','test')");
            await using var listener = new DirectLanListener(null, new V2ListenerOptions { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 1000 }, store);
            listener.Start();
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "over tcp", listener.Port) &&
                Texts(store, new("11111111-1111-4111-8111-111111111111", null)).SequenceEqual(["over tcp"]), "① real TCP: the observed source address belongs to the roster row");
            Execute(f.Db, "UPDATE RosterEntry SET endpoint='10.9.9.9' WHERE id='11111111-1111-4111-8111-111111111111'");
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "after move", listener.Port) &&
                Texts(store, new(null, "127.0.0.1")).SequenceEqual(["after move"]) && Texts(store, new("11111111-1111-4111-8111-111111111111", null)).Count == 1,
                "③ real TCP: after the row moved, the old address is out-of-roster and the earlier message stays with the person");
            Check(Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM Message") == 0, "⑦ real TCP flow needs no pairing or key");
        }

        Console.WriteLine($"Classroom roster: {checks} IP attribution, merge preview, IP uniqueness, CSV keep/clear and no-address checks passed; real SQLite and TCP loopback.");
        return checks;
    }

    private static string Add(ChatStore store, RosterEdit edit)
    {
        var result = store.SaveManualRosterEntry(null, edit, Token(store));
        if (!result.Changed) throw new Exception("Classroom roster fixture: " + result.Reason);
        return result.EntryId!;
    }
    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static RosterImportPreview Preview(ChatStore store, string csv) =>
        RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes(csv)));
    private static void Receive(ChatStore store, string ip, string text)
    {
        // The same two calls DirectLanListener makes for a v1 message; ip is the observed source address.
        var context = store.GetLegacyReceiveContext(ip);
        if (!store.ReceiveLegacyMessage(new(text, null), context, () => true).Stored) throw new Exception("Classroom roster fixture: not stored");
    }
    private static IReadOnlyList<string> Texts(ChatStore store, LegacyHistoryScope scope) => store.ListLegacyMessages(scope).Items.Select(m => m.RawText).ToArray();
    private static long Number(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static void Execute(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-classroom-" + Guid.NewGuid().ToString("N"));
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => new DateTime(2026, 11, 3, 3, 4, 5, DateTimeKind.Utc) };
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
}
