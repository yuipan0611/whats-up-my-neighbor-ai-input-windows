using System.IO;

namespace NekoChat.TestFixtures;

// Synthetic solid red64x48 (ICO16x16); GIF/TIFF second frame/page is blue. No user files or external images.
internal static class AttachmentPreviewFixtures
{
    internal static readonly string[] Names = ["png", "jpg", "gif", "webp", "tiff", "ico", "bmp", "heic"];
    internal static byte[] Read(string extension)
    {
        if (!Names.Contains(extension, StringComparer.Ordinal)) throw new ArgumentException("Unknown synthetic image fixture");
        using var input = typeof(AttachmentPreviewFixtures).Assembly.GetManifestResourceStream("NekoChat.PreviewFixtures.tiny." + extension)
            ?? throw new InvalidDataException("Missing embedded image fixture");
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return bytes.ToArray();
    }
}
