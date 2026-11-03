using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunReactionMessagingChecksAsync(MessagingSmokeOptions options, CancellationToken cancellation)
    {
        pet.ReactionDisplayDevice = "disconnected-test-screen";
        string Ready(string value) => Path.Combine(options.Directory, value + ".ready");
        // First prove the unmodified privacy default on a real legacy classroom reaction.
        if (options.Role == "alice")
        {
            await UntilAsync(() => File.Exists(Ready("privacy-default")), cancellation);
            await SendReactionAsync(ClassroomReactions.Find("too-fast")!, cancellation);
            await UntilAsync(() => File.Exists(Ready("privacy-verified")), cancellation);
        }
        else
        {
            File.WriteAllText(Ready("privacy-default"), "ready");
            await UntilAsync(() => pet.Bubble.Visibility == Visibility.Visible && pet.BubbleText.Text.Contains("新訊息"), cancellation);
            Require(!pet.ReactionActive && !pet.BubbleText.Text.Contains("太快") && !pet.BubbleText.Text.Contains("127.0.0.1"), "Default legacy reaction leaked body or endpoint");
            pet.Bubble.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => chat.SmokeSelectedEntry?.LegacyScope?.UnknownSourceIp == "127.0.0.1" && chat.History.Rows.Any(r => r.Legacy), cancellation);
            Require(chat.SmokeSelectedEntry is { PeerId: null } && chat.History.Rows.All(r => r.Legacy), "Legacy notification merged into the signed peer history");
            File.WriteAllText(Ready("privacy-verified"), "ready");
        }
        // Deliberately opt in through the actual settings control before asset/timing checks.
        chat.ReactionPreviewToggle.IsChecked = true;
        chat.ReactionPreviewToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(settings.ShowReactionPreviews, "Explicit reaction-preview preference did not persist");
        File.WriteAllText(Ready(options.Role + "-opt-in"), "ready");
        await UntilAsync(() => File.Exists(Ready((options.Role == "alice" ? "bob" : "alice") + "-opt-in")), cancellation);
        if (options.Role == "alice")
        {
            foreach (var reaction in ClassroomReactions.All)
            {
                await UntilAsync(() => File.Exists(Ready("receive-" + reaction.Id)), cancellation);
                await SendReactionAsync(reaction, cancellation);
                if (reaction.Id == "too-fast") await SendReactionAsync(reaction, cancellation);
                await UntilAsync(() => File.Exists(Ready("restored-" + reaction.Id)), cancellation);
            }
            await UntilAsync(() => File.Exists(Ready("receive-keyword")), cancellation);
            string draft = chat.Draft.Text;
            // Explicit old-client compatibility fixture; the product never falls back to unsigned ordinary private sends.
            await DirectLanMessaging.SendDirectAsync("127.0.0.1", "太快了！！", options.PeerPort);
            Require(chat.Draft.Text == draft, "Legacy compatibility fixture changed the actual composer");
            await UntilAsync(() => File.Exists(Ready("restored-keyword")), cancellation);
            File.WriteAllText(Ready("receive-reverse"), "ready");
            await VerifyReactionAsync(ClassroomReactions.Find("stop-scrolling")!, "alice-reverse", cancellation, clickReply: true);
            File.WriteAllText(Ready("restored-reverse"), "ready");
        }
        else
        {
            foreach (var reaction in ClassroomReactions.All)
            {
                File.WriteAllText(Ready("receive-" + reaction.Id), "ready");
                await VerifyReactionAsync(reaction, "bob-" + reaction.Id, cancellation);
                File.WriteAllText(Ready("restored-" + reaction.Id), "ready");
            }
            File.WriteAllText(Ready("receive-keyword"), "ready");
            await VerifyReactionAsync(ClassroomReactions.Find("too-fast")!, "bob-keyword", cancellation);
            File.WriteAllText(Ready("restored-keyword"), "ready");
            await UntilAsync(() => File.Exists(Ready("receive-reverse")), cancellation);
            await SendReactionAsync(ClassroomReactions.Find("stop-scrolling")!, cancellation);
            await UntilAsync(() => File.Exists(Ready("restored-reverse")), cancellation);
        }
        File.WriteAllText(Path.Combine(options.Directory, options.Role + "-reactions-result.txt"),
            "PASS: actual five WPF reaction buttons; teacher shortcuts ignore selected classmate; fifth sticker works both ways; " +
            "default notification hides reaction content/IP; explicit preview opt-in; draft preserved for shortcuts; old-client exact-text compatibility fixture; real legacy TCP courtesy ACK/separate unverified history; transparent 312x288 central display; " +
            "two-second deadline; duplicate count; no source IP in popup; original HWND/position/size/art restored; click replies to source.");

        async Task VerifyReactionAsync(ClassroomReaction reaction, string captureName, CancellationToken token, bool clickReply = false)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await UntilAsync(() => pet.ReactionActive && pet.ReactionCaption.Text.StartsWith(reaction.Text, StringComparison.Ordinal), token);
            var origin = pet.ReactionReturnPlacement!;
            var source = pet.Cat.Source;
            var coat = pet.CoatMenuItems.FirstOrDefault(item => item.Value.IsChecked).Key;
            Require(pet.Width == 312 && pet.Height == 288 && pet.ReactionImage.Source is not null, "Reaction size/image is incorrect");
            Require(!pet.IsActive && pet.NormalPet.Visibility == Visibility.Collapsed, "Reaction activates or duplicates the normal pet");
            Require(Windows.OfType<PetWindow>().Count() == 1, "Reaction created a second pet window");
            Require(!pet.ReactionCaption.Text.Contains("127.0.0.1") &&
                !AutomationProperties.GetName(pet.ReactionView).Contains("127.0.0.1") && pet.ReactionView.ToolTip is null,
                "Source IP leaked outside the chat");
            var histories = await chatRuntime!.Worker.Run(s => s.ListLegacyHistories());
            Require(histories.Any(h => h.Scope.UnknownSourceIp == "127.0.0.1" && h.LastText == (captureName == "bob-keyword" ? "太快了！！" : reaction.Text)), "Actual legacy receive was not durably stored under its unverified source");
            if (captureName == "bob-too-fast")
                await UntilAsync(() => pet.ReactionCaption.Text.Contains("×2"), token);
            string capturePath = Path.Combine(options.Directory, captureName + "-reaction.png");
            Capture(pet, capturePath);
            using (var captureFile = File.OpenRead(capturePath))
            {
                var frame = new PngBitmapDecoder(captureFile, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
                bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
                int visibleWhite = 0;
                for (int y = 248; y < bgra.PixelHeight; y++)
                    for (int x = 0; x < bgra.PixelWidth; x++)
                    {
                        int i = (y * bgra.PixelWidth + x) * 4;
                        if (pixels[i] > 220 && pixels[i + 1] > 220 && pixels[i + 2] > 220 && pixels[i + 3] > 220) visibleWhite++;
                    }
                Require(visibleWhite > 20, "Caption has no readable white fill over its black outline");
            }
            if (clickReply)
            {
                pet.ReactionView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => chat.SmokeSelectedEntry?.LegacyScope?.UnknownSourceIp == "127.0.0.1" && chat.History.Rows.Any(r => r.Legacy && r.RawText == reaction.Text), token);
                Require(chat.SmokeSelectedEntry is { PeerId: null } && chat.History.Rows.All(r => r.Legacy), "Reaction reply selected a signed peer by matching IP");
            }
            else
            {
                await UntilAsync(() => !pet.ReactionActive, token);
                Require(elapsed.Elapsed >= TimeSpan.FromSeconds(1.8) && elapsed.Elapsed < TimeSpan.FromSeconds(4),
                    "Reaction did not retain its two-second timing");
            }
            Require(pet.NormalPet.Visibility == Visibility.Visible && pet.ReactionView.Visibility == Visibility.Collapsed &&
                pet.Width == 280 && pet.Height == 310 &&
                pet.CoatMenuItems.FirstOrDefault(item => item.Value.IsChecked).Key == coat &&
                (coat is not null || ReferenceEquals(source, pet.Cat.Source)), "Original pet was not restored");
            var restored = DisplayScreens.Capture(new WindowInteropHelper(pet).Handle);
            Require(restored == origin, "Pet physical screen position changed after reaction");
        }
    }

    private async Task SendReactionAsync(ClassroomReaction reaction, CancellationToken cancellation)
    {
        await chat.SmokeOpenEntryAsync(e => e.PeerId == messagingPeerId);
        chat.Draft.Text = "這份草稿要保留";
        chat.RecipientIp.Text = reaction.TeacherOnly ? "192.168.1.254" : "127.0.0.1";
        var button = reaction.TeacherOnly
            ? chat.TeacherReactions.Children.OfType<Button>().Single(b => (string)b.Tag == reaction.Id)
            : chat.ScrollReaction;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => chat.SmokeSendIdle, cancellation);
        Require(chat.CanSendReaction && chat.Status.Text.Contains("舊版・已送出") && !chat.History.Rows.Any(r => r.Outgoing && r.RawText == reaction.Text), "Reaction courtesy ACK failed or invented an outgoing signed message");
        Require(chat.Draft.Text == "這份草稿要保留", "Reaction shortcut changed the draft");
    }
}
