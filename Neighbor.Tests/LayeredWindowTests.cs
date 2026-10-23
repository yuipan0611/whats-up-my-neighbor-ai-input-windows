using System;
using System.Drawing;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    // Pure-data checks for the per-pixel alpha HUD. Expected values are literal numbers or
    // derived from the specification, never from the implementation's own formula.
    public static class LayeredWindowTests
    {
        private static readonly Color PanelFill = Color.FromArgb(255, 30, 31, 35);

        public static void Run(Action<bool, string> check)
        {
            PresentChecks(check);
            Expect(check, "Half-transparent magenta is scaled by alpha instead of keeping full magenta", () =>
            {
                var pixel = LayeredWindow.Premultiply(Color.FromArgb(128, 255, 0, 255));
                return pixel.A == 128 && pixel.R == 128 && pixel.G == 0 && pixel.B == 128;
            });
            Expect(check, "Fully transparent pixel premultiplies to all zero regardless of color", () =>
            {
                var pixel = LayeredWindow.Premultiply(Color.FromArgb(0, 255, 0, 255));
                return pixel.A == 0 && pixel.R == 0 && pixel.G == 0 && pixel.B == 0;
            });
            Expect(check, "Opaque pixel is unchanged by premultiplication", () =>
            {
                var pixel = LayeredWindow.Premultiply(PanelFill);
                return pixel.ToArgb() == PanelFill.ToArgb();
            });
            Expect(check, "Premultiplication rounds to nearest (alpha 1 of white gives channel 1)", () =>
            {
                var pixel = LayeredWindow.Premultiply(Color.FromArgb(1, 255, 255, 255));
                return pixel.A == 1 && pixel.R == 1 && pixel.G == 1 && pixel.B == 1;
            });

            using (var panel = TryRenderPanel())
            {
                Expect(check, "Rounded panel is 32bpp premultiplied ARGB", () => panel.PixelFormat == PixelFormat.Format32bppPArgb);
                Expect(check, "Rounded panel corner pixel is fully transparent", () => RawPixels(panel)[0] == 0);
                Expect(check, "Rounded panel center pixel is the opaque fill color", () =>
                {
                    uint pixel = RawPixels(panel)[30 * 100 + 50];
                    return (pixel >> 24) == 255 && ((pixel >> 16) & 0xFF) == PanelFill.R && ((pixel >> 8) & 0xFF) == PanelFill.G && (pixel & 0xFF) == PanelFill.B;
                });
                Expect(check, "Rounded panel has anti-aliased partially transparent edge pixels", () =>
                {
                    int partial = 0;
                    foreach (uint pixel in RawPixels(panel)) { uint alpha = pixel >> 24; if (alpha > 0 && alpha < 255) partial++; }
                    return partial >= 10;
                });
                Expect(check, "Rounded panel edges contain no magenta fringe (every pixel is fill scaled by its alpha)", () => HasNoFringe(panel, PanelFill));
            }

            Expect(check, "Left eye keeps the original geometry (47, 89, 61, 49) at 340x220", () =>
                RecordingHudLayout.CancelEye(new Size(340, 220)) == new Rectangle(47, 89, 61, 49));
            Expect(check, "Right eye keeps the original geometry (227, 89, 64, 49) at 340x220", () =>
                RecordingHudLayout.FinishEye(new Size(340, 220)) == new Rectangle(227, 89, 64, 49));

            var size = new Size(340, 220);
            Expect(check, "Hit test at the left eye center cancels", () => RecordingHudLayout.HitTest(new Point(77, 113), size) == RecordingHudTarget.Cancel);
            Expect(check, "Hit test at the right eye center finishes", () => RecordingHudLayout.HitTest(new Point(259, 113), size) == RecordingHudTarget.Finish);
            Expect(check, "Hit test inside the left eye ellipse near its bottom cancels", () => RecordingHudLayout.HitTest(new Point(77, 133), size) == RecordingHudTarget.Cancel);
            Expect(check, "Hit test on the bounding-box corner of the left eye is not a hit", () => RecordingHudLayout.HitTest(new Point(47, 89), size) == RecordingHudTarget.None);
            Expect(check, "Hit test near the bottom-right corner inside the left eye box is not a hit", () => RecordingHudLayout.HitTest(new Point(107, 137), size) == RecordingHudTarget.None);
            Expect(check, "Hit test between the eyes is not a hit", () => RecordingHudLayout.HitTest(new Point(170, 113), size) == RecordingHudTarget.None);
            Expect(check, "Hit test on the text plate is not a hit", () => RecordingHudLayout.HitTest(new Point(170, 200), size) == RecordingHudTarget.None);

            Expect(check, "Meter fraction maps -15 dB to 0.75", () => RecordingHudLayout.LevelFraction(-15, true) == 0.75);
            Expect(check, "Meter fraction maps -60 dB to empty", () => RecordingHudLayout.LevelFraction(-60, true) == 0);
            Expect(check, "Meter fraction maps 0 dB to full", () => RecordingHudLayout.LevelFraction(0, true) == 1);
            Expect(check, "Meter fraction clamps above 0 dB to full", () => RecordingHudLayout.LevelFraction(5, true) == 1);
            Expect(check, "Meter fraction clamps below -60 dB to empty", () => RecordingHudLayout.LevelFraction(-80, true) == 0);
            Expect(check, "Meter fraction is empty when the level is unknown", () => RecordingHudLayout.LevelFraction(-15, false) == 0);
            Expect(check, "Meter fraction is empty for NaN input", () => RecordingHudLayout.LevelFraction(double.NaN, true) == 0);

            CheckPresentFailure(check, "Present to an invalid window handle throws Win32Exception", new IntPtr(1));
            IntPtr destroyed;
            using (var probe = new LayeredProbe()) destroyed = probe.Handle;
            CheckPresentFailure(check, "Present to a destroyed window handle throws Win32Exception", destroyed);

            using (var probe = new LayeredProbe())
            using (var frame = NewFrame())
            {
                IntPtr hwnd = probe.Handle;
                CheckGdiStability(check, "Successful Present", () => LayeredWindow.Present(hwnd, Point.Empty, frame));
            }
            using (var frame = NewFrame())
            {
                CheckGdiStability(check, "Failed Present (invalid handle)", () =>
                {
                    try { LayeredWindow.Present(new IntPtr(1), Point.Empty, frame); }
                    catch (Win32Exception) { }
                    return true;
                });
            }
        }

        // Net GDI growth allowed across the measured batches. Warm-up absorbs one-time caches, and measured
        // batch-to-batch drift was 0 in the first run. A leak of one object per Present would add about 100
        // per 100-call batch, far above this threshold, so linear leaks are still caught.
        private const int GdiThreshold = 4;
        private const int WarmupCount = 100, BatchCount = 5, OperationsPerBatch = 100;
        private const uint GR_GDIOBJECTS = 0;

        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        private static Bitmap NewFrame()
        {
            return new Bitmap(340, 220, PixelFormat.Format32bppPArgb);
        }

        // The expected native error code is not fixed here; the actual value is printed for the record.
        private static void CheckPresentFailure(Action<bool, string> check, string name, IntPtr hwnd)
        {
            using (var frame = NewFrame())
            {
                try
                {
                    LayeredWindow.Present(hwnd, Point.Empty, frame);
                    check(false, name + " (no exception thrown)");
                }
                catch (Win32Exception error)
                {
                    check(error.NativeErrorCode != 0, name + " (native error " + error.NativeErrorCode + ")");
                }
                catch (Exception error)
                {
                    check(false, name + " (unexpected " + error.GetType().Name + ")");
                }
            }
        }

        // Warms up, then records GDI object counts over BatchCount batches. A failed measurement (0 or exception) fails the check.
        private static void CheckGdiStability(Action<bool, string> check, string label, Func<bool> operation)
        {
            try
            {
                for (int i = 0; i < WarmupCount; i++) if (!operation()) throw new InvalidOperationException("operation failed during warm-up");
                int baseline = MeasureGdiObjects();
                int last = baseline;
                for (int batch = 1; batch <= BatchCount; batch++)
                {
                    for (int i = 0; i < OperationsPerBatch; i++) if (!operation()) throw new InvalidOperationException("operation failed during batch " + batch);
                    last = MeasureGdiObjects();
                    check(true, label + " batch " + batch + ": GDI objects " + last + " (baseline " + baseline + ")");
                }
                check(last - baseline <= GdiThreshold, label + ": GDI growth " + (last - baseline) + ", threshold " + GdiThreshold);
            }
            catch (Exception error) { check(false, label + " GDI measurement failed: " + error.Message); }
        }

        private static int MeasureGdiObjects()
        {
            uint count = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
            if (count == 0) throw new InvalidOperationException("GetGuiResources returned 0");
            return (int)count;
        }

        // Layered top-level window; the handle is created without being shown.
        private sealed class LayeredProbe : Form
        {
            public LayeredProbe()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
            }
            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x00080000; // WS_EX_LAYERED
                    return parameters;
                }
            }
        }

        private static Bitmap TryRenderPanel()
        {
            try { return LayeredWindow.RenderRoundedPanel(new Size(100, 60), 12, PanelFill); }
            catch (Exception) { return null; }
        }

        // Any exception from a not-yet-implemented member counts as a failed check, not a crashed suite.
        private static void Expect(Action<bool, string> check, string name, Func<bool> assertion)
        {
            bool passed;
            try { passed = assertion(); }
            catch (Exception) { passed = false; }
            check(passed, name);
        }

        // Reads raw premultiplied BGRA memory as ARGB integers, row-major.
        private static uint[] RawPixels(Bitmap bitmap)
        {
            var rect = new Rectangle(Point.Empty, bitmap.Size);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = new uint[bitmap.Width * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        pixels[y * bitmap.Width + x] = (uint)Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                return pixels;
            }
            finally { bitmap.UnlockBits(data); }
        }

        // Premultiplied output must equal the fill scaled by alpha (within rounding), with fully transparent pixels all zero.
        private static void PresentChecks(Action<bool, string> check)
        {
            // Regression: Present once passed the DIB handle as the pixel buffer and faulted at startup.
            using (var window = new LayeredProbeWindow())
            using (var panel = LayeredWindow.RenderRoundedPanel(new Size(64, 48), 12, PanelFill))
            {
                bool presented = false; Exception failure = null;
                try { presented = LayeredWindow.Present(window.Handle, new Point(0, 0), panel); }
                catch (Exception error) { failure = error; }
                check(failure == null && presented, "Present copies premultiplied pixels into a layered window without faulting" + (failure == null ? "" : " (" + failure.GetType().Name + ")"));
            }
        }

        private sealed class LayeredProbeWindow : Form
        {
            protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x00080000; return p; } } // WS_EX_LAYERED
        }

        private static bool HasNoFringe(Bitmap bitmap, Color fill)
        {
            foreach (uint pixel in RawPixels(bitmap))
            {
                int alpha = (int)(pixel >> 24), red = (int)((pixel >> 16) & 0xFF), green = (int)((pixel >> 8) & 0xFF), blue = (int)(pixel & 0xFF);
                if (alpha == 0) { if (pixel != 0) return false; continue; }
                if (red > alpha || green > alpha || blue > alpha) return false;
                if (Math.Abs(red - fill.R * alpha / 255.0) > 2) return false;
                if (Math.Abs(green - fill.G * alpha / 255.0) > 2) return false;
                if (Math.Abs(blue - fill.B * alpha / 255.0) > 2) return false;
            }
            return true;
        }
    }
}
