using System.Text.Json;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using NekoChat.Windows;

namespace NekoChat.Tests;

internal static class ReactionPreferenceChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string label)
        { checks++; if (!condition) throw new InvalidOperationException(label); }
        string root = Path.Combine(Path.GetTempPath(), "neko-reaction-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            Check(new Settings().ShowReactionPreviews && !new Settings().ReactionPreviewExplicitChoice &&
                Settings.LoadWithStatus(path).Value.ShowReactionPreviews, "RX-01 new/missing settings default ON without explicit choice");
            foreach (string input in new[] { "{}", """{"ShowReactionPreviews":false}""", """{"ShowReactionPreviews":true}""",
                """{"sHoWrEaCtIoNpReViEwS":false,"ReactionDisplayDevice":"projector-2","TeacherIp":"10.0.0.7","Contacts":[{"Ip":"10.0.0.8","Name":"keep"}]}""" })
            {
                File.WriteAllText(path, input);
                byte[] before = File.ReadAllBytes(path);
                var migrated = Settings.LoadWithStatus(path);
                Check(migrated.Status == SettingsSourceStatus.Loaded && migrated.Value.ShowReactionPreviews &&
                    !migrated.Value.ReactionPreviewExplicitChoice && File.ReadAllBytes(path).SequenceEqual(before),
                    "RX-03 hidden-era missing/false/true migrates ON without writing the source during load");
                Check(migrated.Value.Save(), "RX-03 normal save persists normalized settings");
                var reloaded = Settings.LoadWithStatus(path).Value;
                Check(reloaded.ShowReactionPreviews && !reloaded.ReactionPreviewExplicitChoice,
                    "RX-03 normalized default survives save/reload without inventing explicit consent");
                if (input.Contains("projector-2", StringComparison.Ordinal))
                    Check(reloaded.ReactionDisplayDevice == "projector-2" && reloaded.TeacherIp == "10.0.0.7" &&
                        reloaded.Contacts.Single().Name == "keep", "RX-03 migration preserves monitor/teacher/contact settings");
            }
            foreach (bool choice in new[] { false, true })
            {
                var chosen = Settings.ForUiTest(root) with { ShowReactionPreviews = choice, ReactionPreviewExplicitChoice = true };
                Check(chosen.Save() && Settings.LoadWithStatus(path).Value is var result &&
                    result.ShowReactionPreviews == choice && result.ReactionPreviewExplicitChoice,
                    "RX-04 explicit ON/OFF survives repeated load and save");
                Check(Settings.LoadWithStatus(path).Value.Save() && Settings.LoadWithStatus(path).Value.ShowReactionPreviews == choice,
                    "RX-04 a subsequent normal save never rearms explicit OFF");
            }
            Check(ReactionPreviewPolicy.AfterLoad(false, false) && !ReactionPreviewPolicy.AfterLoad(false, true) &&
                ReactionPreviewPolicy.AfterLoad(true, true), "RX-03/RX-04 pure migration policy distinguishes hidden default and explicit OFF");
            foreach (var reaction in ClassroomReactions.All)
            {
                Check(ReactionPreviewPolicy.ShowImage(true, reaction, true) &&
                    ReactionPreviewPolicy.ShowImage(true, reaction, false) == !reaction.TeacherOnly &&
                    !ReactionPreviewPolicy.ShowImage(false, reaction, true),
                    "RX-05 four TeacherOnly/peer reactions and OFF selection: " + reaction.Id);
            }
            Check(!ReactionPreviewPolicy.ShowImage(true, null, true) && ClassroomReactions.All.Count(r => r.TeacherOnly) == 4 &&
                ClassroomReactions.Find("stop-scrolling") is { TeacherOnly: false, Text: "你再給我滑！" },
                "RX-05 unknown stays text, exact four teacher images and peer caption retained");

            using var store = ChatStore.Open(new(Path.Combine(root, "data")));
            const string source = "10.251.232.77";
            var sink = new ClassroomStoreSink(store);
            int messages = 0, legacy = 0;
            sink.MessageStored += (_, _, _) => messages++;
            sink.LegacyMessageStored += _ => legacy++;
            await sink.OnLegacyDirectAsync(source, new("你再給我滑！", "stop-scrolling"), () => true, default);
            Check(legacy == 1, "RX-06 positive control unblocked real sink emits a reaction notification");
            store.SetSourceBlocked(source, true);
            string Before() => JsonSerializer.Serialize(new { roster = store.GetRosterSnapshot(), histories = store.ListLegacyHistories(),
                inbox = store.GetClassroomInboxSnapshot() });
            string beforeBlocked = Before();
            foreach (var reaction in ClassroomReactions.All)
            {
                var reply = await sink.OnLegacyDirectAsync(source, new(reaction.Text, reaction.Id), () => true, default);
                var ack = await sink.OnMessageAsync(source, "10.251.232.78", new(ClassroomRequestKind.Message,
                    MessageId: Guid.NewGuid().ToString("D"), Session: ClassroomPacketWriter.NewToken(),
                    Text: reaction.Text, Reaction: reaction.Id), () => true, default);
                Check(reply is { StatusCode: 200 } && ack is { StatusCode: 200 } && legacy == 1 && messages == 0 &&
                    Before() == beforeBlocked, "RX-06 blocked legacy/classroom cat reactions ACK without body, auto-contact or notification: " + reaction.Id);
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Reaction preferences: {checks} RX-01/03/04/05/06 policy, real settings migration and blocked-receive checks passed.");
        return checks;
    }
}
