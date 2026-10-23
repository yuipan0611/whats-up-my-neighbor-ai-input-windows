using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Neighbor.Core;
using Neighbor.Windows;

namespace Neighbor.Windows.UI
{
    /// <summary>
    /// Non-activating companion on a per-pixel alpha layered window. Transparent pixels pass
    /// clicks through; the cat body can be dragged, then walks horizontally at the dropped height.
    /// </summary>
    public sealed class DesktopPetForm : NonActivatingForm, IDesktopPetView
    {
        private const int Sprite = 128;
        private const int WsExLayered = 0x00080000;
        private const int MinimumAlpha = 24; // Pixels at or below this alpha do not grab the mouse.
        private readonly DesktopPetArtwork artwork = new DesktopPetArtwork();
        private readonly DesktopPetMotion motion = new DesktopPetMotion();
        private readonly Timer timer = new Timer { Interval = 83 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double previous;
        private bool enabled, busy, locked, suspended, placed;
        private Bitmap frame;
        /// <summary>Raised once when the companion stops itself after an unexpected error.</summary>
        public event Action<Exception> Failed;

        public DesktopPetForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Sprite, Sprite); AccessibleName = "桌面散步小黑貓";
            timer.Tick += (_, __) => RefreshPet();
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            MouseCaptureChanged += OnCaptureChanged;
        }

        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= WsExLayered; return p; }
        }

        public void Configure(bool visible, bool isBusy)
        {
            if (IsDisposed) return;
            enabled = visible; busy = isBusy;
            // Off means no timer at all, so a disabled companion costs nothing while the app runs.
            if (!enabled) { motion.CancelDrag(); Capture = false; timer.Stop(); Hide(); return; }
            if (!timer.Enabled) { previous = clock.Elapsed.TotalSeconds; timer.Start(); }
            RefreshPet();
        }

        private void RefreshPet()
        {
            if (IsDisposed) return;
            try { Step(); }
            catch (Exception error) { Stop(error); }
        }

        // Stop quietly instead of letting one failing tick raise an error dialog every 83 ms.
        private void Stop(Exception error)
        {
            enabled = false; motion.CancelDrag(); Capture = false; timer.Stop();
            if (!IsDisposed && Visible) Hide();
            Failed?.Invoke(error);
        }

        private void Step()
        {
            double now = clock.Elapsed.TotalSeconds, elapsed = now - previous; previous = now;
            int size = Math.Max(96, (int)(Sprite * DeviceDpi / 96.0));
            double scale = size / (double)Sprite;
            bool hidden = !enabled || busy || locked || suspended || FullScreenForeground();
            if (hidden) { motion.CancelDrag(); Capture = false; }
            // While dragging, the cursor's monitor decides the bounds, so the cat can cross screens.
            var area = motion.Dragging
                ? Screen.FromPoint(Cursor.Position).WorkingArea
                : Screen.FromPoint(new Point(Location.X + size / 2, Location.Y + size / 2)).WorkingArea;
            if (!placed) { motion.PlaceInitially(area.Left, area.Top, area.Right, area.Bottom, scale); placed = true; }
            if (motion.Dragging) motion.DragTo(Cursor.Position.X, Cursor.Position.Y, area.Left, area.Top, area.Right, area.Bottom, scale);
            bool animate;
            if (!SystemParametersInfo(0x1042, 0, out animate, 0)) animate = true; // SPI_GETCLIENTAREAANIMATION
            // Advance keeps the cat inside the full work area on both axes.
            motion.Advance(elapsed, area.Left, area.Top, area.Right, area.Bottom, scale, hidden || !animate);
            if (hidden) { Hide(); return; }
            if (ClientSize.Width != size) ClientSize = new Size(size, size);
            Location = new Point((int)Math.Round(motion.X), (int)Math.Round(motion.Y));
            Draw(size);
            if (!Visible) Show();
        }

        // Renders one premultiplied frame and presents it; the frame is kept for click tests.
        private void Draw(int size)
        {
            var next = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(next))
            {
                graphics.Clear(Color.Transparent);
                artwork.Draw(graphics, size, motion.Walking, motion.FacingLeft, motion.Distance);
            }
            if (frame != null) frame.Dispose();
            frame = next;
            LayeredWindow.Present(Handle, Location, frame);
        }

        private bool CatAt(Point client)
        {
            if (frame == null || client.X < 0 || client.Y < 0 || client.X >= frame.Width || client.Y >= frame.Height) return false;
            return frame.GetPixel(client.X, client.Y).A > MinimumAlpha;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !enabled || !Visible || !CatAt(e.Location)) return;
            // Grab offset is taken from the current position, so the cat does not jump to the pointer.
            motion.BeginDrag(Cursor.Position.X, Cursor.Position.Y);
            Capture = true;
            RefreshPet();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (motion.Dragging) RefreshPet();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !motion.Dragging) return;
            motion.EndDrag();
            Capture = false;
            RefreshPet();
        }

        // Losing mouse capture (for example Alt+Tab or a modal dialog) must never leave the drag stuck.
        private void OnCaptureChanged(object sender, EventArgs e)
        {
            if (Capture || !motion.Dragging) return;
            motion.CancelDrag();
            RefreshPet();
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff) locked = true;
                if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon) locked = false;
                RefreshPet();
            }));
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => { suspended = e.Mode == PowerModes.Suspend ? true : e.Mode == PowerModes.Resume ? false : suspended; RefreshPet(); }));
        }

        private static bool FullScreenForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero || window == GetShellWindow() || !GetWindowRect(window, out Rect rect)) return false;
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "Progman" || name.ToString() == "WorkerW") return false;
            var screen = Screen.FromHandle(window);
            // Only suppress borderless full-screen windows, not maximized working-area windows.
            return rect.Left <= screen.Bounds.Left && rect.Top <= screen.Bounds.Top && rect.Right >= screen.Bounds.Right && rect.Bottom >= screen.Bounds.Bottom;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop(); SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
                timer.Dispose(); artwork.Dispose();
                if (frame != null) frame.Dispose();
            }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    }
}
