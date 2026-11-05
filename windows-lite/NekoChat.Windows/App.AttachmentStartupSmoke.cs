using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunAttachmentStartupChecksAsync(string directory)
    {
        Require(chatRuntime?.IsolatedData == true, "Startup fault checks require an isolated app");
        var checks = new List<string>();
        void Check(bool value, string name) { Require(value, name); checks.Add(name); }
        var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (string fault in new[] { "blocked-directory", "locked-part" })
        {
            string root = Path.Combine(directory, "attachment-startup-" + fault);
            string data = Path.Combine(root, "data"), vault = Path.Combine(data, "attachments");
            Directory.CreateDirectory(data);
            const string caption = "原本的文字與附件必須保留";
            using (var seed = ChatStore.Open(new(data)))
            {
                var state = seed.GetClassroomDraftState(principal);
                byte[] pending = Encoding.UTF8.GetBytes("pending");
                Check(seed.SaveAttachmentDraft(principal, new(Guid.NewGuid().ToString("N"), "待傳.txt", pending.Length,
                    Convert.ToHexStringLower(SHA256.HashData(pending))), state.Epoch), fault + ": seed attachment draft");
                Check(seed.SaveClassroomDraft(principal, caption, state.Epoch, state.Revision + 1).Saved, fault + ": seed caption");
            }
            FileStream? locked = null;
            if (fault == "blocked-directory") File.WriteAllText(vault, "external-file-must-survive");
            else
            {
                Directory.CreateDirectory(vault);
                locked = new FileStream(Path.Combine(vault, Guid.NewGuid().ToString("N") + ".part"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                locked.WriteByte(42); locked.Flush(true);
            }
            var settings = Settings.ForUiTest(root) with { ClassRosterInitialized = true };
            string peerRoot = root + "-peer";
            var peerSettings = Settings.ForUiTest(peerRoot) with { ClassRosterInitialized = true };
            try
            {
                await using var runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
                await using var peer = await ChatRuntime.OpenAsync(new(peerSettings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
                runtime.Start(); peer.Start();
                Check(runtime.CanSend && runtime.Attachments is null && runtime.ClassroomFiles is null && runtime.AttachmentWarning is not null,
                    fault + ": text runtime survives attachment storage failure");
                var probe = new ChatWindow(settings);
                await probe.AttachRuntimeAsync(runtime, peer.Listener.Port); probe.SetReceiverState(true, runtime.Listener.Port, null);
                await probe.SmokeOpenEntryAsync(entry => entry.ConversationId == principal.Value);
                await UntilAsync(() => probe.AttachmentDraftPanel.Visibility == Visibility.Visible, deadline.Token);
                Check(probe.StorageStatus.Text.Contains("附件") && !probe.AttachFileButton.IsEnabled, fault + ": warning and attachment controls");
                Check(probe.AttachmentDraftPanel.Visibility == Visibility.Visible && probe.Draft.Text == caption && !probe.SendButton.IsEnabled,
                    fault + ": pending attachment remains visible and cannot become a text send");
                probe.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => probe.SmokeSendIdle, deadline.Token);
                Check(await runtime.ReadAttachmentDraftAsync(principal) is not null &&
                    await peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Count == 0), fault + ": blocked send preserves draft");
                probe.RemoveAttachmentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => probe.AttachmentDraftPanel.Visibility == Visibility.Collapsed, deadline.Token);
                const string sent = "附件故障時仍能傳送中文純文字 🐈";
                probe.Draft.Text = sent;
                await UntilAsync(() => probe.SendButton.IsEnabled, deadline.Token);
                Check(probe.SendButton.IsEnabled, fault + ": text send available after explicit attachment removal");
                probe.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => probe.SmokeSendIdle && probe.Draft.Text.Length == 0, deadline.Token);
                while (!await peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Any(row => row.RawText == sent))) await Task.Delay(25, deadline.Token);
                Check(await peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Single().RawText == sent), fault + ": real TCP text stored");
                var other = new ChatWindow(peerSettings);
                await other.AttachRuntimeAsync(peer, runtime.Listener.Port); other.SetReceiverState(true, peer.Listener.Port, null);
                await other.SmokeOpenEntryAsync(entry => entry.ConversationId == principal.Value);
                const string reply = "文字回覆也收到";
                other.Draft.Text = reply;
                await UntilAsync(() => other.SendButton.IsEnabled, deadline.Token);
                other.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => other.SmokeSendIdle && other.Draft.Text.Length == 0, deadline.Token);
                while (!await runtime.Worker.Run(store => store.ListClassroomMessages(principal).Items.Any(row => row.RawText == reply))) await Task.Delay(25, deadline.Token);
                Check(await runtime.Worker.Run(store => store.ListClassroomMessages(principal).Items.Any(row => row.RawText == reply)), fault + ": real TCP reply stored");
                string source = Path.Combine(peerRoot, "attachment.txt"); File.WriteAllText(source, "bytes");
                await peer.Attachments!.AddDraftAsync(principal, source);
                var state = await peer.Worker.Run(store => store.GetClassroomDraftState(principal));
                bool unsupported = false;
                try { await peer.Attachments.SendAsync(principal, "", state.Epoch, state.Revision, runtime.Listener.Port); }
                catch (AttachmentException error) when (error.Code == "unsupported_version") { unsupported = true; }
                Check(unsupported && await peer.ReadAttachmentDraftAsync(principal) is not null,
                    fault + ": no attachment capability or false delivery; sender draft retained");
                await probe.PrepareForShutdownAsync(); await other.PrepareForShutdownAsync();
                probe.DetachRuntime(); other.DetachRuntime();
            }
            finally { locked?.Dispose(); }
            if (fault == "blocked-directory")
            {
                Check(File.ReadAllText(vault) == "external-file-must-survive", "fallback never deletes a conflicting external file");
                File.Delete(vault); // Explicitly repair only this disposable fixture.
            }
            await using var recovered = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            Check(recovered.Attachments is not null && recovered.ClassroomFiles is not null && recovered.AttachmentWarning is null,
                fault + ": attachment services recover on restart after repair");
            Check(await recovered.Worker.Run(store => store.ListClassroomMessages(principal).Items.Count == 2), fault + ": text history survives recovery");
        }
        string corruptRoot = Path.Combine(directory, "corrupt-db"); Directory.CreateDirectory(Path.Combine(corruptRoot, "data"));
        byte[] corrupt = Encoding.UTF8.GetBytes("not a SQLite database");
        string database = Path.Combine(corruptRoot, "data", "nekochat.db"); File.WriteAllBytes(database, corrupt);
        bool rejected = false;
        try { await using var invalid = await ChatRuntime.OpenAsync(new(Settings.ForUiTest(corruptRoot), SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true); }
        catch (StoreException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(database).SequenceEqual(corrupt), "corrupt database remains fail-closed and unmodified");
        File.WriteAllText(Path.Combine(directory, "attachment-startup-result.json"), JsonSerializer.Serialize(new { passed = true, checks, loopbackOnly = true, dailyProfileUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
