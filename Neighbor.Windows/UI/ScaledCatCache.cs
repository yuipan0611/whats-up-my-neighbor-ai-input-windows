using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Neighbor.Windows.UI
{
    // The cat artwork never changes, so it is scaled once per drawn size (which already includes DPI)
    // instead of on every HUD frame. The cache is used on the UI thread only.
    public sealed class ScaledCatCache : IDisposable
    {
        private Image source;
        private Bitmap scaled;
        private Size scaledSize;
        private bool disposed;

        public ScaledCatCache(Image source)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
        }

        // Returns the scaled image; a new size rebuilds it and releases the previous one.
        public Bitmap Get(Size size)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ScaledCatCache));
            if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (scaled != null && scaledSize == size) return scaled;
            Invalidate();
            scaled = Scale(source, size);
            scaledSize = size;
            return scaled;
        }

        // A different artwork source makes every cached size stale.
        public void SetSource(Image value)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ScaledCatCache));
            source = value ?? throw new ArgumentNullException(nameof(value));
            Invalidate();
        }

        public void Invalidate()
        {
            if (scaled != null) { scaled.Dispose(); scaled = null; }
            scaledSize = Size.Empty;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Invalidate();
        }

        private static Bitmap Scale(Image source, Size size)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
            }
            return bitmap;
        }
    }
}
