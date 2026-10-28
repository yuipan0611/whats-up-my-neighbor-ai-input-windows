using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NekoChat.Windows;

public partial class PetWindow
{
    internal void VerifyDesktopIntegration(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidDataException(message);
        }
        Check(PetDesktopLayout.NormalizeScale(double.NaN) == 1 &&
            PetDesktopLayout.NormalizeScale(double.PositiveInfinity) == 1 &&
            PetDesktopLayout.NormalizeScale(.1) == .5 && PetDesktopLayout.NormalizeScale(8) == 2,
            "Invalid size settings are not normalized");
        var visible = new Rect(.25, .125, .5, .625);
        foreach (var work in new[] { new Rect(0, 0, 1920, 1040), new Rect(-2560, -400, 2560, 1400), new Rect(0, 0, 800, 560) })
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        foreach (double scale in new[] { .5, 1.0, 2.0 })
        foreach (var point in new[] { work.TopLeft, work.TopRight, work.BottomLeft, work.BottomRight })
        {
            var frame = PetDesktopLayout.ClampArtwork(new Rect(point, new Size(208 * dpi * scale, 192 * dpi * scale)), visible, work);
            var body = PetDesktopLayout.Visible(frame, visible);
            Check(work.Contains(body), "Visible cat escaped synthetic monitor work area");
            Check(work.Contains(PetDesktopLayout.PlaceBubble(body, new Size(280 * dpi, 108 * dpi), work, 6 * dpi)),
                "Bubble escaped synthetic monitor work area");
        }
        bool wasVisible = IsVisible;
        bool previousWander = WanderEnabled;
        double previousScale = PetScale;
        SetWanderEnabled(false);
        Show();
        frameTimer.Stop();
        var originalSource = Cat.Source;
        foreach (double scale in new[] { .5, 1.0, 2.0 })
        {
            SetPetScale(scale);
            Check(ReferenceEquals(Cat.Source, originalSource), "Resizing resampled or replaced original bitmap");
            foreach (var corner in new[] { desktopWork.TopLeft, desktopWork.TopRight, desktopWork.BottomLeft, desktopWork.BottomRight })
            {
                SetDesktopPetPosition(corner.X - artworkVisible.Left * desktopPet.Width,
                    corner.Y - artworkVisible.Top * desktopPet.Height);
                FlushLayout();
                var body = VisibleDesktopPet;
                var actual = Cat.PointToScreen(new Point(Cat.Width * artworkVisible.Left, Cat.Height * artworkVisible.Top));
                Check(Math.Abs(body.Left - actual.X) <= 1.1 && Math.Abs(body.Top - actual.Y) <= 1.1,
                    $"WPF/native desktop coordinates disagree: expected {body.TopLeft}, actual {actual}");
                Check(desktopWork.Contains(body), "Cat escaped real monitor work area");
                Check(Math.Abs((corner.X == desktopWork.Left ? body.Left : body.Right) - corner.X) < .01 &&
                    Math.Abs((corner.Y == desktopWork.Top ? body.Top : body.Bottom) - corner.Y) < .01,
                    "Visible cat did not reach real screen corner");
                Notify("尺寸與螢幕邊界測試：點擊氣泡仍可回覆。", "127.0.0.1");
                FlushLayout();
                Check((VisibleDesktopPet.TopLeft - body.TopLeft).Length < .01, "Showing a bubble moved the cat");
                var bubblePoint = Bubble.PointToScreen(new Point());
                var dpi = VisualTreeHelper.GetDpi(this);
                var bubbleRect = new Rect(bubblePoint, new Size(Bubble.ActualWidth * dpi.DpiScaleX, Bubble.ActualHeight * dpi.DpiScaleY));
                var tolerance = desktopWork; tolerance.Inflate(1.1, 1.1);
                Check(tolerance.Contains(bubbleRect), "Visible bubble is clipped at a screen edge");
                Bubble.Visibility = Visibility.Collapsed;
                RefreshDesktop();
                FlushLayout();
                Check((VisibleDesktopPet.TopLeft - body.TopLeft).Length < .01, "Hiding a bubble moved the cat");
            }
            Notify($"貓咪尺寸 {scale:P0}");
            FlushLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(directory, $"pet-size-{scale * 100:0}.png"))) encoder.Save(output);
            Bubble.Visibility = Visibility.Collapsed;
            RefreshDesktop();
        }
        SetPetScale(previousScale);
        SetWanderEnabled(previousWander);
        if (!wasVisible) Hide();
        File.WriteAllText(Path.Combine(directory, "desktop-result.txt"),
            $"PASS: {checks} checks; 50/100/200% sizes, original bitmap retention, physical screen corners, bubble placement, negative monitor origins, 100/150/200% geometry. Physical mixed-DPI monitor transitions require hardware validation.");

        void FlushLayout()
        {
            UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            UpdateLayout();
        }
    }
}
