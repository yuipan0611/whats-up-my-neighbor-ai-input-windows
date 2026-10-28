using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NekoChat.Windows;

public partial class PetWindow
{
    internal event Action<double>? PetScaleChanged;
    private double petScale = 1;
    internal double PetScale => petScale;
    private double artworkWidth = 208, artworkHeight = 192;
    private Rect desktopPet, desktopWork;
    private Rect artworkVisible = new(0, 0, 1, 1);
    internal Rect VisibleDesktopPet => PetDesktopLayout.Visible(desktopPet, artworkVisible);
    internal Rect DesktopWorkArea => desktopWork;
    private bool desktopReady, updatingDesktop, desktopRefreshPending, updatingSizeMenu;
    private Slider sizeSlider = null!;
    private TextBlock sizeLabel = null!, qualityLabel = null!;
    private readonly DispatcherTimer sizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Point dragCursorStart, dragPetStart;

    private void InitializeDesktop(ContextMenu menu)
    {
        var sizeMenu = new MenuItem { Header = "📏 貓咪尺寸" };
        var panel = new StackPanel { Width = 250, Margin = new Thickness(10, 4, 10, 4) };
        sizeLabel = new TextBlock();
        sizeSlider = new Slider
        {
            Minimum = 50, Maximum = 200, Value = 100, TickFrequency = 5,
            IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 25,
            Margin = new Thickness(0, 8, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(sizeSlider, "貓咪尺寸百分比");
        qualityLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        var reset = new Button { Content = "恢復預設 100%", Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) => sizeSlider.Value = 100;
        panel.Children.Add(sizeLabel);
        panel.Children.Add(sizeSlider);
        panel.Children.Add(qualityLabel);
        panel.Children.Add(reset);
        sizeMenu.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true });
        menu.Items.Insert(1, sizeMenu);
        sizeSlider.ValueChanged += (_, _) =>
        {
            if (updatingSizeMenu) return;
            SetPetScale(sizeSlider.Value / 100);
            sizeSaveTimer.Stop();
            sizeSaveTimer.Start();
        };
        sizeSaveTimer.Tick += (_, _) =>
        {
            sizeSaveTimer.Stop();
            PetScaleChanged?.Invoke(petScale);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(DesktopMessage);
            desktopWork = WorkArea(new Rect(0, 0, 1, 1), handle);
            var dpi = VisualTreeHelper.GetDpi(this);
            desktopPet = new Rect(desktopWork.Right - artworkWidth * petScale * dpi.DpiScaleX - 20,
                desktopWork.Bottom - artworkHeight * petScale * dpi.DpiScaleY - 20,
                artworkWidth * petScale * dpi.DpiScaleX, artworkHeight * petScale * dpi.DpiScaleY);
            desktopReady = true;
            RefreshDesktop();
        };
        DpiChanged += (_, _) => QueueDesktopRefresh();
        Bubble.IsVisibleChanged += (_, _) => RefreshDesktop();
        Closed += (_, _) =>
        {
            if (sizeSaveTimer.IsEnabled) PetScaleChanged?.Invoke(petScale);
            sizeSaveTimer.Stop();
        };
        UpdateSizeMenu();
    }

    internal void SetPetScale(double value)
    {
        petScale = PetDesktopLayout.NormalizeScale(value);
        UpdateArtworkSize(artworkWidth, artworkHeight);
        if (desktopReady)
        {
            activity.Reset(AnimationNow, 0);
            nextAction = AnimationNow + 2;
            if (automaticMotion && !animation.Pressed) animation.Reset(AnimationNow);
            automaticMotion = false;
        }
        UpdateSizeMenu();
    }

    private void UpdateArtworkSize(double width, double height)
    {
        artworkWidth = width;
        artworkHeight = height;
        Cat.Width = width * petScale;
        Cat.Height = height * petScale;
        RefreshDesktop(preserveFeet: true);
    }

    private void UpdateSizeMenu()
    {
        if (sizeSlider is null) return;
        updatingSizeMenu = true;
        sizeSlider.Value = petScale * 100;
        updatingSizeMenu = false;
        sizeLabel.Text = $"貓咪尺寸：{petScale:P0}（50%～200%）";
        var dpi = VisualTreeHelper.GetDpi(this);
        bool enlarged = Cat.Source is System.Windows.Media.Imaging.BitmapSource bitmap &&
            (Cat.Width * dpi.DpiScaleX > bitmap.PixelWidth + .5 || Cat.Height * dpi.DpiScaleY > bitmap.PixelHeight + .5);
        qualityLabel.Text = enlarged
            ? "目前尺寸超過原圖解析度，可能變柔和；縮小可保留更多細節。"
            : "使用原始圖片高品質縮放；Windows 顯示縮放也會影響清晰度。";
    }

    private void QueueDesktopRefresh()
    {
        if (desktopRefreshPending) return;
        desktopRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            desktopRefreshPending = false;
            if (!desktopReady) return;
            activity.Reset(AnimationNow, 0);
            automaticMotion = false;
            nextAction = AnimationNow + 2;
            RefreshDesktop(preserveFeet: true);
            UpdateSizeMenu();
        }));
    }

    private IntPtr DesktopMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x007E or 0x02E0) QueueDesktopRefresh();
        return IntPtr.Zero;
    }

    internal void SetDesktopPetPosition(double x, double y)
    {
        desktopPet = new Rect(x, y, desktopPet.Width, desktopPet.Height);
        RefreshDesktop();
    }

    private void RefreshDesktop(bool preserveFeet = false)
    {
        if (!desktopReady || updatingDesktop) return;
        updatingDesktop = true;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            desktopWork = WorkArea(VisibleDesktopPet);
            var dpi = VisualTreeHelper.GetDpi(this);
            double fit = Math.Min(petScale, Math.Min(desktopWork.Width / (artworkWidth * dpi.DpiScaleX),
                desktopWork.Height / (artworkHeight * dpi.DpiScaleY)));
            Cat.Width = artworkWidth * fit;
            Cat.Height = artworkHeight * fit;
            double width = Cat.Width * dpi.DpiScaleX, height = Cat.Height * dpi.DpiScaleY;
            double left = desktopPet.Left, top = desktopPet.Top;
            if (preserveFeet)
            {
                left += (desktopPet.Width - width) * (artworkVisible.Left + artworkVisible.Width / 2);
                top += (desktopPet.Height - height) * artworkVisible.Bottom;
            }
            desktopPet = PetDesktopLayout.ClampArtwork(new Rect(left, top, width, height), artworkVisible, desktopWork);
            Rect window = desktopPet;
            if (Bubble.Visibility == Visibility.Visible)
            {
                Bubble.Width = Math.Min(280, desktopWork.Width / dpi.DpiScaleX);
                Bubble.Measure(new Size(Bubble.Width, double.PositiveInfinity));
                var bubble = PetDesktopLayout.PlaceBubble(VisibleDesktopPet,
                    new Size(Bubble.Width * dpi.DpiScaleX, Bubble.DesiredSize.Height * dpi.DpiScaleY), desktopWork, 6 * dpi.DpiScaleY);
                window.Union(bubble);
                Canvas.SetLeft(Bubble, (bubble.Left - window.Left) / dpi.DpiScaleX);
                Canvas.SetTop(Bubble, (bubble.Top - window.Top) / dpi.DpiScaleY);
            }
            Canvas.SetLeft(Cat, (desktopPet.Left - window.Left) / dpi.DpiScaleX);
            Canvas.SetTop(Cat, (desktopPet.Top - window.Top) / dpi.DpiScaleY);
            Width = window.Width / dpi.DpiScaleX;
            Height = window.Height / dpi.DpiScaleY;
            // Native placement avoids mixing WPF DIPs with physical monitor origins.
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(window.Left), (int)Math.Round(window.Top),
                (int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 0x0014);
        }
        finally { updatingDesktop = false; }
    }

    private static Point CursorPosition()
    {
        GetCursorPos(out var point);
        return new Point(point.X, point.Y);
    }

    private static Rect ArtworkBounds(ImageSource image)
    {
        if (image is not System.Windows.Media.Imaging.BitmapSource source) return new Rect(0, 0, 1, 1);
        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] >= 8)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        return right < left ? new Rect(0, 0, 1, 1) : new Rect((double)left / width, (double)top / height,
            (double)(right - left + 1) / width, (double)(bottom - top + 1) / height);
    }

    private static Rect WorkArea(Rect pet, IntPtr window = default)
    {
        var bounds = new NativeRect
        {
            Left = (int)Math.Floor(pet.Left), Top = (int)Math.Floor(pet.Top),
            Right = (int)Math.Ceiling(pet.Right), Bottom = (int)Math.Ceiling(pet.Bottom)
        };
        var monitor = window != IntPtr.Zero ? MonitorFromWindow(window, 2) : MonitorFromRect(ref bounds, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
