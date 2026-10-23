using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Neighbor.Core;

namespace Neighbor.Windows.UI
{
    // Drawn into a per-pixel alpha layered window (LayeredWindow), so the color-key fringe cannot appear.
    // Child controls are not drawn into that bitmap, so the eyes are hit-tested and hover-painted here.
    public sealed class RecordingHud : NonActivatingForm
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private readonly Image cat = Theme.LoadCatImage();
        private readonly ScaledCatCache catCache;
        private readonly ToolTip tips = new ToolTip();
        private DictationState state;
        private string message = "按快捷鍵開始說話";
        private double level;
        private bool levelKnown, atTop;
        private RecordingHudTarget hovered;
        public event Action StartRequested;
        public event Action FinishRequested;
        public event Action CancelRequested;
        public event Action RetryRequested;

        // Single entry point for a mouse click and for an accessibility default action.
        // The state is read at call time, so a stale target cannot run an action for a newer state.
        public bool Activate(RecordingHudTarget target)
        {
            switch (RecordingHudLayout.ActionFor(target, state))
            {
                case RecordingHudAction.Cancel: CancelRequested?.Invoke(); return true;
                case RecordingHudAction.Finish: FinishRequested?.Invoke(); return true;
                case RecordingHudAction.Start: StartRequested?.Invoke(); return true;
                case RecordingHudAction.Retry: RetryRequested?.Invoke(); return true;
                default: return false;
            }
        }

        public RecordingHud()
        {
            Font = new Font("Microsoft JhengHei UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            ClientSize = new Size(340, 220);
            StartPosition = FormStartPosition.Manual;
            AccessibleName = "小黑貓語音輸入";
            catCache = new ScaledCatCache(cat);
            UpdateState(DictationState.Idle, message);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_LAYERED;
                return parameters;
            }
        }

        public void ApplySettings(AppSettings settings)
        {
            if (settings == null) return;
            atTop = settings.HudAtTop;
            PlaceOnScreen();
        }

        public void UpdateState(DictationState value, string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdateState(value, text))); return; }
            var previous = state;
            state = value;
            message = String.IsNullOrWhiteSpace(text) ? StateLabel(value) : text;
            if (previous != value) NotifyEyesChanged(AccessibleEvents.NameChange);
            SetHovered(hovered);
            if (value != DictationState.Recording) { level = 0; levelKnown = false; }
            AccessibleDescription = StateLabel(value) + "，" + message;
            Redraw();
        }

        public void UpdateLevel(double decibels, bool isKnown)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdateLevel(decibels, isKnown))); return; }
            levelKnown = state == DictationState.Recording && isKnown && !Double.IsNaN(decibels) && !Double.IsInfinity(decibels);
            level = levelKnown ? RecordingHudLayout.LevelFraction(decibels, true) : 0;
            Redraw();
        }

        // Renders the current frame as premultiplied ARGB at the window's client size.
        public Bitmap RenderFrame()
        {
            var frame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // ClearType writes opaque color into the text pixels and breaks per-pixel alpha, so use grayscale AA.
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                int catHeight = (int)(ClientSize.Width * .4978);
                graphics.DrawImage(catCache.Get(new Size(ClientSize.Width, catHeight)), 0, 0);
                var plate = new Rectangle(18, catHeight + 2, ClientSize.Width - 36, ClientSize.Height - catHeight - 4);
                using (var shape = LayeredWindow.RoundedPath(plate, 12))
                using (var background = new SolidBrush(Color.FromArgb(30, 31, 35))) graphics.FillPath(background, shape);
                var bounds = new RectangleF(plate.Left + 12, plate.Top + 7, plate.Width - 24, plate.Height - 11);
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                using (var brush = new SolidBrush(Color.FromArgb(241, 242, 245))) graphics.DrawString(message, Font, brush, bounds, format);
                if (state == DictationState.Recording)
                {
                    int meterWidth = (int)(ClientSize.Width * .30);
                    var meter = new Rectangle((ClientSize.Width - meterWidth) / 2, (int)(catHeight * .82), meterWidth, Math.Max(4, (int)(ClientSize.Width / 68.0)));
                    using (var brush = new SolidBrush(Color.FromArgb(61, 62, 68))) graphics.FillRectangle(brush, meter);
                    if (levelKnown && level > 0)
                    {
                        var filled = new Rectangle(meter.Left, meter.Top, Math.Max(1, (int)(meterWidth * level)), meter.Height);
                        using (var brush = new SolidBrush(Color.FromArgb(165, 193, 255))) graphics.FillRectangle(brush, filled);
                    }
                }
                PaintHover(graphics, RecordingHudTarget.Cancel, RecordingHudLayout.CancelEye(ClientSize));
                PaintHover(graphics, RecordingHudTarget.Finish, RecordingHudLayout.FinishEye(ClientSize));
            }
            return frame;
        }

        // Overriding CreateAccessibilityInstance does not reach the MSAA client path in this framework
        // (verified: IAccessible reports zero children). The client-object request is answered directly
        // with the container, so MSAA clients see the two eyes.
        private const int WM_GETOBJECT = 0x003D;
        private const int OBJID_CLIENT = -4;
        private RecordingHudContainerAccessibleObject accessibleRoot;

        // The accessible container for the two eyes. MSAA clients reach it through WM_GETOBJECT below.
        public RecordingHudContainerAccessibleObject AccessibleRoot
        {
            get { return accessibleRoot ?? (accessibleRoot = new RecordingHudContainerAccessibleObject(this)); }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WM_GETOBJECT && unchecked((int)message.LParam.ToInt64()) == OBJID_CLIENT)
            {
                Guid accessibleIid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IAccessible
                message.Result = LresultFromObject(ref accessibleIid, message.WParam, AccessibleRoot);
                return;
            }
            base.WndProc(ref message);
        }

        [System.Runtime.InteropServices.DllImport("oleacc.dll")]
        private static extern IntPtr LresultFromObject(ref Guid riid, IntPtr wParam, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IUnknown)] object punk);

        internal DictationState CurrentState { get { return state; } }

        internal RecordingHudAction ActionFor(RecordingHudTarget target)
        {
            return RecordingHudLayout.ActionFor(target, state);
        }

        // Accessible name for the right eye follows the current state; the left eye never changes.
        internal string EyeName(RecordingHudTarget target)
        {
            if (target == RecordingHudTarget.Cancel) return "取消這次錄音或辨識";
            switch (state)
            {
                case DictationState.Idle: return "開始錄音";
                case DictationState.Recording: return "完成錄音";
                case DictationState.RetainedAudio: return "重試辨識";
                default: return "開始或完成錄音";
            }
        }

        // Default action for an eye. Runs on the UI thread and reads the state at that moment, so a
        // stale or hidden or released HUD does nothing.
        public void DoAccessibleAction(RecordingHudTarget target)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { Invoke(new Action(() => DoAccessibleAction(target))); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            // Visibility and state are checked here, on the UI thread, at the moment of execution.
            if (IsDisposed || !Visible) return;
            Activate(target);
        }

        // Tells assistive technology that the eyes changed. Called on the UI thread only.
        // Child ids are the MSAA child ids (1 = left eye, 2 = right eye). AccessibilityNotifyClients takes an
        // internal object id that the framework remaps, which sent the events to the wrong child; this sends them directly.
        private void NotifyEyesChanged(AccessibleEvents accEvent)
        {
            if (!IsHandleCreated || IsDisposed) return;
            const int ObjidClient = -4;
            NotifyWinEvent((uint)accEvent, Handle, ObjidClient, 1);
            NotifyWinEvent((uint)accEvent, Handle, ObjidClient, 2);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void NotifyWinEvent(uint winEvent, IntPtr hwnd, int idObject, int idChild);

        protected override void OnShown(EventArgs args) { base.OnShown(args); PlaceOnScreen(); }
        // Eye bounds are computed from the window position and size, so a move, resize or DPI change
        // must tell assistive technology to read them again.
        protected override void OnLocationChanged(EventArgs args) { base.OnLocationChanged(args); NotifyEyesChanged(AccessibleEvents.LocationChange); }
        protected override void OnSizeChanged(EventArgs args) { base.OnSizeChanged(args); NotifyEyesChanged(AccessibleEvents.LocationChange); }
        protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); Redraw(); }
        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            var target = Clickable(RecordingHudLayout.HitTest(args.Location, ClientSize));
            if (target != hovered) SetHovered(target);
        }
        protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); SetHovered(RecordingHudTarget.None); }
        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button != MouseButtons.Left) return;
            Activate(RecordingHudLayout.HitTest(args.Location, ClientSize));
        }

        private void PlaceOnScreen()
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, atTop ? area.Top + 12 : area.Bottom - Height - 12);
            Redraw();
        }

        private void Redraw()
        {
            if (IsDisposed || !IsHandleCreated) return;
            using (var frame = RenderFrame()) LayeredWindow.Present(Handle, Location, frame);
        }

        private RecordingHudTarget Clickable(RecordingHudTarget target)
        {
            return RecordingHudLayout.ActionFor(target, state) == RecordingHudAction.None ? RecordingHudTarget.None : target;
        }

        private void SetHovered(RecordingHudTarget target)
        {
            hovered = Clickable(target);
            Cursor = hovered == RecordingHudTarget.None ? Cursors.Default : Cursors.Hand;
            tips.SetToolTip(this, TipFor(hovered));
            Redraw();
        }

        private string TipFor(RecordingHudTarget target)
        {
            switch (target)
            {
                case RecordingHudTarget.Cancel: return "取消";
                case RecordingHudTarget.Finish: return state == DictationState.Recording ? "完成錄音" : state == DictationState.RetainedAudio ? "重試辨識" : "開始錄音";
                default: return "";
            }
        }

        private void PaintHover(Graphics graphics, RecordingHudTarget target, Rectangle eye)
        {
            if (hovered != target) return;
            using (var pen = new Pen(Color.FromArgb(160, 190, 255), 2)) graphics.DrawEllipse(pen, eye.X + 2, eye.Y + 2, eye.Width - 5, eye.Height - 5);
        }

        private static string StateLabel(DictationState value)
        {
            switch (value)
            {
                case DictationState.Starting: return "正在開啟麥克風…";
                case DictationState.Recording: return "正在聽你說話…";
                case DictationState.Finalizing: return "正在整理錄音…";
                case DictationState.Transcribing: return "正在辨識…";
                case DictationState.Delivering: return "正在放入文字…";
                case DictationState.RetainedAudio: return "辨識未完成，可重試或取消";
                default: return "按快捷鍵開始說話";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { catCache.Dispose(); cat.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public enum RecordingHudTarget { None, Cancel, Finish }

    // Container for the two eyes. Name and description keep describing the whole HUD.
    public sealed class RecordingHudContainerAccessibleObject : AccessibleObject
    {
        private readonly RecordingHud hud;
        private readonly RecordingHudEyeAccessibleObject cancel, finish;
        public RecordingHudContainerAccessibleObject(RecordingHud hud)
        {
            this.hud = hud;
            cancel = new RecordingHudEyeAccessibleObject(hud, RecordingHudTarget.Cancel, this);
            finish = new RecordingHudEyeAccessibleObject(hud, RecordingHudTarget.Finish, this);
        }
        public override string Name { get { return hud.AccessibleName; } }
        public override string Description { get { return hud.AccessibleDescription; } }
        public override AccessibleRole Role { get { return AccessibleRole.Window; } }
        public override Rectangle Bounds { get { return hud.IsDisposed ? Rectangle.Empty : hud.RectangleToScreen(new Rectangle(Point.Empty, hud.ClientSize)); } }
        public override int GetChildCount() { return 2; }
        public override AccessibleObject GetChild(int index)
        {
            if (index == 0) return cancel;
            if (index == 1) return finish;
            return null;
        }
    }

    // One eye as a push button. Name, enabled state and default action all come from the same rule as the mouse.
    public sealed class RecordingHudEyeAccessibleObject : AccessibleObject
    {
        private readonly RecordingHud hud;
        private readonly RecordingHudTarget target;
        private readonly AccessibleObject parent;
        public RecordingHudEyeAccessibleObject(RecordingHud hud, RecordingHudTarget target, AccessibleObject parent)
        {
            this.hud = hud; this.target = target; this.parent = parent;
        }
        public override AccessibleObject Parent { get { return parent; } }
        public override string Name { get { return hud.EyeName(target); } }
        public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
        public override AccessibleStates State
        {
            get { return hud.ActionFor(target) == RecordingHudAction.None ? AccessibleStates.Unavailable : AccessibleStates.None; }
        }
        // Screen coordinates, recomputed on every request so moves and DPI changes are reflected.
        public override Rectangle Bounds
        {
            get
            {
                if (hud.IsDisposed) return Rectangle.Empty;
                var eye = target == RecordingHudTarget.Cancel ? RecordingHudLayout.CancelEye(hud.ClientSize) : RecordingHudLayout.FinishEye(hud.ClientSize);
                return hud.RectangleToScreen(eye);
            }
        }
        public override void DoDefaultAction() { hud.DoAccessibleAction(target); }
    }

    // What an eye does in the current dictation state.
    public enum RecordingHudAction { None, Start, Finish, Retry, Cancel }

    // Geometry shared by painting and hit testing. Eyes are ellipses inscribed in these rectangles.
    public static class RecordingHudLayout
    {
        public static Rectangle CancelEye(Size clientSize)
        {
            int catHeight = CatHeight(clientSize);
            return new Rectangle((int)(clientSize.Width * .14), (int)(catHeight * .53), (int)(clientSize.Width * .18), (int)(catHeight * .29));
        }

        public static Rectangle FinishEye(Size clientSize)
        {
            int catHeight = CatHeight(clientSize);
            return new Rectangle((int)(clientSize.Width * .67), (int)(catHeight * .53), (int)(clientSize.Width * .19), (int)(catHeight * .29));
        }

        // Single rule for clicks, hover and accessibility. The right eye starts, finishes or retries
        // depending on the state; the left eye cancels whenever dictation is not idle or delivering.
        public static RecordingHudAction ActionFor(RecordingHudTarget target, DictationState state)
        {
            switch (target)
            {
                case RecordingHudTarget.Cancel:
                    return state != DictationState.Idle && state != DictationState.Delivering ? RecordingHudAction.Cancel : RecordingHudAction.None;
                case RecordingHudTarget.Finish:
                    if (state == DictationState.Recording) return RecordingHudAction.Finish;
                    if (state == DictationState.RetainedAudio) return RecordingHudAction.Retry;
                    if (state == DictationState.Idle) return RecordingHudAction.Start;
                    return RecordingHudAction.None;
                default:
                    return RecordingHudAction.None;
            }
        }

        public static RecordingHudTarget HitTest(Point point, Size clientSize)
        {
            if (InsideEllipse(CancelEye(clientSize), point)) return RecordingHudTarget.Cancel;
            if (InsideEllipse(FinishEye(clientSize), point)) return RecordingHudTarget.Finish;
            return RecordingHudTarget.None;
        }

        // Maps decibels to a 0..1 meter fill; unknown or non-finite input yields 0.
        public static double LevelFraction(double decibels, bool isKnown)
        {
            if (!isKnown || Double.IsNaN(decibels) || Double.IsInfinity(decibels)) return 0;
            return Math.Max(0, Math.Min(1, (decibels + 60) / 60));
        }

        private static int CatHeight(Size clientSize)
        {
            return (int)(clientSize.Width * .4978);
        }

        private static bool InsideEllipse(Rectangle box, Point point)
        {
            double dx = (point.X - (box.Left + box.Width / 2.0)) / (box.Width / 2.0);
            double dy = (point.Y - (box.Top + box.Height / 2.0)) / (box.Height / 2.0);
            return dx * dx + dy * dy <= 1;
        }
    }
}
