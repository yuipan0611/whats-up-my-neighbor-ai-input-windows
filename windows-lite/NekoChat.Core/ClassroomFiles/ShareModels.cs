using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NekoChat.Core.Attachments;

namespace NekoChat.Core.ClassroomFiles;

public interface IShareSecrets
{
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}
public sealed record SharePeer(string Ip, int Port, bool Teacher = false);
public sealed record ShareManifest(string Id, string PublicKey, string Name, long Size, string Sha256, string UploaderIp, string Note, string CreatedUtc);
public sealed record ShareOperation(ShareManifest File, string Kind, string Signature);
public sealed record ShareLogEntry(long Sequence, ShareOperation Operation);
public sealed record SharedFile(ShareManifest File, bool Owned, string? BlobId, string State, string? Problem, int Replicas, long Sequence = 0);
public sealed record ShareSyncPage(string NodeId, IReadOnlyList<ShareLogEntry> Items);
public sealed record ShareStatus(bool Available, string Token);
public sealed record ShareRequest(int Version, string Kind, long After = 0, string? NodeId = null, ShareOperation? Operation = null, string? Id = null, long Offset = 0, int Count = 0, string? Token = null);

public static class ShareProtocol
{
    public const string Capability = "classroom-files.v1";
    public const string Prefix = "/nekochat/classroom-files/v1/";
    public const int MaximumOperations = 16384;
    internal static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    // A new per-share signing key gives the share an unforgeable id and withdraw authority without enrollment or numeric pairing.
    public static string Id(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    public static ShareOperation Sign(ShareManifest file, string kind, ECDsa key) => new(file, kind,
        Convert.ToBase64String(key.SignData(Bytes(new { Protocol = Capability, Kind = kind, File = file }), HashAlgorithmName.SHA256)));
    public static bool Valid(ShareOperation? op)
    {
        try
        {
            if (op?.File is not { } f || op.Kind is not ("add" or "withdraw") || !AttachmentLimits.IsHash(f.Id) ||
                !AttachmentLimits.ValidName(f.Name) || f.Size is < 0 or > AttachmentLimits.MaximumFileBytes || !AttachmentLimits.IsHash(f.Sha256) ||
                !LanInvitation.PrivateIPv4(f.UploaderIp) || f.Note is null || f.Note.Length > 240 || f.Note.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
                f.PublicKey is null || f.PublicKey.Length > 256 || op.Signature is null || op.Signature.Length > 128 ||
                !DateTimeOffset.TryParseExact(f.CreatedUtc, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) return false;
            using var key = ECDsa.Create(); var bytes = Convert.FromBase64String(f.PublicKey);
            key.ImportSubjectPublicKeyInfo(bytes, out int read);
            return read == bytes.Length && key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                Id(key) == f.Id && key.VerifyData(Bytes(new { Protocol = Capability, Kind = op.Kind, File = f }), Convert.FromBase64String(op.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    internal static T Parse<T>(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        Unique(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes.Span, Json) ?? throw new AttachmentException("invalid_payload");
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new AttachmentException("invalid_payload"); Unique(p.Value); }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
