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

        var visual = (FrameworkElement)chat.Content;
        // Client sizes reserve non-client chrome and taskbar space. Includes 1366x768/125%,
        // 150%, minimum manual resize, a wide window, and returning to the default size.
        foreach (var size in new[] { new Size(464, 530), new Size(424, 450), new Size(424, 320), new Size(800, 500), new Size(464, 700), new Size(424, 320) })
        {
            chat.SetReceiverState(false, 8768, new string('測', 200));
            chat.SetStatus(new string('試', 200));
            visual.Measure(size);
            visual.Arrange(new Rect(new Point(), size));
            visual.UpdateLayout();
            var available = new Rect(new Point(), visual.RenderSize);
            foreach (var control in new FrameworkElement[] { chat.Draft, chat.SendButton, chat.History, chat.SettingsScroll })
            {
                var bounds = control.TransformToAncestor(visual).TransformBounds(new Rect(control.RenderSize));
                Require(control.ActualHeight > 0 && available.Contains(bounds),
                    control.Name + " must remain within the client area after resize");
            }
            Require(chat.Draft.ActualHeight >= 55 && chat.SendButton.ActualHeight >= 55, "Composer must not be squeezed away");
            if (size.Height <= 530)
                Require(chat.SettingsScroll.ScrollableHeight > 0, "Small windows must scroll settings");
            chat.SettingsScroll.ScrollToEnd();
            visual.UpdateLayout();
            Require(chat.SettingsScroll.VerticalOffset > 0 || chat.SettingsScroll.ScrollableHeight == 0, "Settings must scroll to the last controls");
            // At the absolute end, trailing explanatory text may put this control above a
            // very short viewport. Scroll to the control itself, as keyboard navigation does.
            var displayBounds = chat.ReactionDisplay.TransformToAncestor(chat.SettingsScroll)
                .TransformBounds(new Rect(chat.ReactionDisplay.RenderSize));
            chat.SettingsScroll.ScrollToVerticalOffset(chat.SettingsScroll.VerticalOffset + displayBounds.Top);
            visual.UpdateLayout();
            displayBounds = chat.ReactionDisplay.TransformToAncestor(chat.SettingsScroll)
                .TransformBounds(new Rect(chat.ReactionDisplay.RenderSize));
            Require(displayBounds.Top >= -0.5 && displayBounds.Bottom <= chat.SettingsScroll.ActualHeight + 0.5,
                $"Last settings control must be reachable: client={size}, control={displayBounds}, viewport={chat.SettingsScroll.ActualHeight}");
            chat.SettingsScroll.ScrollToHome();
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"chat-layout-{size.Width}x{size.Height}.png"));
            encoder.Save(file);
        }
        chat.SetReceiverState(true, 8768, null);
        chat.SetStatus("Windows CI 視窗繪製驗證完成");
        File.WriteAllText(Path.Combine(directory, "chat-layout-result.txt"),
            "PASS: work-area fitting at 100/125/150/200%, negative monitor origin, stable repeated fit, small/large/repeated WPF layout, scrollable settings and visible composer. Physical mixed-DPI monitor testing remains required.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
