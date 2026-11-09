using System.Buffers.Binary;
using NekoChat.Core.Attachments;
using NekoChat.TestFixtures;

namespace NekoChat.Tests;

internal static class AttachmentImageProbeChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException("AP " + label); }
        var expected = new[] { AttachmentImageFormat.Png, AttachmentImageFormat.Jpeg, AttachmentImageFormat.Gif, AttachmentImageFormat.WebP,
            AttachmentImageFormat.Tiff, AttachmentImageFormat.Ico, AttachmentImageFormat.Bmp, AttachmentImageFormat.Heif };
        for (int n = 0; n < AttachmentPreviewFixtures.Names.Length; n++)
        {
            string name = AttachmentPreviewFixtures.Names[n]; byte[] bytes = AttachmentPreviewFixtures.Read(name);
            var actual = AttachmentImageProbe.Inspect(bytes, bytes.Length);
            Check(actual.Format == expected[n] && actual.CanDecode, "AP-01 embedded real content recognized: " + name);
            Check(name == "heic" || (name == "ico" ? actual is { Width: 16, Height: 16 } : actual is { Width: 64, Height: 48 }), "AP-03 known header dimensions: " + name);
            Check(!AttachmentImageProbe.Inspect(bytes.AsSpan(0, Math.Min(7, bytes.Length)), Math.Min(7, bytes.Length)).CanDecode,
                "AP-02 truncated magic/header rejected: " + name);
            Check(AttachmentImageProbe.Inspect(bytes, AttachmentLimits.MaximumFileBytes + 1).Status == AttachmentImageHeaderStatus.TooLarge,
                "AP-03 file cap enforced before decoder: " + name);
        }
        byte[] gif = AttachmentPreviewFixtures.Read("gif");
        Check(!AttachmentImageProbe.ImageName("photo.txt") && AttachmentImageProbe.Inspect(gif, gif.Length).Format == AttachmentImageFormat.Gif,
            "AP-02 image with wrong extension remains content-recognized");
        ReadOnlySpan<byte> fake = "%PDF-1.7 fake jpeg"u8, html = "<html><body>not a photo</body>"u8;
        Check(AttachmentImageProbe.ImageName("fake.jpg") && AttachmentImageProbe.Inspect(fake, fake.Length).Status == AttachmentImageHeaderStatus.NotImage,
            "AP-02 fake image extension never authorizes decode");
        Check(AttachmentImageProbe.Inspect(html, html.Length).Status == AttachmentImageHeaderStatus.NotImage,
            "AP-02 non-image content rejected without decoder");
        byte[] png = AttachmentPreviewFixtures.Read("png");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 16385);
        Check(AttachmentImageProbe.Inspect(png, png.Length).Status == AttachmentImageHeaderStatus.DimensionsExceeded, "AP-03 overwide PNG header denied");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 8192); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 8192);
        Check(AttachmentImageProbe.Inspect(png, png.Length).Status == AttachmentImageHeaderStatus.DimensionsExceeded, "AP-03 pixel-product cap denied");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 0); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 0);
        Check(!AttachmentImageProbe.Inspect(png, png.Length).CanDecode, "AP-03 zero PNG dimensions rejected");
        byte[] webp = new byte[30]; "RIFF"u8.CopyTo(webp); BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), 22);
        "WEBPVP8X"u8.CopyTo(webp.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16), 10);
        webp[24] = webp[25] = webp[26] = 255;
        Check(AttachmentImageProbe.Inspect(webp, webp.Length).Status == AttachmentImageHeaderStatus.DimensionsExceeded, "AP-03 extended WebP huge canvas rejected");
        byte[] bmp = AttachmentPreviewFixtures.Read("bmp"); BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), int.MaxValue);
        Check(!AttachmentImageProbe.Inspect(bmp, bmp.Length).CanDecode, "AP-03 BMP huge dimensions rejected");
        byte[] tiff = AttachmentPreviewFixtures.Read("tiff");
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        for (int at = ifd + 2; at + 12 <= tiff.Length; at += 12)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) != 256) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), 4); BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), 20000); break;
        }
        Check(!AttachmentImageProbe.Inspect(tiff, tiff.Length).CanDecode, "AP-03 TIFF huge first-page header rejected");
        byte[] brokenWebp = AttachmentPreviewFixtures.Read("webp"); BinaryPrimitives.WriteUInt32LittleEndian(brokenWebp.AsSpan(4), 100000);
        Check(!AttachmentImageProbe.Inspect(brokenWebp, brokenWebp.Length).CanDecode, "AP-02 truncated RIFF container rejected");
        foreach (string state in new[] { "queued", "sending", "offered", "receiving", "cancelled", "failed", "unavailable" })
            Check(!AttachmentImageProbe.Eligible(state, true, 100), "AP-05 incomplete state cannot decode: " + state);
        Check(AttachmentImageProbe.Eligible("complete", true, 100) && !AttachmentImageProbe.Eligible("complete", false, 100) &&
            !AttachmentImageProbe.Eligible("complete", true, AttachmentLimits.MaximumFileBytes + 1), "AP-05 complete/path/size all required");
        Check(AttachmentImageProbe.ValidDimensions(16384, 1) && AttachmentImageProbe.ValidDimensions(8000, 4000) &&
            !AttachmentImageProbe.ValidDimensions(16385, 1) && !AttachmentImageProbe.ValidDimensions(8000, 4001), "AP-03 original dimension limits unchanged");
        var formats = Enum.GetValues<AttachmentImageFormat>().Where(format => format != AttachmentImageFormat.Unknown).ToArray();
        foreach (var format in formats)
            Check(AttachmentDecoderPolicy.ContainerFormat(format) != Guid.Empty, "AP-04 every supported format has a WIC container: " + format);
        Check(formats.Select(AttachmentDecoderPolicy.ContainerFormat).Distinct().Count() == formats.Length, "AP-04 container GUIDs are distinct");
        Check(AttachmentDecoderPolicy.ContainerFormat(AttachmentImageFormat.WebP) == new Guid("e094b0e2-67f2-45b3-b0ea-115337ca7cf3") &&
            AttachmentDecoderPolicy.ContainerFormat(AttachmentImageFormat.Heif) == new Guid("e1e62521-6787-405b-a339-500715b5763f"), "AP-04 extension containers match Windows SDK");
        foreach (uint success in new uint[] { 0, 1 })
            Check(AttachmentDecoderPolicy.FromCreateResult((int)success) == AttachmentDecoderRegistration.Registered, "AP-04 WIC query success");
        foreach (uint missing in new uint[] { 0x88982F50, 0x80040154 })
            Check(AttachmentDecoderPolicy.FromCreateResult(unchecked((int)missing)) == AttachmentDecoderRegistration.NotRegistered, "AP-04 explicit registration absence");
        foreach (uint unknown in new uint[] { 0x80004005, 0x88982F07, 0x88982F60, 0x80131515 })
            Check(AttachmentDecoderPolicy.FromCreateResult(unchecked((int)unknown)) == AttachmentDecoderRegistration.Unknown, "AP-04 generic/format/corrupt/NotSupported is not absence");
        foreach (var registration in Enum.GetValues<AttachmentDecoderRegistration>())
            Check(AttachmentDecoderPolicy.Failure(registration) == (registration == AttachmentDecoderRegistration.NotRegistered
                ? AttachmentDecoderFailure.DecoderUnavailable : AttachmentDecoderFailure.DecodeFailed),
                "AP-04 absence distinct from registered corrupt data and unknown query: " + registration);
        // Deterministic malformed-header corpus: all probes must remain bounded and exception-free.
        var random = new Random(35);
        for (int n = 0; n < 64; n++)
        {
            byte[] malformed = new byte[1 + n * 2]; random.NextBytes(malformed);
            bool failed = false; try { _ = AttachmentImageProbe.Inspect(malformed, malformed.Length); } catch { failed = true; }
            Check(!failed, "AP-02 malformed bounded header " + n);
        }
        Console.WriteLine($"Attachment image probe: {checks} AP-01/02/03/04/05 embedded format, false extension, bounds, decoder classification and malformed-header checks passed.");
        return checks;
    }
}
