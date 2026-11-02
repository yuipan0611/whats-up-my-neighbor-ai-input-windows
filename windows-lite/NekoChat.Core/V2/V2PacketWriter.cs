using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NekoChat.Core.V2;

/// <summary>Produces signed v2 packets. Takes ownership of the P-256 private key; never exports private material.</summary>
public sealed class V2PacketWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly ECDsa _key;
    private bool _disposed;
    public string PeerId { get; }
    public string PublicSpkiBase64 { get; }
    public int MaxTextBytes { get; }
    public V2PacketWriter(ECDsa privateKey, int maxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        _ = V2JsonLimits.Message(maxTextBytes);
        byte[] spki = privateKey.ExportSubjectPublicKeyInfo();
        using var canonical = V2Crypto.OpenCanonicalSigningKey(spki);
        if (canonical is null) throw new ArgumentException("A named P-256 key is required.", nameof(privateKey));
        // Reject public-only keys at initialization rather than when the receiver is already serving requests.
        byte[] probe = V2Crypto.Sign(privateKey, "nekochat-v2-key-check"u8);
        if (!V2Crypto.Verify(canonical, "nekochat-v2-key-check"u8, probe)) throw new CryptographicException("Signing key check failed.");
        _key = privateKey; MaxTextBytes = maxTextBytes; PeerId = V2Crypto.PeerId(spki); PublicSpkiBase64 = Convert.ToBase64String(spki);
    }
    public static string NewMessageId() => Guid.NewGuid().ToString("D");
    public static string NewClientNonce() => V2Format.ToBase64Url(RandomNumberGenerator.GetBytes(16));
    private static Dictionary<string, object?> Envelope(string kind) => new(StringComparer.Ordinal)
    { ["protocol"] = "nekochat", ["version"] = 2, ["kind"] = kind };
    private static void Require(bool valid) { if (!valid) throw new ArgumentException("Invalid v2 packet fields."); }
    private byte[] Signed(Dictionary<string, object?> packet, string signatureField, byte[] input)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            packet[signatureField] = Convert.ToBase64String(V2Crypto.Sign(_key, input));
            return JsonSerializer.SerializeToUtf8Bytes(packet);
        }
    }
    public byte[] HelloRequest(string clientNonce)
    {
        Require(V2Format.IsNonce22(clientNonce));
        var p = Envelope("hello"); p["sender_id"] = PeerId; p["client_nonce"] = clientNonce;
        return JsonSerializer.SerializeToUtf8Bytes(p);
    }
    public byte[] HelloReply(string clientNonce, string nonce)
    {
        Require(V2Format.IsNonce22(clientNonce) && V2Format.IsNonce22(nonce));
        string[] caps = ["msg.sign", "receipt.read"];
        var p = Envelope("hello.ok"); p["receiver_id"] = PeerId; p["receiver_spki"] = PublicSpkiBase64;
        p["nonce"] = nonce; p["capabilities"] = caps; p["max_text_bytes"] = MaxTextBytes; p["cert"] = null; p["kex_spki"] = null;
        return Signed(p, "proof_sig", V2Signing.Hello(PeerId, clientNonce, nonce, caps, MaxTextBytes.ToString(CultureInfo.InvariantCulture), null));
    }
    public byte[] Message(string id, string recipient, string nonce, string text, string? reaction = null)
    {
        Require(V2Format.IsUuid(id) && V2Format.IsPeerId(recipient) && V2Format.IsNonce22(nonce) &&
            (reaction is null || V2Format.IsReactionId(reaction)) && V2Text.Check(text, MaxTextBytes) == V2TextProblem.None);
        var p = Envelope("message.send"); p["message_id"] = id; p["sender_id"] = PeerId; p["recipient_id"] = recipient;
        p["nonce"] = nonce; p["text"] = text; p["reaction"] = reaction; p["sender_spki"] = PublicSpkiBase64; p["cert"] = null;
        return Signed(p, "sig", V2Signing.Message(id, PeerId, recipient, nonce, text, reaction));
    }
    public byte[] Receipt(string id, string recipient, string nonce)
    {
        Require(V2Format.IsUuid(id) && V2Format.IsPeerId(recipient) && V2Format.IsNonce22(nonce));
        var p = Envelope("receipt"); p["message_id"] = id; p["sender_id"] = PeerId; p["recipient_id"] = recipient; p["nonce"] = nonce; p["state"] = "read";
        return Signed(p, "sig", V2Signing.Receipt(id, PeerId, recipient, nonce, "read"));
    }
    public byte[] MessageAck(string status, string id, string originalSender, string fingerprint)
    {
        Require(status is "stored" or "duplicate" && V2Format.IsUuid(id) && V2Format.IsPeerId(originalSender) && V2Format.IsHex64(fingerprint));
        var p = Envelope("message.ack"); p["status"] = status; p["message_id"] = id; p["sender_id"] = originalSender; p["receiver_id"] = PeerId; p["content_fingerprint"] = fingerprint;
        return Signed(p, "ack_sig", V2Signing.MessageAck(status, id, originalSender, PeerId, fingerprint));
    }
    public byte[] ReceiptAck(string status, string id, string originalSender)
    {
        Require(status is "ok" or "duplicate" or "discarded" && V2Format.IsUuid(id) && V2Format.IsPeerId(originalSender));
        var p = Envelope("receipt.ack"); p["status"] = status; p["message_id"] = id; p["sender_id"] = originalSender; p["receiver_id"] = PeerId;
        return Signed(p, "ack_sig", V2Signing.ReceiptAck(status, id, originalSender, PeerId));
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _key.Dispose(); } }
}
