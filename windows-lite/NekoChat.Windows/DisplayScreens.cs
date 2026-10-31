using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NekoChat.Windows;

internal sealed record DisplayScreen(string DeviceName, string Label, IntPtr Handle, NativeRect Bounds, NativeRect WorkArea);
internal sealed record PetPlacement(NativeRect Bounds, string DeviceName);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

internal static class DisplayScreens
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);

    internal static IReadOnlyList<DisplayScreen> All()
    {
        var screens = new List<DisplayScreen>();
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
                screens.Add(new(info.Device, $"螢幕 {screens.Count + 1}{((info.Flags & 1) != 0 ? "（主要）" : "")} · {info.Monitor.Width}×{info.Monitor.Height}", monitor, info.Monitor, info.Work));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Win32Exception();
        return screens;
    }

    internal static PetPlacement Capture(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect)) throw new Win32Exception();
        var monitor = MonitorFromWindow(window, 2);
        return new(rect, All().FirstOrDefault(s => s.Handle == monitor)?.DeviceName ?? "");
    }

    internal static void Center(IntPtr window, string deviceName, double logicalWidth, double logicalHeight)
    {
        var screens = All();
        var screen = screens.FirstOrDefault(s => s.DeviceName == deviceName)
            ?? screens.FirstOrDefault(s => s.Handle == MonitorFromWindow(window, 2))
            ?? screens.First();
        if (!GetWindowRect(window, out var current)) throw new Win32Exception();
        // Move first so GetDpiForWindow observes the destination display after WM_DPICHANGED.
        Move(window, screen.Bounds.Left + (screen.Bounds.Width - current.Width) / 2,
            screen.Bounds.Top + (screen.Bounds.Height - current.Height) / 2, current.Width, current.Height);
        uint dpi = GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        int width = Math.Min(screen.WorkArea.Width, (int)Math.Round(logicalWidth * dpi / 96));
        int height = Math.Min(screen.WorkArea.Height, (int)Math.Round(logicalHeight * dpi / 96));
        Move(window, screen.Bounds.Left + (screen.Bounds.Width - width) / 2,
            screen.Bounds.Top + (screen.Bounds.Height - height) / 2, width, height);
    }

    internal static void Restore(IntPtr window, PetPlacement placement)
    {
        var screens = All();
        var screen = screens.FirstOrDefault(s => s.DeviceName == placement.DeviceName)
            ?? screens.FirstOrDefault(s => s.Handle == MonitorFromWindow(window, 2))
            ?? screens.First();
        var rect = placement.Bounds;
        int width = Math.Min(rect.Width, screen.WorkArea.Width), height = Math.Min(rect.Height, screen.WorkArea.Height);
        Move(window, Math.Clamp(rect.Left, screen.WorkArea.Left, screen.WorkArea.Right - width),
            Math.Clamp(rect.Top, screen.WorkArea.Top, screen.WorkArea.Bottom - height), width, height);
    }

    internal static NativeRect ConstrainToWorkArea(NativeRect bounds, NativeRect workArea)
    {
        int width = Math.Min(bounds.Width, workArea.Width);
        int height = Math.Min(bounds.Height, workArea.Height);
        int left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        int top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>
    /// Fits a window into a work area by its visible frame. On Windows 10/11 GetWindowRect
    /// includes invisible DWM resize borders, so a window touching the edge or snapped to half
    /// the screen extends past the work area by a few pixels. Comparing the visible frame keeps
    /// such windows untouched; the borders are added back for SetWindowPos.
    /// </summary>
    internal static NativeRect FitWindowToWorkArea(NativeRect window, NativeRect frame, NativeRect workArea)
    {
        var fit = ConstrainToWorkArea(frame, workArea);
        if (fit.Left == frame.Left && fit.Top == frame.Top && fit.Width == frame.Width && fit.Height == frame.Height)
            return window;
        int left = fit.Left - (frame.Left - window.Left), top = fit.Top - (frame.Top - window.Top);
        return new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + fit.Width + (window.Width - frame.Width),
            Bottom = top + fit.Height + (window.Height - frame.Height),
        };
    }

    internal static void FitChat(ChatWindow chat)
    {
        var window = new System.Windows.Interop.WindowInteropHelper(chat).Handle;
        if (window == IntPtr.Zero) return;
        var screen = All().FirstOrDefault(s => s.Handle == MonitorFromWindow(window, 2));
        if (screen is null) return;
        uint dpi = GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        // WPF minima are DIPs; monitor work areas and SetWindowPos are physical pixels.
        // Relax minima too, otherwise WPF can undo the native clamp on tiny/high-DPI screens.
        chat.MinWidth = Math.Min(440, screen.WorkArea.Width * 96.0 / dpi);
        chat.MinHeight = Math.Min(360, screen.WorkArea.Height * 96.0 / dpi);
        if (!GetWindowRect(window, out var current)) return;
        // Per-monitor-v2 aware, so the extended frame bounds are physical pixels like the work area.
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<NativeRect>()) != 0
            || frame.Width <= 0 || frame.Height <= 0)
            frame = current;
        var fit = FitWindowToWorkArea(current, frame, screen.WorkArea);
        if (fit.Left != current.Left || fit.Top != current.Top || fit.Width != current.Width || fit.Height != current.Height)
            Move(window, fit.Left, fit.Top, fit.Width, fit.Height);
    }

    private static void Move(IntPtr window, int x, int y, int width, int height)
    {
        // Native physical coordinates avoid mixing logical offsets from monitors with different DPI.
        if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0010 | 0x0004)) throw new Win32Exception();
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
}
