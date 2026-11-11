using System.IO;
using System.Text.Json;
using System.Windows;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private async Task<int> RunUpdateHandoffSmokeAsync(string directory)
    {
        string output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        async Task DismissOwnFailureDialogAsync()
        {
            await Task.Run(async () => {
                IntPtr dialog;
                while ((dialog = DndFindWindow("#32770", "無法完成存稿")) == IntPtr.Zero)
                    await Task.Delay(25, deadline.Token);
                DndGetWindowThreadProcessId(dialog, out int pid);
                if (pid != Environment.ProcessId) throw new InvalidOperationException("Unexpected dialog owner");
                DndPostMessage(dialog, 0x111, new IntPtr(1), IntPtr.Zero);
            }, deadline.Token);
        }
        try
        {
            settings = Settings.ForUiTest(Path.Combine(output, "isolated-profile")) with { ClassRosterInitialized = true };
            isolatedData = true; startupPort = 0; nativeDndIsolated = true;
            startupSource = new(settings, SettingsSourceStatus.Loaded);
            chat = new(settings); pet = new();
            chat.Show(); pet.Show();
            await StartDataAsync();
            var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
            await chatRuntime!.Worker.Run(s => s.SaveClassroomDraft(principal, "已儲存的草稿", 0, 1));
            await chat.SmokeOpenEntryAsync(e => e.ConversationId == principal.Value);
            chat.Draft.Text = "更新前必須保留的文字";
            chat.BeginClosing();
            Check(chat.HasUnsavedDrafts && chat.IsComposing, "real dirty draft defers automatic restart");
            var oldRuntime = chatRuntime;
            chat.DetachRuntime(); chatRuntime = null;
            await oldRuntime.DisposeAsync();
            int launches = 0;
            var dialog = DismissOwnFailureDialogAsync();
            await RequestShutdownAsync(() => launches++);
            await dialog;
            Check(launches == 0 && !closingApp && !Dispatcher.HasShutdownStarted,
                "real draft-save failure aborts shutdown before installer launch");
            Check(chat.Draft.Text == "更新前必須保留的文字" && chatRuntime is not null,
                "aborted shutdown retains text and reopens isolated storage");

            chat.Draft.Text = "成功存稿後才可交接";
            bool committedBeforeLaunch = false;
            dialog = DismissOwnFailureDialogAsync();
            await RequestShutdownAsync(() => {
                launches++;
                using var stored = ChatStore.Open(new(Path.Combine(ChatRuntime.RootDirectory(settings, true), "data")));
                committedBeforeLaunch = stored.GetClassroomDraftState(principal).RawText == "成功存稿後才可交接";
                throw new IOException("Isolated installer-launch failure");
            });
            await dialog;
            Check(committedBeforeLaunch, "installer launch callback observes committed draft after runtime disposal");
            Check(launches == 1 && !closingApp && chatRuntime is not null && !Dispatcher.HasShutdownStarted,
                "installer-launch failure restores running app instead of exiting");
            pet.NotifyChat("保留的未讀通知", "127.0.0.1", "測試");
            pet.NotifyUpdateReady("更新", () => { });
            pet.DismissUpdateNotice();
            string resumed = pet.BubbleText.Text;
            pet.DismissUpdateNotice();
            Check(resumed.Contains("保留的未讀通知") && pet.BubbleText.Text == resumed,
                "repeated update dismissal preserves resumed unread notice");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            closingApp = true;
            if (chatRuntime is not null) await chatRuntime.DisposeAsync();
            chatRuntime = null;
            chat?.Close(); pet?.Close();
        }
        File.WriteAllText(Path.Combine(output, "update-handoff-result.json"), JsonSerializer.Serialize(new {
            passed = failure is null, dailyProfileUsed = false, loopbackOnly = true, checks, failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
