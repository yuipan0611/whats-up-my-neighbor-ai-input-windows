using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    // Cache behavior and pixel equivalence. Expected pixels come from drawing the source directly.
    public static class ScaledCatCacheTests
    {
        public static void Run(Action<bool, string> check)
        {
            using (var source = Theme.LoadCatImage())
            using (var cache = new ScaledCatCache(source))
            {
                var first = cache.Get(new Size(340, 169));
                check(ReferenceEquals(first, cache.Get(new Size(340, 169))), "Same size reuses the cached cat image");

                var larger = cache.Get(new Size(510, 253));
                check(larger.Width == 510 && larger.Height == 253, "A DPI or size change rebuilds the cat image at the new size");
                check(IsDisposed(first), "A size change releases the previous cat image");

                cache.Invalidate();
                check(IsDisposed(larger), "Invalidate releases the cached cat image");
                var rebuilt = cache.Get(new Size(340, 169));
                check(!IsDisposed(rebuilt) && rebuilt.Width == 340, "The first request after Invalidate builds a fresh image");
            }

            using (var source = Theme.LoadCatImage())
            using (var cache = new ScaledCatCache(source))
            {
                check(SamePixels(DirectScale(source, new Size(340, 169)), cache.Get(new Size(340, 169))), "Cached cat matches direct scaling at 100% (340 px)");
                check(SamePixels(DirectScale(source, new Size(510, 253)), cache.Get(new Size(510, 253))), "Cached cat matches direct scaling at 150% (510 px)");
            }

            using (var first = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            using (var second = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            {
                Fill(first, Color.FromArgb(255, 0, 0, 255));
                Fill(second, Color.FromArgb(255, 255, 0, 0));
                using (var cache = new ScaledCatCache(first))
                {
                    var before = cache.Get(new Size(8, 8)).GetPixel(4, 4);
                    cache.SetSource(second);
                    var after = cache.Get(new Size(8, 8)).GetPixel(4, 4);
                    check(before.B > 200 && after.R > 200 && after.B < 50, "Changing the source image rebuilds the cache from the new source");
                }
            }

            using (var source = Theme.LoadCatImage())
            {
                var cache = new ScaledCatCache(source);
                cache.Get(new Size(340, 169));
                cache.Dispose();
                cache.Dispose();
                bool threw = false;
                try { cache.Get(new Size(340, 169)); } catch (ObjectDisposedException) { threw = true; }
                check(threw, "Disposing twice is safe and later requests fail clearly");
            }
        }

        // On .NET Framework, GDI+ reports a disposed image as ArgumentException ("parameter is not valid").
        private static bool IsDisposed(Image image)
        {
            try { var ignored = image.Width; return false; }
            catch (ArgumentException) { return true; }
            catch (ObjectDisposedException) { return true; }
        }

        private static Bitmap DirectScale(Image source, Size size)
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

        private static void Fill(Bitmap bitmap, Color color)
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        }

        private static bool SamePixels(Bitmap expected, Bitmap actual)
        {
            if (expected.Size != actual.Size) return false;
            for (int y = 0; y < expected.Height; y++)
                for (int x = 0; x < expected.Width; x++)
                    if (expected.GetPixel(x, y).ToArgb() != actual.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
    }
}
