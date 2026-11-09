using System.Text;
using NekoChat.Core;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Windows;

namespace NekoChat.Tests;

internal static class DefaultClassroomRosterChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("CT-16 " + name); }
        string root = Path.Combine(Path.GetTempPath(), "neko-default-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = ClassroomRosterSeed.Load(); var csv = RosterCsv.Parse(bytes);
            var students = Enumerable.Range(1, 37).Select(index => "192.168.24." + index * 5).ToArray();
            Check(csv.FatalError is null && csv.Errors.Count == 0 && csv.Rows.Count == 38 &&
                csv.Rows.All(row => row.Name == row.Endpoint && row.StudentNumber is null), "embedded resource contains IP only; no names/student numbers");
            Check(csv.Rows.Where(row => !row.IsTeacher).Select(row => row.Endpoint).SequenceEqual(students) &&
                csv.Rows.Single(row => row.IsTeacher).Endpoint == ClassroomReactions.DefaultTeacherIp &&
                csv.Rows.All(row => row.Endpoint != "192.168.24.200"), "37 student IPs and corrected teacher .240; old .200 excluded");
            foreach (string local in new[] { "192.168.24.5", ClassroomReactions.DefaultTeacherIp })
            {
                using var store = ChatStore.Open(new(Path.Combine(root, local)));
                var preview = ClassroomRosterSeed.Preview(bytes, store.GetRosterSnapshot(), [local])!;
                Check(store.ApplyRosterPreview(preview).Applied, "real SQLite seed applies for " + local);
                var rows = store.GetRosterSnapshot();
                Check(rows.Count == 37 && rows.All(row => row.Entry.Endpoint != local),
                    "self excluded with exact peer count for " + local);
                Check(rows.Count(row => row.Entry.IsTeacher) == (local.EndsWith(".5", StringComparison.Ordinal) ? 1 : 0) &&
                    (local != "192.168.24.5" || store.GetClassroomTeacherIp() == ClassroomReactions.DefaultTeacherIp),
                    "teacher is unique/pinned only when remote: " + local);
                Check(new Settings().ResolveTeacherDeviceRole([local], false).ThisDeviceIsTeacher ==
                    (local == ClassroomReactions.DefaultTeacherIp), "D-33 local role independent of roster teacher: " + local);
                string token = RosterImportPreview.ComputeSnapshotToken(rows);
                Check(ClassroomRosterSeed.Preview(bytes, rows, [local]) is null &&
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()) == token, "nonempty existing roster is never auto-merged");
            }
            string sidecar = Path.Combine(root, "classroom-roster.csv");
            byte[] custom = Encoding.UTF8.GetBytes("student_number,name,ip,role\n,10.32.0.7,10.32.0.7,teacher\n");
            File.WriteAllBytes(sidecar, custom);
            using (var held = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Check(ClassroomRosterSeed.Load(sidecar).SequenceEqual(custom), "sidecar overrides embedded resource with Windows-compatible shared reads");
            using (var store = ChatStore.Open(new(Path.Combine(root, "sidecar-db"))))
            {
                Check(store.ApplyRosterPreview(ClassroomRosterSeed.Preview(ClassroomRosterSeed.Load(sidecar),
                    store.GetRosterSnapshot(), ["192.168.24.5"])!).Applied &&
                    store.GetRosterSnapshot().Single().Entry.Endpoint == "10.32.0.7", "actual sidecar produces only its own roster");
            }
            File.WriteAllText(sidecar, "broken csv");
            bool rejected = false;
            try { ClassroomRosterSeed.Preview(ClassroomRosterSeed.Load(sidecar), [], []); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid existing sidecar does not silently fall back to default");
            Check(ClassroomRosterSeed.Load(Path.Combine(root, "missing.csv")).SequenceEqual(bytes), "missing sidecar falls back to embedded resource");
            byte[] mixed = Encoding.UTF8.GetBytes("student_number,name,ip,role\n,10.32.0.11,10.32.0.11,student\n,bad row,999.32.0.12,student\n,10.32.0.13,10.32.0.13,teacher\n");
            File.WriteAllBytes(sidecar, mixed);
            var mixedCsv = RosterCsv.Parse(mixed);
            Check(ClassroomRosterSeed.Load(sidecar).SequenceEqual(mixed) && mixedCsv.FatalError is null &&
                mixedCsv.Rows.Count == 2 && mixedCsv.Errors.Count == 1, "G1 mixed sidecar has valid rows around one bad IP and never falls back to embedded");
            foreach (bool manual in new[] { false, true })
            {
                using var store = ChatStore.Open(new(Path.Combine(root, "mixed-" + manual)));
                bool wholeRejected = false;
                try
                {
                    var preview = manual
                        ? ClassroomRosterSeed.PreviewMissing(ClassroomRosterSeed.Load(sidecar), store.GetRosterSnapshot(), [], store.GetRemovedRosterEndpoints()).Preview
                        : ClassroomRosterSeed.Preview(ClassroomRosterSeed.Load(sidecar), store.GetRosterSnapshot(), [])!;
                    store.ApplyRosterPreview(preview);
                }
                catch (ArgumentException) { wholeRejected = true; }
                Check(wholeRejected && store.GetRosterSnapshot().Count == 0,
                    "G1 entire mixed sidecar rejected with no partial/default rows: " + (manual ? "manual fill" : "automatic seed"));
            }


            using (var store = ChatStore.Open(new(Path.Combine(root, "existing-db"))))
            {
                string Add(string ip, string name, bool teacher = false) => store.SaveManualRosterEntry(null,
                    new(name, null, teacher, ip), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).EntryId!;
                string kept = Add("192.168.24.10", "保留本機備註");
                string teacher = Add("10.32.0.1", "原本老師", true);
                string collision = Add("10.32.0.2", "192.168.24.15");
                store.SetSourceBlocked("192.168.24.20", true);
                var original = store.GetRosterSnapshot();
                Check(ClassroomRosterSeed.Preview(bytes, original, ["192.168.24.5"]) is null, "upgrade does not merge into nonempty custom roster");
                var addOnly = ClassroomRosterSeed.PreviewMissing(bytes, original, ["192.168.24.5"], store.GetRemovedRosterEndpoints()).Preview;
                Check(addOnly.Changes.Count == 36 && addOnly.Changes.All(item => item.Action == RosterImportAction.Add && item.TargetId is null),
                    "explicit missing-IP preview only adds; matching nickname cannot target existing row");
                var result = store.ApplyRosterPreview(addOnly);
                var after = store.GetRosterSnapshot();
                Check(result.Applied && result.Added == 36 && result.Updated == 0 &&
                    after.Single(row => row.Entry.Id == kept).Entry.DisplayName == "保留本機備註" &&
                    after.Single(row => row.Entry.Id == collision).Entry.Endpoint == "10.32.0.2",
                    "manual fill preserves notes and unrelated same-name endpoints");
                Check(after.Single(row => row.Entry.IsTeacher).Entry.Id == teacher && store.IsSourceBlocked("192.168.24.20"),
                    "manual fill does not replace teacher or unblock source");
                Check(ClassroomRosterSeed.PreviewMissing(bytes, after, ["192.168.24.5"], store.GetRemovedRosterEndpoints()).Preview.Changes.Count == 0,
                    "explicit fill is idempotent and excludes self");
            }
            using (var store = ChatStore.Open(new(Path.Combine(root, "removed-db"))))
            {
                void Remove(string ip)
                {
                    string id = store.SaveManualRosterEntry(null, new(ip, null, false, ip),
                        RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).EntryId!;
                    var deletion = store.PreviewRosterDelete(id);
                    Check(store.DeleteRosterEntry(id, deletion.SnapshotToken, deletion.ToConfirmation()).Changed, "N1 fixture removes contact with modern endpoint snapshot");
                }
                Remove("192.168.24.15"); Remove("192.168.24.20"); Remove("192.168.24.15");
                var removed = store.GetRemovedRosterEndpoints();
                Check(removed.SequenceEqual(new[] { "192.168.24.15", "192.168.24.20" }),
                    "N1 modern removal IPs are distinct even after repeated remove/re-add");
                var fill = ClassroomRosterSeed.PreviewMissing(bytes, store.GetRosterSnapshot(), ["192.168.24.5"], removed);
                Check(fill.SkippedRemoved == 2 && fill.Preview.Changes.Count == 35 &&
                    fill.Preview.Changes.All(item => item.Row!.Endpoint is not ("192.168.24.15" or "192.168.24.20")),
                    "N1 removed contacts excluded from proposed additions with exact skipped count");
                Check(store.ApplyRosterPreview(fill.Preview).Applied && store.GetRosterSnapshot().Count == 35 &&
                    store.GetRemovedRosterEndpoints().SequenceEqual(removed), "N1 fill respects removal and preserves snapshots");
                var repeated = ClassroomRosterSeed.PreviewMissing(bytes, store.GetRosterSnapshot(), ["192.168.24.5"], store.GetRemovedRosterEndpoints());
                Check(repeated.SkippedRemoved == 2 && repeated.Preview.Changes.Count == 0,
                    "N1 repeated fill still skips removals instead of resurrecting them");
            }
            string oldRoot = Path.Combine(root, "old-removal");
            string oldId;
            using (var old = ChatStore.Open(new(oldRoot) { Migrations = Migration.Default.Take(13).ToArray() }))
            {
                oldId = old.SaveManualRosterEntry(null, new("192.168.24.25", null, false, "192.168.24.25"),
                    RosterImportPreview.ComputeSnapshotToken(old.GetRosterSnapshot())).EntryId!;
                var deletion = old.PreviewRosterDelete(oldId);
                Check(old.DeleteRosterEntry(oldId, deletion.SnapshotToken, deletion.ToConfirmation()).Changed &&
                    old.GetRemovedRosterEndpoints().Count == 0, "N1 historical v13 removal contains no comparable endpoint");
            }
            using (var upgraded = ChatStore.Open(new(oldRoot)))
            {
                var fill = ClassroomRosterSeed.PreviewMissing(bytes, upgraded.GetRosterSnapshot(), ["192.168.24.5"], upgraded.GetRemovedRosterEndpoints());
                Check(fill.SkippedRemoved == 0 && fill.Preview.Changes.Any(item => item.Row!.Endpoint == "192.168.24.25") &&
                    upgraded.GetClassroomInboxSnapshot().Entries.Any(entry => entry.RosterEntryId == oldId && entry.Removed),
                    "N1 upgrade keeps old snapshot; never guesses IP from display name");
            }
            using (var store = ChatStore.Open(new(Path.Combine(root, "stale-db"))))
            {
                var preview = ClassroomRosterSeed.PreviewMissing(bytes, store.GetRosterSnapshot(), ["192.168.24.5"], store.GetRemovedRosterEndpoints()).Preview;
                store.SaveManualRosterEntry(null, new("newer", null, false, "10.32.0.8"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                Check(!store.ApplyRosterPreview(preview).Applied && store.GetRosterSnapshot().Count == 1,
                    "changed roster invalidates confirmed snapshot without partial additions");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Default classroom roster: {checks} CT-16 embedded/sidecar/self/old-profile/add-only/CAS checks passed.");
        return checks;
    }
}
