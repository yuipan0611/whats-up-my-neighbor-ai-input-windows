using System.Text;
using NekoChat.Core.Presentation;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;
internal static class ClassroomUxChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new Exception(name); }
        foreach (bool teacher in new[] { false, true })
        foreach (bool removed in new[] { false, true })
        foreach (bool sendable in new[] { false, true })
        foreach (bool teacherOnly in new[] { false, true })
            Check(ClassroomReactionPolicy.CanSend(teacher, removed, sendable, teacherOnly) == (!removed && sendable && teacher == teacherOnly), "reaction recipient policy");
        string root = Path.Combine(Path.GetTempPath(), "neko-class-ux-" + Guid.NewGuid().ToString("N"));
        byte[] csv = Encoding.UTF8.GetBytes("student_number,name,ip,role\n,測試老師,10.0.0.10,teacher\n01,測試甲,10.0.0.11,student\n02,測試乙,10.0.0.12,student\n");
        try
        {
            using (var store = ChatStore.Open(new(root)))
            {
                var preview = ClassroomRosterSeed.Preview(csv, store.GetRosterSnapshot(), new[] { "10.0.0.11" });
                Check(preview is not null && store.ApplyRosterPreview(preview).Applied, "real SQLite seed");
                var rows = store.GetRosterSnapshot();
                Check(rows.Count == 2 && rows.Single(r => r.Entry.IsTeacher).Entry.DisplayName == "測試老師", "teacher role and own IP exclusion");
                Check(rows.All(r => r.Entry.PeerId is null), "roster seed creates no identity binding");
                Check(ClassroomRosterSeed.Preview(csv, rows, []) is null, "never overwrite existing roster");
                foreach (string invalid in new[] { "bad", "student_number,name,ip\n,缺少位址,\n", "student_number,name,ip\n,A,10.0.0.1\n,B,10.0.0.1\n", "student_number,name,ip\n,A,https://example.com\n" })
                {
                    bool rejected = false; try { ClassroomRosterSeed.Preview(Encoding.UTF8.GetBytes(invalid), [], []); } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "invalid file must be rejected as a whole");
                }
                var stale = ClassroomRosterSeed.Preview(csv, [], [])!;
                Check(!store.ApplyRosterPreview(stale).Applied, "concurrent existing roster blocks stale seed");
            }
            using (var store = ChatStore.Open(new(root))) Check(store.GetRosterSnapshot().Count == 2, "seed survives restart");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Classroom UX: {count} recipient-policy and real SQLite roster checks.");
    }
}
