using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private bool workAreaUpdateQueued;
    private bool movingOrResizing;

    private void InitializeWorkAreaSizing()
    {
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WorkAreaMessage);
            QueueWorkAreaUpdate();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueWorkAreaUpdate(); };
        StateChanged += (_, _) => QueueWorkAreaUpdate();
    }

    private IntPtr WorkAreaMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Wait for WPF to apply DPI/restore bounds. Do not fight the mouse during a drag.
        if (message == 0x0231) movingOrResizing = true;
        if (message == 0x0232) movingOrResizing = false;
        if (message is 0x02E0 or 0x007E or 0x001A or 0x0232)
            QueueWorkAreaUpdate(); // DPI, display, work-area settings, exit move/resize
        return IntPtr.Zero;
    }

    private void QueueWorkAreaUpdate()
    {
        if (workAreaUpdateQueued) return;
        workAreaUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            workAreaUpdateQueued = false;
            if (!IsVisible || movingOrResizing || WindowState != WindowState.Normal) return;
            DisplayScreens.FitChat(this);
        }));
    }
}
