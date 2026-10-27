using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Neighbor.Core;
using Neighbor.Windows;
using NekoChat.Animation;

namespace Neighbor.Windows.UI
{
    /// <summary>
    /// Non-activating companion on a per-pixel alpha layered window.
    /// Features 9 native coats, smooth animation playback (pickup, drop, petted, idle),
    /// horizontal roaming, and pass-through transparency.
    /// </summary>
    public sealed class DesktopPetForm : NonActivatingForm, IDesktopPetView
    {
        private const int Sprite = 128;
        private const int WsExLayered = 0x00080000;
        private const int MinimumAlpha = 24;
        private readonly DesktopPetArtwork artwork = new DesktopPetArtwork();
        private readonly AnimationPlayer player;
        private readonly Timer timer = new Timer { Interval = 40 }; // 25 fps
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double previous, nextAction;
        private int actionIndex;
        private bool enabled, busy, locked, suspended, placed, automaticMotion;
        private string coat = "original";
        private Point grabOffset;
        private Bitmap frame;

        public event Action<Exception> Failed;

        public DesktopPetForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Sprite, Sprite); AccessibleName = "桌面散步小黑貓";
            player = new AnimationPlayer(artwork.Manifest);
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
            if (!enabled)
            {
                if (player.Pressed) { player.Cancel(clock.Elapsed.TotalSeconds); Capture = false; }
                timer.Stop(); Hide(); return;
            }
            if (!timer.Enabled) { previous = clock.Elapsed.TotalSeconds; timer.Start(); }
            RefreshPet();
        }

        public void SetCoat(string newCoat)
        {
            if (IsDisposed) return;
            if (!string.IsNullOrWhiteSpace(newCoat) && artwork.Manifest.Coats.Contains(newCoat))
            {
                coat = newCoat;
                RefreshPet();
            }
        }

        private void RefreshPet()
        {
            if (IsDisposed) return;
            try { Step(); }
            catch (Exception error) { Stop(error); }
        }

        private void Stop(Exception error)
        {
            enabled = false;
            if (player.Pressed) { player.Cancel(clock.Elapsed.TotalSeconds); Capture = false; }
            timer.Stop();
            if (!IsDisposed && Visible) Hide();
            Failed?.Invoke(error);
        }

        private void Step()
        {
            double now = clock.Elapsed.TotalSeconds, elapsed = now - previous; previous = now;
            double dt = Math.Max(0, Math.Min(elapsed, 0.05));
            int size = Math.Max(96, (int)(Sprite * DeviceDpi / 96.0));
            bool hidden = !enabled || busy || locked || suspended || FullScreenForeground();
            if (hidden)
            {
                if (player.Pressed) { player.Cancel(now); Capture = false; }
                Hide();
                return;
            }

            var area = Screen.FromPoint(new Point(Location.X + size / 2, Location.Y + size / 2)).WorkingArea;
            if (!placed)
            {
                Location = new Point(area.Right - size - 40, area.Bottom - size);
                placed = true;
            }

            player.Tick(now);

            bool animate;
            if (!SystemParametersInfo(0x1042, 0, out animate, 0)) animate = true;

            if (!animate || hidden)
            {
                if (automaticMotion) { player.Reset(now); automaticMotion = false; }
                nextAction = now + 2;
            }
            else if (!player.Pressed && player.Mode != "lifting" && player.Mode != "landing" && player.Mode != "carried" && now >= nextAction)
            {
                (string Name, double Seconds)[] actions = [
                    ("standing", 1.8), ("walk-left", 4.0), ("grooming", 3.2), ("yawning", 3.2),
                    ("loafing", 5.0), ("sleeping", 20.0), ("curious", 2.2), ("walk-right", 4.0)
                ];
                var action = actions[actionIndex++ % actions.Length];
                player.Play(action.Name, now);
                automaticMotion = true;
                nextAction = now + action.Seconds;
            }

            if (automaticMotion && player.Mode == "clip" && (player.ClipName == "walk-left" || player.ClipName == "walk-right"))
            {
                double target = Location.X + (player.ClipName == "walk-left" ? -1 : 1) * 70 * dt;
                double clamped = Math.Max(area.Left, Math.Min(target, area.Right - size));
                Location = new Point((int)Math.Round(clamped), Location.Y);
                if (Math.Abs(clamped - target) > 0.01)
                {
                    player.Reset(now); automaticMotion = false; nextAction = now + 2;
                }
            }

            if (ClientSize.Width != size) ClientSize = new Size(size, size);
            Draw(size);
            if (!Visible) Show();
        }

        private void Draw(int size)
        {
            var next = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(next))
            {
                graphics.Clear(Color.Transparent);
                string frameName = player.Frame;
                bool facingLeft = player.ClipName != "walk-right";
                artwork.DrawFrame(graphics, size, coat, frameName, facingLeft);
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
            double now = clock.Elapsed.TotalSeconds;
            player.Press(now);
            grabOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
            Capture = true;
            RefreshPet();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (player.Pressed && Capture)
            {
                double now = clock.Elapsed.TotalSeconds;
                player.Drag(now);
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                int size = ClientSize.Width;
                int targetX = Math.Max(area.Left, Math.Min(Cursor.Position.X - grabOffset.X, area.Right - size));
                int targetY = Math.Max(area.Top, Math.Min(Cursor.Position.Y - grabOffset.Y, area.Bottom - size));
                Location = new Point(targetX, targetY);
                RefreshPet();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !player.Pressed) return;
            double now = clock.Elapsed.TotalSeconds;
            player.Release(now);
            Capture = false;
            RefreshPet();
        }

        private void OnCaptureChanged(object sender, EventArgs e)
        {
            if (Capture || !player.Pressed) return;
            double now = clock.Elapsed.TotalSeconds;
            player.Cancel(now);
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
