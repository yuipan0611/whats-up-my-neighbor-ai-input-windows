using System.Buffers.Binary;

namespace NekoChat.Core.Attachments;

public enum AttachmentImageFormat { Unknown, Png, Jpeg, Gif, WebP, Tiff, Ico, Bmp, Heif }
public enum AttachmentImageHeaderStatus { NotImage, Ready, Invalid, TooLarge, DimensionsExceeded, NotSupported }
public sealed record AttachmentImageHeader(AttachmentImageFormat Format, AttachmentImageHeaderStatus Status, int Width = 0, int Height = 0)
{
    public bool CanDecode => Status == AttachmentImageHeaderStatus.Ready;
}

/// Bounded content recognition only; the installed decoder must validate the compressed first frame too.
public static class AttachmentImageProbe
{
    public const int MaximumHeaderBytes = 65536;
    public static bool ValidDimensions(int width, int height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= 32_000_000;
    public static bool Eligible(string state, bool hasPath, long size) => state == "complete" && hasPath && size is > 0 and <= AttachmentLimits.MaximumFileBytes;
    public static bool ImageName(string name) => new[] { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".tif", ".tiff", ".ico", ".bmp", ".heic", ".heif", ".avif", ".svg" }
        .Contains(Path.GetExtension(name).ToLowerInvariant(), StringComparer.Ordinal);

    public static AttachmentImageHeader Inspect(ReadOnlySpan<byte> bytes, long fileLength)
    {
        if (fileLength > AttachmentLimits.MaximumFileBytes) return new(AttachmentImageFormat.Unknown, AttachmentImageHeaderStatus.TooLarge);
        if (fileLength <= 0 || bytes.Length > fileLength) return new(AttachmentImageFormat.Unknown, AttachmentImageHeaderStatus.Invalid);
        bytes = bytes[..Math.Min(bytes.Length, MaximumHeaderBytes)];
        AttachmentImageHeader Header(AttachmentImageFormat format, int? width = null, int? height = null) =>
            new(format, width.HasValue && height.HasValue
                ? ValidDimensions(width.Value, height.Value) ? AttachmentImageHeaderStatus.Ready : AttachmentImageHeaderStatus.DimensionsExceeded
                : AttachmentImageHeaderStatus.Ready, width ?? 0, height ?? 0);
        AttachmentImageHeader Bad(AttachmentImageFormat format) => new(format, AttachmentImageHeaderStatus.Invalid);
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (bytes.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return Bad(AttachmentImageFormat.Png);
            return Header(AttachmentImageFormat.Png, SafeInt(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..])), SafeInt(BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])));
        }
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
        {
            int cursor = 2;
            while (cursor + 1 < bytes.Length)
            {
                if (bytes[cursor++] != 255) return Bad(AttachmentImageFormat.Jpeg);
                while (cursor < bytes.Length && bytes[cursor] == 255) cursor++;
                if (cursor >= bytes.Length) break;
                int marker = bytes[cursor++];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                if (marker is 0xD9 or 0xDA) return Bad(AttachmentImageFormat.Jpeg);
                if (cursor + 2 > bytes.Length) break;
                int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[cursor..]);
                if (size < 2 || (long)cursor + size > fileLength) return Bad(AttachmentImageFormat.Jpeg);
                bool frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (frame)
                {
                    if (size < 8 || cursor + 7 > bytes.Length) return Bad(AttachmentImageFormat.Jpeg);
                    return Header(AttachmentImageFormat.Jpeg, BinaryPrimitives.ReadUInt16BigEndian(bytes[(cursor + 5)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(cursor + 3)..]));
                }
                cursor += size;
            }
            return bytes.Length < fileLength ? Header(AttachmentImageFormat.Jpeg) : Bad(AttachmentImageFormat.Jpeg);
        }
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return bytes.Length < 13 ? Bad(AttachmentImageFormat.Gif) :
                Header(AttachmentImageFormat.Gif, BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (bytes.Length < 30 || (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8 > fileLength ||
                (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) + 20 > fileLength) return Bad(AttachmentImageFormat.WebP);
            if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
                return Header(AttachmentImageFormat.WebP, UInt24(bytes[24..]) + 1, UInt24(bytes[27..]) + 1);
            if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
                return Header(AttachmentImageFormat.WebP, (bytes[21] | (bytes[22] & 0x3F) << 8) + 1,
                    ((bytes[22] >> 6) | bytes[23] << 2 | (bytes[24] & 15) << 10) + 1);
            if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 1, 0x2A }))
                return Header(AttachmentImageFormat.WebP, BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF,
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF);
            return Bad(AttachmentImageFormat.WebP);
        }
        bool littleTiff = bytes.StartsWith(new byte[] { 0x49, 0x49, 42, 0 });
        bool bigTiff = bytes.StartsWith(new byte[] { 0x4D, 0x4D, 0, 42 });
        if (littleTiff || bigTiff)
        {
            if (bytes.Length < 8) return Bad(AttachmentImageFormat.Tiff);
            uint offset = U32(bytes[4..], littleTiff);
            if (offset < 8 || (long)offset + 2 > fileLength) return Bad(AttachmentImageFormat.Tiff);
            if ((long)offset + 2 > bytes.Length) return Header(AttachmentImageFormat.Tiff);
            int index = (int)offset, count = U16(bytes[index..], littleTiff);
            if (count > 4096 || (long)offset + 2 + (long)count * 12 + 4 > fileLength) return Bad(AttachmentImageFormat.Tiff);
            int width = 0, height = 0;
            for (int n = 0; n < count; n++)
            {
                int at = index + 2 + n * 12;
                if (at + 12 > bytes.Length) break;
                int tag = U16(bytes[at..], littleTiff), type = U16(bytes[(at + 2)..], littleTiff);
                if (tag is not (256 or 257) || U32(bytes[(at + 4)..], littleTiff) != 1) continue;
                int value = type == 3 ? U16(bytes[(at + 8)..], littleTiff) : type == 4 ? SafeInt(U32(bytes[(at + 8)..], littleTiff)) : 0;
                if (tag == 256) width = value; else height = value;
            }
            return width != 0 && height != 0 ? Header(AttachmentImageFormat.Tiff, width, height) : Header(AttachmentImageFormat.Tiff);
        }
        if (bytes.StartsWith(new byte[] { 0, 0, 1, 0 }))
        {
            if (bytes.Length < 22) return Bad(AttachmentImageFormat.Ico);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            long directory = 6L + count * 16;
            long length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]), offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]);
            if (count is < 1 or > 256 || directory > fileLength || length == 0 || offset < directory || offset + length > fileLength) return Bad(AttachmentImageFormat.Ico);
            // D-36: identify ICO by content, but never admit it to a preview decoder.
            return new(AttachmentImageFormat.Ico, AttachmentImageHeaderStatus.NotSupported,
                bytes[6] == 0 ? 256 : bytes[6], bytes[7] == 0 ? 256 : bytes[7]);
        }
        if (bytes.StartsWith("BM"u8))
        {
            if (bytes.Length < 26) return Bad(AttachmentImageFormat.Bmp);
            uint dib = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
            if (dib == 12) return Header(AttachmentImageFormat.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]));
            if (dib < 40 || bytes.Length < 54 || (long)dib + 14 > fileLength) return Bad(AttachmentImageFormat.Bmp);
            int height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
            return Header(AttachmentImageFormat.Bmp, BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]), height == int.MinValue ? -1 : Math.Abs(height));
        }
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes); int brandsAt = 8;
            if (size == 1)
            {
                if (bytes.Length < 24) return Bad(AttachmentImageFormat.Heif);
                ulong extended = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
                if (extended > MaximumHeaderBytes) return Bad(AttachmentImageFormat.Heif);
                size = (long)extended; brandsAt = 16;
            }
            if (size < brandsAt + 8 || size > fileLength || size > MaximumHeaderBytes || size > bytes.Length || (size - brandsAt) % 4 != 0)
                return Bad(AttachmentImageFormat.Heif);
            for (int at = brandsAt; at + 4 <= size; at += 4)
            {
                if (at == brandsAt + 4) continue; // minor version is not a compatible brand
                var brand = bytes.Slice(at, 4);
                if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return new(AttachmentImageFormat.Unknown, AttachmentImageHeaderStatus.NotImage);
            }
            for (int at = brandsAt; at + 4 <= size; at += 4)
            {
                if (at == brandsAt + 4) continue;
                var brand = bytes.Slice(at, 4);
                if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8) ||
                    brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
                    brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                    return Header(AttachmentImageFormat.Heif); // WIC checks first-frame dimensions before pixel materialization.
            }
        }
        return new(AttachmentImageFormat.Unknown, AttachmentImageHeaderStatus.NotImage);
    }

    private static int SafeInt(uint value) => value > int.MaxValue ? -1 : (int)value;
    private static int UInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    private static ushort U16(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
