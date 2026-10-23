using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Neighbor.Windows.UI
{
    // Per-pixel alpha helpers for layered windows. Color-key transparency has no alpha, so
    // anti-aliased edge pixels blend with the key color and leave a colored fringe.
    public static class LayeredWindow
    {
        // Converts a straight-alpha color to premultiplied form: each channel is scaled by alpha / 255, rounded to nearest.
        public static Color Premultiply(Color color)
        {
            int alpha = color.A;
            return Color.FromArgb(alpha, Scale(color.R, alpha), Scale(color.G, alpha), Scale(color.B, alpha));
        }

        // Renders a 32bpp premultiplied ARGB rounded rectangle filled with an opaque color; outside the corners is alpha 0.
        public static Bitmap RenderRoundedPanel(Size size, int radius, Color fill)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var path = RoundedPath(new Rectangle(Point.Empty, size), radius))
            using (var brush = new SolidBrush(fill))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillPath(brush, path);
            }
            return bitmap;
        }

        // Pushes a premultiplied 32bpp bitmap to a layered window at the given screen position.
        // The bitmap must be Format32bppPArgb; its size becomes the window size.
        public static bool Present(IntPtr hwnd, Point location, Bitmap bitmap)
        {
            if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb) throw new ArgumentException("Layered windows require premultiplied 32bpp ARGB.", "bitmap");
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                // The HBITMAP selects into the DC; the pixel buffer pointer is what CopyPixels must write to.
                dib = CreateDib(screen, bitmap.Width, bitmap.Height, out IntPtr pixels);
                if (dib == IntPtr.Zero || pixels == IntPtr.Zero) return false;
                CopyPixels(bitmap, pixels);
                previous = SelectObject(memory, dib);
                var size = bitmap.Size;
                var source = Point.Empty;
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                // A failed update must surface: a silently missing HUD or companion hides startup problems.
                if (!UpdateLayeredWindow(hwnd, screen, ref location, ref size, memory, ref source, 0, ref blend, ULW_ALPHA))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed.");
                return true;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static int Scale(int channel, int alpha)
        {
            return (channel * alpha + 127) / 255;
        }

        internal static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
        {
            var path = new GraphicsPath(); int diameter = radius * 2;
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }

        private static void CopyPixels(Bitmap bitmap, IntPtr destination)
        {
            var rect = new Rectangle(Point.Empty, bitmap.Size);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // Top-down DIB with stride = width * 4 matches the bitmap rows exactly.
                int length = bitmap.Width * 4 * bitmap.Height;
                var buffer = new byte[length];
                Marshal.Copy(data.Scan0, buffer, 0, length);
                Marshal.Copy(buffer, 0, destination, length);
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static IntPtr CreateDib(IntPtr screen, int width, int height, out IntPtr pixels)
        {
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32, biCompression = BI_RGB,
            };
            return CreateDIBSection(screen, ref info, DIB_RGB_COLORS, out pixels, IntPtr.Zero, 0);
        }

        private const int ULW_ALPHA = 0x2, DIB_RGB_COLORS = 0, BI_RGB = 0;
        private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point sourceOrigin, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
