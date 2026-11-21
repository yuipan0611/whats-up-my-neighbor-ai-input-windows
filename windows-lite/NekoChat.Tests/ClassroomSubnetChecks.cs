using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Windows;

namespace NekoChat.Tests;

internal static class ClassroomSubnetChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("Classroom subnet: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "neko-subnet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var faults = new CommitFault();
            using var store = ChatStore.Open(new(root) { Faults = faults });
            string Add(string ip, string name, bool teacher = false) => store.SaveManualRosterEntry(null,
                new(name, null, teacher, ip), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).EntryId!;
            string student = Add("192.168.24.115", "本機備註");
            string teacher = Add("192.168.24.240", "老師", true);
            string plain = Add("192.168.24.15", "192.168.24.15");
            string conflict = Add("192.168.24.20", "舊教室同學");
            string occupied = Add("192.168.21.20", "已有的新位址");
            string unrelated = Add("10.30.0.9", "其他網路");
            string removed = Add("192.168.24.25", "已移除的同學");
            var deletion = store.PreviewRosterDelete(removed);
            store.DeleteRosterEntry(removed, deletion.SnapshotToken, deletion.ToConfirmation());
            store.SetSourceBlocked("192.168.24.15", true);
            store.SetSourceBlocked("192.168.24.20", true);
            var principal = ClassroomPrincipalKey.ForRosterEntry(student);
            store.ReceiveClassroomMessage("192.168.24.115", new("subnet-history", "保留歷史 IP 192.168.24.115"), () => true);
            var history = store.ListClassroomMessages(principal).Items.ToArray();
            store.SaveClassroomDraft(principal, "保留草稿 192.168.24.115", 0, 1);
            var draft = store.GetClassroomDraftState(principal);
            string token = RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
            Check(store.MigrateClassroomSubnet(["192.168.24.115"]).Updated == 0 &&
                RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()) == token, "old classroom does not migrate");
            Check(store.MigrateClassroomSubnet(["192.168.21.115.evil"]).Updated == 0, "invalid local address cannot enable migration");
            faults.Fail = true;
            bool rolledBack = false;
            try { store.MigrateClassroomSubnet(["192.168.21.115"]); } catch (IOException) { rolledBack = true; }
            Check(rolledBack && RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()) == token &&
                store.IsSourceBlocked("192.168.24.15") && !store.IsSourceBlocked("192.168.21.15") &&
                store.GetRemovedRosterEndpoints().Contains("192.168.24.25"), "commit failure rolls back contacts, blocks and removals together");
            faults.Fail = false;
            var result = store.MigrateClassroomSubnet(["192.168.21.115"]);
            var rows = store.GetRosterSnapshot();
            Check(result.Updated == 3 && result.Conflicts == 1 && rows.Count == 6, "only unambiguous old addresses move; no merging");
            Check(rows.Single(r => r.Entry.Id == student).Entry is { Endpoint: "192.168.21.115", DisplayName: "本機備註" } &&
                rows.Single(r => r.Entry.Id == plain).Entry is { Endpoint: "192.168.21.15", DisplayName: "192.168.21.15" }, "preserve IDs/notes and update IP-only labels");
            Check(rows.Single(r => r.Entry.Id == teacher).Entry.IsTeacher && store.GetClassroomTeacherIp() == "192.168.21.240", "teacher recipient keeps role and final octet");
            Check(rows.Single(r => r.Entry.Id == conflict).Entry.Endpoint == "192.168.24.20" &&
                rows.Single(r => r.Entry.Id == occupied).Entry.DisplayName == "已有的新位址" &&
                rows.Single(r => r.Entry.Id == unrelated).Entry.Endpoint == "10.30.0.9", "conflicting and unrelated contacts stay intact");
            Check(store.IsSourceBlocked("192.168.21.15") && !store.IsSourceBlocked("192.168.24.15"), "blocked contact moves to the new classroom");
            Check(store.IsSourceBlocked("192.168.24.20") && !store.IsSourceBlocked("192.168.21.20"),
                "conflicting contact keeps its old block without blocking the occupied new address");
            Check(store.GetRemovedRosterEndpoints().Contains("192.168.21.25") &&
                !store.GetRemovedRosterEndpoints().Contains("192.168.24.25"), "removed contact stays excluded from new default roster");
            Check(store.ListClassroomMessages(principal).Items.SequenceEqual(history), "history/source addresses/message IDs stay byte-for-byte unchanged");
            Check(store.GetClassroomDraftState(principal) == draft, "draft text/epoch/revision stay byte-for-byte unchanged");
            Check(store.MigrateClassroomSubnet(["192.168.21.115"]).Updated == 0, "restart is idempotent");
            store.SetSourceBlocked("192.168.21.15", false);
            store.MigrateClassroomSubnet(["192.168.21.115"]);
            Check(!store.IsSourceBlocked("192.168.21.15"), "migration never undoes a later unblock");
            var preferences = new Settings { LastRecipientIp = "192.168.24.115", TeacherIp = "192.168.24.240",
                Contacts = [new("192.168.24.15", "192.168.24.15"), new("192.168.24.115", "保留備註"), new("10.30.0.9", "其他網路")] };
            var migrated = preferences.MigrateClassroomSubnet();
            Check(migrated.LastRecipientIp == "192.168.21.115" && migrated.TeacherIp == "192.168.21.240" &&
                migrated.Contacts[0] == new ContactItem("192.168.21.15", "192.168.21.15") &&
                migrated.Contacts[1] == new ContactItem("192.168.21.115", "保留備註") && migrated.Contacts[2] == preferences.Contacts[2], "legacy settings change only classroom addresses/IP-only names");
            Check(new Settings().ResolveTeacherDeviceRole(["192.168.21.240"], false).ThisDeviceIsTeacher == true &&
                new Settings().ResolveTeacherDeviceRole(["192.168.21.115"], false).ThisDeviceIsTeacher == false, "new teacher IP resolves role without promoting student");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Classroom subnet: {checks} real SQLite/settings checks passed.");
        return checks;
    }

    private sealed class CommitFault : IStoreFaults
    {
        internal bool Fail;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { if (Fail && op == "classroom-subnet-21") throw new IOException("synthetic subnet commit failure"); }
        public void AfterCommit(string op) { }
    }
}
