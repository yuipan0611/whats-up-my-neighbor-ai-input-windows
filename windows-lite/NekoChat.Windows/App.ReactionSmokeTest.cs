using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunReactionMessagingChecksAsync(MessagingSmokeOptions options, CancellationToken cancellation)
    {
        pet.ReactionDisplayDevice = "disconnected-test-screen";
        string Ready(string value) => Path.Combine(options.Directory, value + ".ready");
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
            chat.RecipientIp.Text = "127.0.0.1";
            chat.Draft.Text = "太快了！！";
            chat.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => chat.SendButton.IsEnabled, cancellation);
            Require(chat.Draft.Text.Length == 0, "Keyword send did not follow ordinary draft behavior");
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
            "draft preserved for shortcuts; exact-text trigger; real TCP receipt/history; transparent 312x288 central display; " +
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
            Require(chat.History.Text.Contains("127.0.0.1："), "Chat lost source IP");
            if (captureName == "bob-too-fast")
                await UntilAsync(() => pet.ReactionCaption.Text.Contains("×2"), token);
            Capture(pet, Path.Combine(options.Directory, captureName + "-reaction.png"));
            if (clickReply)
            {
                chat.RecipientIp.Text = "192.168.1.254";
                pet.ReactionView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(chat.RecipientIp.Text == "127.0.0.1", "Reaction reply lost true source IP");
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
        chat.Draft.Text = "這份草稿要保留";
        chat.RecipientIp.Text = reaction.TeacherOnly ? "192.168.1.254" : "127.0.0.1";
        var button = reaction.TeacherOnly
            ? chat.TeacherReactions.Children.OfType<Button>().Single(b => (string)b.Tag == reaction.Id)
            : chat.ScrollReaction;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => chat.SendButton.IsEnabled, cancellation);
        Require(chat.Status.Text.Contains("已送達") && chat.History.Text.Contains("我 → 127.0.0.1：" + reaction.Text), "Reaction did not reach intended recipient");
        Require(chat.Draft.Text == "這份草稿要保留", "Reaction shortcut changed the draft");
    }
}
