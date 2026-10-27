namespace NekoChat.Core;

/// Coverage exported by the native macOS renderer: face, then upper bib.
/// Bilinear sampling retains the narrow antialiased border at arbitrary pixels.
public sealed class TuxedoMask
{
    private const int Size = 512;
    private readonly byte[] pixels;
    public TuxedoMask(byte[] pixels)
    {
        if (pixels.Length != 2 * Size * Size) throw new InvalidDataException("賓士貓花色素材不完整。");
        this.pixels = pixels;
    }
    public double Coverage(bool chest, double u, double v)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1) return 0;
        double x = Math.Min(Size - 1, u * Size), y = Math.Min(Size - 1, v * Size);
        int ix = (int)x, iy = (int)y, nx = Math.Min(Size - 1, ix + 1), ny = Math.Min(Size - 1, iy + 1), offset = chest ? Size * Size : 0;
        double fx = x - ix, fy = y - iy;
        double top = pixels[offset + iy * Size + ix] * (1 - fx) + pixels[offset + iy * Size + nx] * fx;
        double bottom = pixels[offset + ny * Size + ix] * (1 - fx) + pixels[offset + ny * Size + nx] * fx;
        return (top * (1 - fy) + bottom * fy) / 255;
    }
}
