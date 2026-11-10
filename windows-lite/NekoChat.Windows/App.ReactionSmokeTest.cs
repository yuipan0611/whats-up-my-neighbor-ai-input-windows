using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core;
using NekoChat.Core.Storage;
using NekoChat.Core.Roster;
using Microsoft.Data.Sqlite;

namespace NekoChat.Windows;

public partial class App
{
    private string? _reactionFixtureRowId;

    private async Task ConfigureReactionFixtureAsync(bool teacher, string endpoint = "127.0.0.2")
    {
        var runtime = chatRuntime ?? throw new InvalidOperationException("Missing fixture runtime");
        Require(runtime.IsolatedData && runtime.ArchivedIdentityFixture && messagingPeerId is not null,
            "Reaction roster setup is restricted to the archived isolated fixture");
        Require(endpoint is "127.0.0.1" or "127.0.0.2", "Fixture routes must remain loopback");
        await runtime.Worker.Run(store => {
            if (_reactionFixtureRowId is null)
            {
                var added = store.SaveManualRosterEntry(null, new("合成課堂對象", null, teacher, "192.168.255.250"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                Require(added.Changed, "Synthetic reaction roster creation failed");
                _reactionFixtureRowId = added.EntryId!;
                Require(store.BindPairedRosterPeer(_reactionFixtureRowId, messagingPeerId,
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).Changed, "Synthetic reaction peer binding failed");
            }
            // Synthetic loopback is deliberately not accepted by the product roster editor.
            // .2 is the destination, while incoming legacy traffic remains a separate .1 source.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(runtime.DataDirectory, "nekochat.db"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE RosterEntry SET endpoint=@endpoint,isTeacher=@teacher WHERE id=@id";
            command.Parameters.AddWithValue("@endpoint", endpoint); command.Parameters.AddWithValue("@teacher", teacher ? 1 : 0);
            command.Parameters.AddWithValue("@id", _reactionFixtureRowId);
            Require(command.ExecuteNonQuery() == 1, "Synthetic reaction roster missing");
            return true;
        });
        await chat.SmokeOpenEntryAsync(entry => entry.PeerId == messagingPeerId);
        Require(chat.SmokeSelectedEntry is { } selected && selected.IsTeacher == teacher && selected.Endpoint == endpoint,
            "Reaction fixture did not select the actual roster snapshot");
    }

    private async Task RunReactionMessagingChecksAsync(MessagingSmokeOptions options, CancellationToken cancellation)
    {
        await ConfigureReactionFixtureAsync(false);
        pet.ReactionDisplayDevice = "disconnected-test-screen";
        string Ready(string value) => Path.Combine(options.Directory, value + ".ready");
        // RX-01: prove the unmodified ON default through real legacy TCP before any toggle click.
        if (options.Role == "alice")
        {
            await UntilAsync(() => File.Exists(Ready("privacy-default")), cancellation);
            await SendReactionAsync(ClassroomReactions.Find("too-fast")!, cancellation);
            await UntilAsync(() => File.Exists(Ready("preview-off")), cancellation);
            await SendReactionAsync(ClassroomReactions.Find("too-fast")!, cancellation);
            await UntilAsync(() => File.Exists(Ready("privacy-verified")), cancellation);
        }
        else
        {
            Require(settings.ShowReactionPreviews && !settings.ReactionPreviewExplicitChoice, "RX-01 default must not be pre-opted-in");
            File.WriteAllText(Ready("privacy-default"), "ready");
            await UntilAsync(() => pet.ReactionActive && pet.ReactionImage.Source is not null && pet.ReactionCaption.Text.StartsWith("講太快了", StringComparison.Ordinal), cancellation);
            Capture(pet, Path.Combine(options.Directory, "bob-default-reaction.png"));
            await UntilAsync(() => !pet.ReactionActive, cancellation);
            chat.SmokeApplySettings(s => s with { ShowReactionPreviews = false, ReactionPreviewExplicitChoice = true });
            Require(!settings.ShowReactionPreviews && settings.ReactionPreviewExplicitChoice, "RX-04 explicit OFF did not persist");
            // Remove earlier signed-chat reminders before allowing the OFF control message.
            // An old visible bubble must not satisfy the next receive assertion.
            while (pet.Bubble.Visibility == Visibility.Visible) pet.AdvanceChatNotice();
            File.WriteAllText(Ready("preview-off"), "ready");
            await UntilAsync(() => pet.Bubble.Visibility == Visibility.Visible && pet.BubbleText.Text.Contains("新訊息"), cancellation);
            Require(!pet.ReactionActive && !pet.BubbleText.Text.Contains("太快") && !pet.BubbleText.Text.Contains("127.0.0.1"), "Explicit-OFF legacy reaction leaked body or endpoint");
            pet.Bubble.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => chat.SmokeSelectedEntry?.LegacyScope?.UnknownSourceIp == "127.0.0.1" && chat.History.Rows.Any(r => r.Legacy), cancellation);
            Require(chat.SmokeSelectedEntry is { PeerId: null } && chat.History.Rows.All(r => r.Legacy), "Legacy notification merged into the signed peer history");
            File.WriteAllText(Ready("privacy-verified"), "ready");
        }
        // Restore ON through the settings path before the unchanged asset/timing checks.
        chat.SmokeApplySettings(s => s with { ShowReactionPreviews = true, ReactionPreviewExplicitChoice = true });
        Require(settings.ShowReactionPreviews && settings.ReactionPreviewExplicitChoice, "Explicit reaction-preview preference did not persist");
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
            while (pet.Bubble.Visibility == Visibility.Visible) pet.AdvanceChatNotice();
            File.WriteAllText(Ready("receive-keyword"), "ready");
            await UntilAsync(() => pet.Bubble.Visibility == Visibility.Visible && !pet.ReactionActive, cancellation);
            Require(!pet.ReactionActive && pet.ReactionImage.Source is null, "RX-08 typed caption without reaction ID must remain ordinary text");
            var typed = await chatRuntime!.Worker.Run(s => s.ListLegacyHistories());
            Require(typed.Any(h => h.LastText == "太快了！！" && h.LastReaction is null), "RX-08 ordinary typed caption must retain null reaction metadata");
            File.WriteAllText(Ready("restored-keyword"), "ready");
            await UntilAsync(() => File.Exists(Ready("receive-reverse")), cancellation);
            await SendReactionAsync(ClassroomReactions.Find("stop-scrolling")!, cancellation);
            await UntilAsync(() => File.Exists(Ready("restored-reverse")), cancellation);
        }
        File.WriteAllText(Path.Combine(options.Directory, options.Role + "-reactions-result.txt"),
            "PASS: actual teacher WPF buttons reject a selected classmate and send only from the selected teacher chat; peer sticker popup works both ways; " +
            "D-32 default shows reaction image; explicit OFF hides reaction content/IP; explicit ON restored; draft preserved for shortcuts; D-33 no-ID typed caption stays ordinary text; real legacy TCP courtesy ACK/separate unverified history; transparent 312x288 central display; " +
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
        if (reaction.TeacherOnly)
        {
            await ConfigureReactionFixtureAsync(false);
            chat.Draft.Text = "這份草稿要保留";
            int unintendedSends = 0;
            void Observe(string _, ClassroomReaction __) => unintendedSends++;
            chat.ReactionRequested += Observe;
            try
            {
                var blocked = chat.TeacherReactions.Children.OfType<Button>().Single(b => (string)b.Tag == reaction.Id);
                blocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(unintendedSends == 0 && chat.Status.Text.Contains("對應的聊天室") && chat.Draft.Text == "這份草稿要保留",
                    "Teacher reaction escaped the selected classmate guard");
            }
            finally { chat.ReactionRequested -= Observe; }
        }
        await ConfigureReactionFixtureAsync(reaction.TeacherOnly);
        chat.Draft.Text = "這份草稿要保留";
        chat.RecipientIp.Text = "192.168.1.254"; // Editing the legacy hidden field cannot retarget the selected chat.
        if (reaction.TeacherOnly)
        {
            var button = chat.TeacherReactions.Children.OfType<Button>().Single(b => (string)b.Tag == reaction.Id);
            Require(button.IsVisible && button.IsEnabled, "Selected teacher reaction must be visible and enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else
        {
            Require(chat.StickerButton.IsVisible && chat.StickerButton.IsEnabled, "Selected classmate sticker action is unavailable");
            chat.StickerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContextMenu? menu = null;
            await UntilAsync(() => {
                menu = PresentationSource.CurrentSources.OfType<HwndSource>().Where(source => source.RootVisual is not null)
                    .SelectMany(source => SmokeVisuals<ContextMenu>(source.RootVisual!)).SingleOrDefault(value => value.PlacementTarget == chat.StickerButton && value.IsOpen);
                return menu is not null && menu.Items.OfType<MenuItem>().Any(item => item.IsVisible);
            }, cancellation);
            var sticker = menu!.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, reaction.Id));
            Require(sticker.IsEnabled, "Peer sticker menu item is disabled");
            sticker.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        }
        await UntilAsync(() => chat.SmokeSendIdle, cancellation);
        Require(chat.CanSendReaction && chat.Status.Text.Contains("舊版・已送出") && !chat.History.Rows.Any(r => r.Outgoing && r.RawText == reaction.Text), "Reaction courtesy ACK failed or invented an outgoing signed message: " + chat.Status.Text);
        Require(chat.Draft.Text == "這份草稿要保留", "Reaction shortcut changed the draft");
    }
}
