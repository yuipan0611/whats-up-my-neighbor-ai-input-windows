using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_DPICHANGED = 0x02E0;

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

    // Settings use their natural height (an Auto row) so large windows give the rest to the
    // history. On small windows cap them at two thirds of the space above the composer and
    // let them scroll, which keeps the history and composer visible.
    private void ChatLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double composer = ChatLayout.RowDefinitions[2].ActualHeight;
        double cap = Math.Max(0, (ChatLayout.ActualHeight - composer) * 2 / 3 - SettingsScroll.Margin.Bottom);
        if (Math.Abs(SettingsScroll.MaxHeight - cap) > 0.5) SettingsScroll.MaxHeight = cap;
    }

    private IntPtr WorkAreaMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Wait for WPF to apply DPI/restore bounds. Do not fight the mouse during a drag.
        if (message == WM_ENTERSIZEMOVE) movingOrResizing = true;
        if (message == WM_EXITSIZEMOVE) movingOrResizing = false;
        if (message is WM_DPICHANGED or WM_DISPLAYCHANGE or WM_SETTINGCHANGE or WM_EXITSIZEMOVE)
            QueueWorkAreaUpdate();
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
            // Fitting is best effort: the window may be closing or the display changing again.
            try { DisplayScreens.FitChat(this); }
            catch (Win32Exception) { }
        }));
    }
}
