using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace NekoChat.Windows;

internal sealed record MessagingSmokeOptions(string Directory, string Role, int ListenPort, int PeerPort)
{
    internal static MessagingSmokeOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "--messaging-smoke-test") return null;
        if (args.Length != 5 || args[2] is not ("alice" or "bob") ||
            !int.TryParse(args[3], out int listen) || !int.TryParse(args[4], out int peer) ||
            listen is < 1024 or > 65535 || peer is < 1024 or > 65535 || listen == peer)
            throw new ArgumentException("Invalid messaging smoke-test arguments.");
        return new(Path.GetFullPath(args[1]), args[2], listen, peer);
    }
}

public partial class App
{
    private async Task RunMessagingSmokeTestAsync(MessagingSmokeOptions options)
    {
        string prefix = Path.Combine(options.Directory, options.Role);
        string other = options.Role == "alice" ? "bob" : "alice";
        const string first = "訊息驗收 A → B：下課見 🐈\n```csharp\nConsole.WriteLine(\"原文\");\n```";
        const string reply = "訊息驗收 B → A：收到，等你一起走 🐾";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            Require(chatRuntime?.CanSend == true && directListener?.Running == true, "Signed app runtime failed to start");
            Require(chat.IsVisible && pet.IsVisible && messagingPeerId is not null, "Actual fixture windows or pinned identity missing");
            Require(!settings.ShowReactionPreviews, "Reaction preview privacy default changed");
            var defaults = await chatRuntime!.Worker.Run(s => (Policy: s.GetReadReceiptPolicy(), Preferences: s.GetReadReceiptPreferences()));
            Require(chat.ReadReceiptToggle.IsChecked == true && defaults.Policy.Enabled && !defaults.Preferences.ExplicitChoice,
                "D-28 fresh read-receipt default must be ON in UI and storage, without a manual choice");
            // Preserve the opt-out privacy contract below. Both peers make an explicit UI choice
            // before either writes .ready, so no message can race the opt-out transition.
            chat.ReadReceiptToggle.IsChecked = false;
            chat.ReadReceiptToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (await chatRuntime.Worker.Run(s => s.GetReadReceiptPolicy().Enabled || !s.GetReadReceiptPreferences().ExplicitChoice))
                await Task.Delay(25, timeout.Token);
            Require(chat.ReadReceiptToggle.IsChecked == false && chat.ReadReceiptToggle.IsEnabled, "Explicit read-receipt opt-out did not reach the UI");
            await chat.SmokeOpenEntryAsync(e => e.PeerId == messagingPeerId);
            string conversation = chat.SmokeSelectedEntry!.ConversationId!;
            File.WriteAllText(prefix + ".ready", "ready");
            await UntilAsync(() => File.Exists(Path.Combine(options.Directory, other + ".ready")), timeout.Token);

            if (options.Role == "alice")
            {
                Send(first, enter: true);
                await VerifySentAsync(first);
                await VerifyIncomingAsync(reply);
            }
            else
            {
                await VerifyIncomingAsync(first);
                Send(reply, enter: false);
                await VerifySentAsync(reply);
            }
            var persisted = await chatRuntime!.Worker.Run(s => s.ListMessages(conversation).Items);
            Require(persisted.Count == 2 && persisted.All(m => m.RawText == first || m.RawText == reply) &&
                persisted.Single(m => m.Direction == "outgoing").RemoteStoredAt is not null &&
                persisted.Single(m => m.Direction == "outgoing").RemoteReadAt is null, "Signed storage ACK or explicit-opt-out remote read state incorrect");
            Require(await chatRuntime.Worker.Run(s => !s.GetReadReceiptPolicy().Enabled && s.GetReadReceiptPreferences().ExplicitChoice && s.ListPendingReadReceipts().Count == 0 && s.ListPendingClassroomReceipts().Count == 0),
                "Explicit opt-out must remain OFF and queue no outbound read receipts");
            Capture(chat, prefix + "-chat.png");
            File.WriteAllText(prefix + ".received", "received");
            await UntilAsync(() => File.Exists(Path.Combine(options.Directory, other + ".received")), timeout.Token);
            await RunReactionMessagingChecksAsync(options, timeout.Token);
            // Both receivers stay alive until every bidirectional check has finished.
            File.WriteAllText(prefix + ".network-complete", "ready");
            await UntilAsync(() => File.Exists(Path.Combine(options.Directory, other + ".network-complete")), timeout.Token);
            await ConfigureReactionFixtureAsync(false, "127.0.0.1");
            await chat.SmokeOpenEntryAsync(e => e.PeerId == messagingPeerId);

            // Keep a port reserved without serving HTTP: the genuine client must time out, never hit an unrelated process.
            using var unresponsive = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            unresponsive.Start(); chat.SmokeUsePeerPort(((System.Net.IPEndPoint)unresponsive.LocalEndpoint).Port);
            const string failed = "未確認送達的訊息必須保留在草稿";
            Send(failed, enter: false);
            await UntilAsync(() => chat.SmokeSendIdle, timeout.Token);
            Require(chat.Draft.Text == failed && chat.SendButton.IsEnabled, "Failed send lost draft or retry affordance");
            await UntilAsync(() => chat.History.Rows.Any(row => row.RawText == failed && row.AttemptId is not null && row.AttemptFailed), timeout.Token);
            var failure = chat.History.Rows.Single(row => row.RawText == failed);
            Require(failure.AttemptId is not null && failure.Delivery.Contains("未確認送達") && failure.RetrySend is not null &&
                !chat.History.Rows.Any(row => row.RawText == failed && row.AttemptId is null), "Failed attempt claimed delivered history or lost inline retry");
            Require(chat.Status.Visibility == Visibility.Collapsed, "Failed attempt must use its message card, not the global status banner");
            chat.History.ScrollIntoView(failure); chat.UpdateLayout();
            var status = SmokeVisuals<TextBlock>(chat.History).Single(block => block.Text == failure.Delivery);
            var measured = new FormattedText(status.Text, System.Globalization.CultureInfo.CurrentCulture,
                status.FlowDirection, new Typeface(status.FontFamily, status.FontStyle, status.FontWeight, status.FontStretch),
                status.FontSize, status.Foreground, VisualTreeHelper.GetDpi(status).PixelsPerDip) { MaxTextWidth = status.ActualWidth };
            Require(status.IsVisible && status.ActualHeight + 1 >= measured.Height, "Inline failure explanation is clipped");
            Capture(chat, prefix + "-failure.png");

            await chat.PrepareForShutdownAsync();
            string dataDirectory = chatRuntime.DataDirectory;
            await chatRuntime.DisposeAsync(); chatRuntime = null; directListener = null;
            using (var reopened = NekoChat.Core.Storage.ChatStore.Open(new(dataDirectory)))
            {
                Require(reopened.GetDraftState(conversation)?.RawText == failed, "Orderly close did not persist the failed draft");
                Require(reopened.ListMessages(conversation).Items.Count == 2, "Reopen lost or invented signed messages");
                Require(reopened.GetInboxSnapshot().Entries.Single(e => e.PeerId == messagingPeerId).Attempt is { TryCount: 1 }, "Unconfirmed send retried automatically or lost its attempt");
                Require(!reopened.GetReadReceiptPolicy().Enabled && reopened.GetReadReceiptPreferences().ExplicitChoice,
                    "Explicit read-receipt opt-out was not preserved on reopen");
            }
            File.WriteAllText(prefix + "-result.txt",
                "PASS: two independent real WPF processes; archived signed fixture with synthetic identity pins and D-06 L=65536; Enter/button → SQLite attempt → signed loopback HTTP/TCP → persisted incoming message → verified storage ACK → durable draft clear; " +
                "Unicode/code raw text, default name/new-message notification without body/IP, opaque peer reply context, D-28 default ON verified before explicit UI opt-out; no remote read or queued receipts while opted out; persisted explicit choice, separate unverified legacy history; timeout retains draft and one manual attempt; orderly close/reopen. " +
                "Same-host loopback only; no classroom LAN, real pairing consent or product L decision is established.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(prefix + "-result.txt", "FAIL: " + ex);
            Environment.Exit(1);
        }

        void Send(string text, bool enter)
        {
            chat.Draft.Text = text;
            Require(chat.SendButton.IsEnabled, "Actual composer was not ready");
            if (enter)
                chat.Draft.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chat),
                    Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else chat.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        async Task VerifySentAsync(string text)
        {
            await UntilAsync(() => chat.SmokeSendIdle && chat.History.Rows.Any(r => r.Outgoing && !r.Legacy && r.RawText == text && r.Delivery == "對方已儲存"), timeout.Token);
            Require(chat.Draft.Text.Length == 0 && !chat.SendButton.IsEnabled, "Verified ACK did not clear draft or empty composer remained sendable");
        }
        async Task VerifyIncomingAsync(string text)
        {
            await UntilAsync(() => chat.History.Rows.Any(r => !r.Outgoing && !r.Legacy && r.RawText == text) && pet.Bubble.Visibility == Visibility.Visible, timeout.Token);
            Require(pet.BubbleText.Text.Contains("新訊息") && !pet.BubbleText.Text.Contains(text) &&
                !pet.BubbleText.Text.Contains("127.0.0.1") && !pet.ReactionActive, "Default notification leaked content or endpoint");
            Capture(pet, prefix + "-bubble.png");
            pet.Bubble.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => chat.SmokeSelectedEntry?.PeerId == messagingPeerId && chat.History.Text.Contains(text), timeout.Token);
            Require(chat.SmokeSelectedEntry.LegacyScope is null, "Signed reply selected an unverified legacy conversation");
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken cancellation)
    {
        while (!predicate()) await Task.Delay(50, cancellation);
    }

    private static IEnumerable<T> SmokeVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in SmokeVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
