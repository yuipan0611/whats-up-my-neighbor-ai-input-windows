using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Wire;

/// <summary>
/// NCMW-classroom-wire-v2 (frozen contract e956e0c5…2917): constants and pure helpers. No keys, no signatures, no I/O, no persistence.
/// Nothing here produces or accepts a V2Verified* object; the signed V2 route is untouched (contract §0.4).
/// </summary>
public static class ClassroomWire
{
    public const string Protocol = "nekochat-classroom";
    public const int Version = 1;
    /// <summary>D-06: UTF-8 bytes of message text. Same value as <see cref="V2Text.ProductMaxUtf8Bytes"/>.</summary>
    public const int MaxTextBytes = V2Text.ProductMaxUtf8Bytes;
    public const int ControlBodyMaxBytes = 8 * 1024;
    /// <summary>6 × L + 4096: worst-case JSON escaping (6 bytes per character) plus the envelope.</summary>
    public const int MessageBodyMaxBytes = 6 * MaxTextBytes + 4096;
    public const int HeadMaxBytes = 8 * 1024;
    public const int MaxHeaderLines = 65;
    public const int MaxCapabilities = 8;
    public static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(60);
    public const int SessionCapacity = 512;
    public const int SessionPerSource = 16;
    public const string PathPrefix = "/nekochat/classroom/v1/";
    public const string ContentHashDomain = "nekochat-classroom-v1 content\n";

    public static V2JsonLimits JsonLimits(int maxBytes) => new(maxBytes, 8, 64);

    /// <summary>§10: error code → HTTP status. Unknown codes are not part of the wire.</summary>
    public static bool TryErrorStatus(string? code, out int status)
    {
        status = code switch
        {
            ClassroomCodes.InvalidPayload or ClassroomCodes.UnsupportedVersion => 400,
            ClassroomCodes.PayloadTooLarge => 413, ClassroomCodes.RateLimited => 429,
            ClassroomCodes.WrongRecipient or ClassroomCodes.StaleSession or ClassroomCodes.Conflict => 409,
            ClassroomCodes.SourceNotAllowed => 403,
            ClassroomCodes.CapacityExceeded or ClassroomCodes.ReceiverNotReady or ClassroomCodes.StorageUnavailable or ClassroomCodes.Transient => 503,
            _ => 0
        };
        return status != 0;
    }

    /// <summary>§8: hex_lower(SHA-256(domain ‖ message_id ‖ LF ‖ (reaction ?? "") ‖ LF ‖ text)). Text is hashed as-is: no normalisation, no trimming.</summary>
    public static string ContentSha256(string messageId, string? reaction, string text)
    {
        ArgumentNullException.ThrowIfNull(messageId); ArgumentNullException.ThrowIfNull(text);
        var utf8 = new UTF8Encoding(false, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(utf8.GetBytes(ContentHashDomain + messageId + "\n" + (reaction ?? "") + "\n"));
        hash.AppendData(utf8.GetBytes(text));
        return V2Format.ToLowerHex(hash.GetHashAndReset());
    }

    // ---- field formats (contract §4.2). Whole-string, hand-written, no Regex.
    internal static bool IsIPv4(string? s)
    {
        if (s is null) return false;
        int parts = 0, start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] != '.') continue;
            int len = i - start;
            if (len is < 1 or > 3) return false;
            int value = 0;
            for (int j = start; j < i; j++) { if (s[j] is < '0' or > '9') return false; value = value * 10 + (s[j] - '0'); }
            if (len > 1 && s[start] == '0') return false;
            if (value > 255) return false;
            parts++; start = i + 1;
        }
        return parts == 4;
    }

    // ---- source attribution (contract §2.1, CM-10, CM-16)
    /// <summary>IPv4 as dotted decimal; IPv4-mapped IPv6 becomes IPv4; anything else (and any non-canonical IPv4 text) is null.</summary>
    public static string? CanonicalSource(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out var ip)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString() == text ? text : null;
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : null;
    }

    public static string? CanonicalSource(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : null;
    }

    /// <summary>Same ranges as LanInvitation.PrivateIPv4: 127/8, 10/8, 172.16/12, 192.168/16. <paramref name="canonical"/> must already be canonical IPv4 text.</summary>
    public static bool IsPrivateIPv4(string? canonical)
    {
        if (!IsIPv4(canonical)) return false;
        string[] p = canonical!.Split('.');
        int a = int.Parse(p[0]), b = int.Parse(p[1]);
        return a == 127 || a == 10 || a == 192 && b == 168 || a == 172 && b is >= 16 and <= 31;
    }

    /// <summary>Canonical source if the address may use the classroom route; null → <see cref="ClassroomCodes.SourceNotAllowed"/>.</summary>
    public static string? AdmitSource(string? text) => CanonicalSource(text) is { } c && IsPrivateIPv4(c) ? c : null;
    public static string? AdmitSource(IPAddress? address) => CanonicalSource(address) is { } c && IsPrivateIPv4(c) ? c : null;
}

/// <summary>Wire error codes (contract §10) plus the sender-local result codes.</summary>
public static class ClassroomCodes
{
    public const string Ok = "ok";
    public const string InvalidPayload = "invalid_payload", UnsupportedVersion = "unsupported_version", PayloadTooLarge = "payload_too_large", RateLimited = "rate_limited",
        WrongRecipient = "wrong_recipient", StaleSession = "stale_session", Conflict = "conflict", SourceNotAllowed = "source_not_allowed", CapacityExceeded = "capacity_exceeded",
        ReceiverNotReady = "receiver_not_ready", StorageUnavailable = "storage_unavailable", Transient = "transient";
    public const string InvalidResponse = "invalid_response", AckMismatch = "ack_mismatch", CapabilityMissing = "capability_missing", RemoteError = "remote_error";
}

public enum LegacyProbeResult { NotLegacy, LegacyCandidate }

/// <summary>CM-14: the only legacy signal is the byte-exact reply of the 0bb8bcb listener to a text-less hello, and only before any classroom message may have been written.</summary>
public static class ClassroomLegacyProbe
{
    private static ReadOnlySpan<byte> Exact => "{\"error\":\"empty_text\"}"u8;
    public static LegacyProbeResult Classify(int httpStatus, ReadOnlySpan<byte> body, bool messagePossiblyWritten) =>
        !messagePossiblyWritten && httpStatus == 400 && body.SequenceEqual(Exact) ? LegacyProbeResult.LegacyCandidate : LegacyProbeResult.NotLegacy;
}
