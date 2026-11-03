using System.Security.Cryptography;
using System.Text.Json;
using NekoChat.Core.V2;

namespace NekoChat.Core.Pairing;

public enum PairingKind { Request, Commitment, Nonce, Reveal, Confirmation, Pending, Cancel }

/// <summary>Immutable structure-validated control packet, not proof of consent, signature validity or trust.</summary>
public sealed class PairingPacket
{
    internal PairingPacket(PairingKind kind, string session, string? spki = null, string? nonce = null, string? commitment = null, string? signature = null, string? reason = null)
    { Kind = kind; SessionId = session; PublicSpkiBase64 = spki; Nonce = nonce; Commitment = commitment; SignatureBase64 = signature; CancelReason = reason; }
    public PairingKind Kind { get; }
    public string SessionId { get; }
    public string? PublicSpkiBase64 { get; }
    public string? Nonce { get; }
    public string? Commitment { get; }
    public string? SignatureBase64 { get; }
    public string? CancelReason { get; }
}

public static class PairingWire
{
    public const int MaxBodyBytes = 1024;
    private static readonly V2JsonLimits Limits = new(MaxBodyBytes, 4, 32);
    public static bool TryParse(ReadOnlySpan<byte> body, out PairingPacket? packet)
    {
        packet = null;
        if (!V2Json.TryParseObject(body, Limits, out var parsed, out _) || parsed is not { } p ||
            !p.TryGetString("protocol", out var protocol) || protocol != "nekochat" || !p.TryGetNumberToken("version", out var version) || version != "2" ||
            !p.TryGetString("kind", out var kind) || !p.TryGetString("session_id", out var session) || !V2Format.IsNonce22(session)) return false;
        string? spki = null, nonce = null, commitment = null, signature = null, reason = null;
        bool Fields(params string[] extra) => p.HasExactKeys(["protocol", "version", "kind", "session_id", .. extra]);
        bool Key(string name) => p.TryGetString(name, out spki) && CanonicalKey(spki);
        bool Nonce(string name) => p.TryGetString(name, out nonce) && V2Format.IsNonce22(nonce);
        PairingKind type;
        switch (kind)
        {
            case "pair.request": if (!Fields("initiator_spki") || !Key("initiator_spki")) return false; type = PairingKind.Request; break;
            case "pair.commit": if (!Fields("commitment") || !p.TryGetString("commitment", out commitment) || !TryDecodeHash(commitment, out _)) return false; type = PairingKind.Commitment; break;
            case "pair.nonce": if (!Fields("na") || !Nonce("na")) return false; type = PairingKind.Nonce; break;
            case "pair.reveal": if (!Fields("responder_spki", "nb") || !Key("responder_spki") || !Nonce("nb")) return false; type = PairingKind.Reveal; break;
            case "pair.confirm": if (!Fields("sig") || !p.TryGetString("sig", out signature) || !V2Format.TryDecodeBase64(signature, out var bytes) || bytes.Length != 64) return false; type = PairingKind.Confirmation; break;
            case "pair.pending": if (!Fields()) return false; type = PairingKind.Pending; break;
            case "pair.cancel": if (!Fields("reason") || !p.TryGetString("reason", out reason) || reason is not ("user_rejected" or "timeout" or "error" or "superseded")) return false; type = PairingKind.Cancel; break;
            default: return false;
        }
        packet = new(type, session, spki, nonce, commitment, signature, reason); return true;
    }
    public static bool TryDecodeHash(string? text, out byte[] bytes)
    {
        bytes = [];
        if (!V2Format.IsPeerId(text)) return false;
        try
        {
            var decoded = Convert.FromBase64String(text!.Replace('-', '+').Replace('_', '/') + "=");
            if (decoded.Length != 32 || V2Format.ToBase64Url(decoded) != text) return false;
            bytes = decoded; return true;
        }
        catch (FormatException) { return false; }
    }
    private static bool CanonicalKey(string text)
    {
        if (!V2Format.TryDecodeBase64(text, out var bytes)) return false;
        using var key = V2Crypto.OpenCanonicalSigningKey(bytes); return key is not null;
    }
    public static string NewNonce() => V2Format.ToBase64Url(RandomNumberGenerator.GetBytes(16));
    public static byte[] Request(string session, string initiatorSpkiBase64) => Encode("pair.request", session, ("initiator_spki", initiatorSpkiBase64));
    public static byte[] Commitment(string session, ReadOnlySpan<byte> commitment) => Encode("pair.commit", session, ("commitment", V2Format.ToBase64Url(commitment)));
    public static byte[] Nonce(string session, string na) => Encode("pair.nonce", session, ("na", na));
    public static byte[] Reveal(string session, string responderSpkiBase64, string nb) => Encode("pair.reveal", session, ("responder_spki", responderSpkiBase64), ("nb", nb));
    /// <summary>The caller owns/serializes the signing key. Create once after human confirmation and reuse the exact bytes on retries.</summary>
    public static byte[] Confirmation(string session, PairingRole role, ReadOnlySpan<byte> transcript, ECDsa signingKey) =>
        ConfirmationSignature(session, Convert.ToBase64String(V2Crypto.Sign(signingKey, PairingCrypto.ConfirmationInput(role, transcript))));
    public static byte[] ConfirmationSignature(string session, string signatureBase64) => Encode("pair.confirm", session, ("sig", signatureBase64));
    public static byte[] Pending(string session) => Encode("pair.pending", session);
    public static byte[] Cancel(string session, string reason) => Encode("pair.cancel", session, ("reason", reason));
    private static byte[] Encode(string kind, string session, params (string Key, string Value)[] fields)
    {
        if (!V2Format.IsNonce22(session) || fields.Any(field => field.Value is null || field.Value.Length > MaxBodyBytes)) throw new ArgumentException("Invalid pairing packet fields.");
        var packet = new Dictionary<string, object>(StringComparer.Ordinal) { ["protocol"] = "nekochat", ["version"] = 2, ["kind"] = kind, ["session_id"] = session };
        foreach (var field in fields) packet.Add(field.Key, field.Value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (!TryParse(bytes, out _)) throw new ArgumentException("Invalid pairing packet fields.");
        return bytes;
    }
}
