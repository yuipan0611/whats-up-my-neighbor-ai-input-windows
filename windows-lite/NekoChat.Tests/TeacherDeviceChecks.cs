using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Storage;
using NekoChat.Windows;

namespace NekoChat.Tests;

internal static class TeacherDeviceChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException(label); }
        string root = Path.Combine(Path.GetTempPath(), "neko-teacher-device-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            using var store = ChatStore.Open(new(Path.Combine(root, "data")));
            File.WriteAllText(path, """{"TeacherIp":"10.30.0.240","ClassRosterInitialized":true}""");
            var legacy = Settings.LoadWithStatus(path).Value;
            Check(legacy.ThisDeviceIsTeacher is null && store.GetClassroomTeacherIp() is null,
                "RX-07 legacy fixture has no self/teacher roster and no migrated device flag");
            var resolved = legacy.ResolveTeacherDeviceRole(["10.30.0.240", "10.30.1.8"], false);
            Check(resolved.ThisDeviceIsTeacher == true && !resolved.ThisDeviceTeacherExplicitChoice,
                "RX-07 legacy TeacherIp matches one real private address before projection");
            var projected = resolved with { TeacherIp = store.GetClassroomTeacherIp() ?? "" };
            Check(projected.Save() && projected.TeacherIp == "" && Settings.LoadWithStatus(path).Value
                .ResolveTeacherDeviceRole(["10.40.0.8"], false).ThisDeviceIsTeacher == true,
                "RX-07 role survives authoritative empty SQLite projection/save/reload and DHCP change");
            var student = legacy.ResolveTeacherDeviceRole(["10.30.0.8"], false);
            Check(student.ThisDeviceIsTeacher == false, "RX-07 student's remote teacher IP is never its local device role");
            foreach (bool? prior in new bool?[] { null, false, true })
                Check((legacy with { TeacherIp = "", ThisDeviceIsTeacher = prior }).ResolveTeacherDeviceRole(
                    ["10.31.0.8", ClassroomReactions.DefaultTeacherIp], false).ThisDeviceIsTeacher == true,
                    "RX-07 every startup promotes fixed default IP even after prior false/empty projection");
            foreach (bool choice in new[] { false, true })
            {
                var explicitValue = student with { ThisDeviceIsTeacher = choice, ThisDeviceTeacherExplicitChoice = true };
                Check(explicitValue.Save() && Settings.LoadWithStatus(path).Value.ResolveTeacherDeviceRole(
                    [ClassroomReactions.DefaultTeacherIp], true).ThisDeviceIsTeacher == choice &&
                    TeacherDevicePolicy.IsTeacher(choice, true, true) == choice,
                    "RX-07 explicit ON/OFF always wins, including fixed default IP and teacher-node hint");
            }
            Check((student with { TeacherIp = "" }).ResolveTeacherDeviceRole([], true).ThisDeviceIsTeacher == true &&
                TeacherDevicePolicy.IsTeacher(false, false, true), "RX-07 CSV teacher node automatically promotes without explicit choice");
            Check(new Settings { TeacherIp = "127.0.0.1" }.ResolveTeacherDeviceRole(["10.30.0.8"], false).ThisDeviceIsTeacher == false &&
                !TeacherDevicePolicy.IsTeacher(null, false, false), "RX-07 no special loopback teacher shortcut");
            Check((student with { TeacherIp = "" }).ResolveTeacherDeviceRole(["10.30.0.8"], false).ThisDeviceIsTeacher == false,
                "RX-07 ordinary student defaults false after empty projection");

            var sink = new ClassroomStoreSink(store);
            var notices = new List<LegacyDeliveryNotice>(); sink.LegacyMessageStored += notices.Add;
            const string source = "10.30.0.9";
            foreach (var reaction in ClassroomReactions.All.Where(r => r.TeacherOnly))
            {
                Check(ReactionPreviewPolicy.Notification(true, reaction, false) == ReactionNotificationKind.None &&
                    ReactionPreviewPolicy.Notification(false, reaction, false) == ReactionNotificationKind.None &&
                    ReactionPreviewPolicy.Notification(false, reaction, true) == ReactionNotificationKind.Text,
                    "RX-08 nonteacher suppresses image and text regardless of preview toggle: " + reaction.Id);
                await sink.OnLegacyDirectAsync(source, new(reaction.Text, reaction.Id), () => true, default);
                var notice = notices[^1]; var row = store.ListLegacyMessages(notice.Scope).Items.Single(m => m.LegacySeq == notice.LegacySeq);
                Check(row.RawText == reaction.Text && row.Reaction == reaction.Id &&
                    ReactionPreviewPolicy.HistoryLabel(row.Reaction, false) == "課堂反應：" + reaction.Text + "（只有老師電腦會跳出貓咪圖）" &&
                    ReactionPreviewPolicy.HistoryLabel(row.Reaction, true) is null &&
                    ReactionPreviewPolicy.HistoryLabel(row.Reaction, false, outgoing: true) is null,
                    "RX-08 durable exact body/id and small history annotation without mutating saved content: " + reaction.Id);
            }
            await sink.OnLegacyDirectAsync(source, new("講太快了", null), () => true, default);
            var ordinary = notices[^1];
            Check(ordinary.Reaction is null && store.ListLegacyMessages(ordinary.Scope).Items.Single(m => m.LegacySeq == ordinary.LegacySeq) is { RawText: "講太快了", Reaction: null } &&
                ReactionPreviewPolicy.HistoryLabel(null, false) is null &&
                ReactionPreviewPolicy.Notification(true, null, false) == ReactionNotificationKind.Text,
                "RX-08 actual legacy typed caption without wire reaction ID remains ordinary text/event");
            Check(ReactionPreviewPolicy.Notification(true, ClassroomReactions.Find("stop-scrolling"), false) == ReactionNotificationKind.Image &&
                ReactionPreviewPolicy.HistoryLabel("stop-scrolling", false) is null &&
                ReactionPreviewPolicy.HistoryLabel("future-reaction", false) is null, "RX-08 peer sticker and unknown ID unchanged");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Teacher device: {checks} RX-07/08 migration, projection, fixed IP, explicit choice and real receive checks passed.");
        return checks;
    }
}
