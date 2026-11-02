using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NekoChat.Windows;

internal static class ChatLayoutChecks
{
    internal static void Run(ChatWindow chat, string directory)
    {
        // Work areas are physical pixels, including negative-origin secondary monitors.
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var work = new NativeRect { Left = -1366, Top = 40, Right = 0, Bottom = 728 };
            var original = new NativeRect { Left = -1000, Top = 100, Right = -1000 + (int)(480 * scale), Bottom = 100 + (int)(740 * scale) };
            var fit = DisplayScreens.ConstrainToWorkArea(original, work);
            Require(fit.Left >= work.Left && fit.Top >= work.Top && fit.Right <= work.Right && fit.Bottom <= work.Bottom,
                "Window must fit the work area at every DPI");
            Require(fit.Width == Math.Min(original.Width, work.Width) && fit.Height == Math.Min(original.Height, work.Height),
                "Fitting must preserve size unless the work area is smaller");
            var again = DisplayScreens.ConstrainToWorkArea(fit, work);
            Require(again.Left == fit.Left && again.Top == fit.Top && again.Right == fit.Right && again.Bottom == fit.Bottom,
                "Repeated fitting must be stable");
        }

        // Invisible DWM borders (7px at 100%) stick out of the work area for edge-touching and
        // snapped windows. Those must stay put; only the visible frame is constrained.
        {
            var work = new NativeRect { Left = 0, Top = 0, Right = 1366, Bottom = 728 };
            var snappedFrame = new NativeRect { Left = 0, Top = 0, Right = 683, Bottom = 728 };
            var snapped = new NativeRect { Left = -7, Top = 0, Right = 690, Bottom = 735 };
            var kept = DisplayScreens.FitWindowToWorkArea(snapped, snappedFrame, work);
            Require(kept.Left == snapped.Left && kept.Top == snapped.Top && kept.Right == snapped.Right && kept.Bottom == snapped.Bottom,
                "Snapped/edge windows must not be moved because of invisible borders");
            var offFrame = new NativeRect { Left = 1000, Top = 500, Right = 1480, Bottom = 1240 };
            var off = new NativeRect { Left = 993, Top = 500, Right = 1487, Bottom = 1247 };
            var moved = DisplayScreens.FitWindowToWorkArea(off, offFrame, work);
            // Visible frame 480x740 is clamped to (886,0)-(1366,728); the 7px borders are added back.
            Require(moved.Left == 879 && moved.Top == 0 && moved.Right == 1373 && moved.Bottom == 735,
                $"Off-screen frame must be fitted by its visible edges: {moved.Left},{moved.Top},{moved.Right},{moved.Bottom}");
        }

        var visual = (FrameworkElement)chat.Content;
        // Native WPF geometry only: this runs on Windows, not during a Mac cross-build.
        foreach (var size in new[] { new Size(424, 320), new Size(440, 550), new Size(800, 500), new Size(1100, 700), new Size(900, 1000), new Size(424, 320) })
        {
            chat.SetReceiverState(false, 8768, new string('測', 200)); chat.SetStatus(new string('試', 200));
            chat.ShowConversationForLayoutCheck(); visual.Measure(size); visual.Arrange(new Rect(new Point(), size)); visual.UpdateLayout();
            chat.ShowConversationForLayoutCheck(); visual.UpdateLayout(); var available = new Rect(new Point(), visual.RenderSize);
            foreach (var control in new FrameworkElement[] { chat.Draft, chat.SendButton, chat.History })
            {
                var bounds = control.TransformToAncestor(visual).TransformBounds(new Rect(control.RenderSize));
                Require(control.ActualHeight > 0 && available.Contains(bounds), control.Name + " must stay visible in the client area");
            }
            Require(chat.Draft.ActualHeight >= 55 && chat.SendButton.ActualHeight >= 40, "Input and send action cannot be squeezed away");
            if (size.Width <= 700)
            {
                Require(chat.InboxPane.Visibility == Visibility.Collapsed && chat.BackToInbox.Visibility == Visibility.Visible, "Narrow conversation must expose Inbox navigation");
                chat.ShowInboxForLayoutCheck(); visual.UpdateLayout();
                Require(chat.InboxPane.Visibility == Visibility.Visible && chat.ConversationPane.Visibility == Visibility.Collapsed, "Narrow Inbox must replace conversation pane");
                chat.ShowConversationForLayoutCheck(); visual.UpdateLayout();
            }
            else Require(chat.InboxPane.Visibility == Visibility.Visible && chat.ConversationPane.Visibility == Visibility.Visible, "Wide view must show both panes");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, $"chat-layout-{size.Width}x{size.Height}.png"))) encoder.Save(file);
            chat.ShowSettingsForLayoutCheck(true); visual.UpdateLayout();
            Require(chat.SettingsScroll.ActualHeight > 0, "Settings must have a reachable scrolling viewport");
            var target = chat.ReactionDisplay.TransformToAncestor(chat.SettingsScroll).TransformBounds(new Rect(chat.ReactionDisplay.RenderSize));
            chat.SettingsScroll.ScrollToVerticalOffset(chat.SettingsScroll.VerticalOffset + target.Top); visual.UpdateLayout();
            target = chat.ReactionDisplay.TransformToAncestor(chat.SettingsScroll).TransformBounds(new Rect(chat.ReactionDisplay.RenderSize));
            Require(target.Top >= -1 && target.Bottom <= chat.SettingsScroll.ActualHeight + 1, "Screen chooser must remain reachable at every window size");
            chat.ShowSettingsForLayoutCheck(false);
        }
        chat.SetReceiverState(true, 8768, null); chat.SetStatus("Windows WPF 版面檢查完成");
        File.WriteAllText(Path.Combine(directory, "chat-layout-result.txt"), "PASS: work-area/DWM fitting, normal and narrow Inbox/conversation switching, compact composer, visible history/input/send and reachable settings. Installed IME, real mixed-DPI monitors and visual fidelity remain manual.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
