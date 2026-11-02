using System.Globalization;

namespace NekoChat.Core.V2;

/// <summary>Real product verifier for N002A-v2.3. No signature, HTTP200 or parse-only success is promoted to a verified domain object.</summary>
public sealed class V2Verifier
{
    public int MaxTextBytes { get; }
    public V2JsonLimits MessageLimits { get; }
    public V2Verifier(int maxTextBytes) { MessageLimits = V2JsonLimits.Message(maxTextBytes); MaxTextBytes = maxTextBytes; }
    private static bool Parse(ReadOnlySpan<byte> bytes, V2JsonLimits limits, out V2JsonObject packet, out V2JsonError error)
    { bool ok = V2Json.TryParseObject(bytes, limits, out var value, out error); packet = value!; return ok; }
    private static bool Envelope(V2JsonObject p, string kind) => p.TryGetString("protocol", out var protocol) && protocol == "nekochat" &&
        p.TryGetNumberToken("version", out var version) && version == "2" && p.TryGetString("kind", out var value) && value == kind;
    private static string S(V2JsonObject p, string key) => p.TryGetString(key, out var value) ? value : "";
    private static bool NullableString(V2JsonObject p, string key, out string? value)
    { value = null; if (p.IsNull(key)) return true; if (!p.TryGetString(key, out var text)) return false; value = text; return true; }
    private static bool Signature(V2JsonObject p, string name, out byte[] signature) => V2Format.TryDecodeBase64(S(p, name), out signature) && signature.Length == 64;
    private static bool Pin(V2Peer peer, ReadOnlySpan<byte> expected) => V2Format.TryDecodeBase64(peer.PublicSpkiBase64, out var pin) && pin.AsSpan().SequenceEqual(expected);

    public V2Verification<V2HelloRequest> VerifyHelloRequest(ReadOnlySpan<byte> bytes)
    {
        if (!Parse(bytes, V2JsonLimits.Control, out var p, out _) || !p.HasExactKeys("protocol", "version", "kind", "sender_id", "client_nonce")) return new("invalid_payload");
        if (!Envelope(p, "hello")) return new("unsupported_version");
        string sender = S(p, "sender_id"), nonce = S(p, "client_nonce");
        return V2Format.IsPeerId(sender) && V2Format.IsNonce22(nonce) ? new("ok", new(sender, nonce)) : new("invalid_payload");
    }

    public V2Verification<V2HelloInfo> VerifyHello(ReadOnlySpan<byte> bytes, V2HelloContext context)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Peer); ArgumentNullException.ThrowIfNull(context.RequiredCapabilities);
        var required = context.RequiredCapabilities.ToArray();
        if (!Parse(bytes, V2JsonLimits.Control, out var p, out _) || !p.HasExactKeys("protocol", "version", "kind", "receiver_id", "receiver_spki", "nonce", "capabilities", "max_text_bytes", "cert", "kex_spki", "proof_sig") ||
            !Envelope(p, "hello.ok") || !p.IsNull("cert")) return new("hello_proof_invalid");
        string id = S(p, "receiver_id"), nonce = S(p, "nonce"), spkiText = S(p, "receiver_spki");
        if (!V2Format.IsPeerId(id) || !V2Format.IsNonce22(nonce) || !V2Format.IsNonce22(context.ClientNonce) ||
            !p.TryGetNumberToken("max_text_bytes", out var rawMax) || !V2Format.IsPositiveIntLiteral(rawMax) ||
            !p.TryGetStringArray("capabilities", out var caps) || caps.Length > 16 || caps.Any(c => !V2Format.IsCapability(c)) || caps.Distinct(StringComparer.Ordinal).Count() != caps.Length ||
            !NullableString(p, "kex_spki", out var kex) || (kex is not null) != caps.Contains("msg.aead", StringComparer.Ordinal) ||
            !V2Format.TryDecodeBase64(spkiText, out var spki) || !Signature(p, "proof_sig", out var signature)) return new("hello_proof_invalid");
        if (kex is not null && (!V2Format.TryDecodeBase64(kex, out var kb) || !V2Crypto.IsCanonicalKeyExchange(kb))) return new("hello_proof_invalid");
        using var key = V2Crypto.OpenCanonicalSigningKey(spki);
        if (key is null || V2Crypto.PeerId(spki) != id) return new("hello_proof_invalid");
        bool paired = context.Peer.State == "paired";
        if (paired && !Pin(context.Peer, spki)) return new("hello_key_mismatch");
        if (!V2Crypto.Verify(key, V2Signing.Hello(id, context.ClientNonce, nonce, caps, rawMax, kex), signature)) return new("hello_proof_invalid");
        if (required.Any(c => !caps.Contains(c, StringComparer.Ordinal))) return new("hello_downgrade");
        return new("ok", new(id, spkiText, nonce, caps, int.Parse(rawMax, CultureInfo.InvariantCulture), kex, paired));
    }

    public V2Verification<V2VerifiedMessage> VerifyMessage(ReadOnlySpan<byte> bytes, V2IncomingContext context)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Peer);
        if (!Parse(bytes, MessageLimits, out var p, out var parseError)) return new(parseError == V2JsonError.TooLarge ? "payload_too_large" : "invalid_payload");
        if (!p.HasExactKeys("protocol", "version", "kind", "message_id", "sender_id", "recipient_id", "nonce", "text", "reaction", "sender_spki", "cert", "sig")) return new("invalid_payload");
        if (!Envelope(p, "message.send")) return new("unsupported_version");
        string id = S(p, "message_id"), sender = S(p, "sender_id"), recipient = S(p, "recipient_id"), nonce = S(p, "nonce");
        if (!V2Format.IsUuid(id) || !V2Format.IsPeerId(sender) || !V2Format.IsPeerId(recipient) || !V2Format.IsNonce22(nonce) || !p.IsNull("cert") ||
            !p.TryGetString("text", out var text) || !NullableString(p, "reaction", out var reaction) || reaction is not null && !V2Format.IsReactionId(reaction)) return new("invalid_payload");
        var problem = V2Text.Check(text, MaxTextBytes);
        if (problem != V2TextProblem.None) return new(problem == V2TextProblem.TooLarge ? "payload_too_large" : "invalid_payload");
        if (!V2Format.TryDecodeBase64(S(p, "sender_spki"), out var spki) || !Signature(p, "sig", out var signature)) return new("invalid_payload");
        using var key = V2Crypto.OpenCanonicalSigningKey(spki);
        if (key is null) return new("invalid_payload");
        if (V2Crypto.PeerId(spki) != sender) return new("key_mismatch");
        if (context.Peer.State != "paired") return new("unpaired_sender");
        if (!Pin(context.Peer, spki)) return new("key_mismatch");
        if (!V2Crypto.Verify(key, V2Signing.Message(id, sender, recipient, nonce, text, reaction), signature)) return new("bad_signature");
        if (recipient != context.LocalRecipientId) return new("wrong_recipient");
        return new("ok", new(id, sender, recipient, nonce, text, reaction));
    }

    public V2Verification<V2VerifiedReceipt> VerifyReceipt(ReadOnlySpan<byte> bytes, V2IncomingContext context)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Peer);
        if (!Parse(bytes, V2JsonLimits.Control, out var p, out _) || !p.HasExactKeys("protocol", "version", "kind", "message_id", "sender_id", "recipient_id", "nonce", "state", "sig")) return new("invalid_payload");
        if (!Envelope(p, "receipt")) return new("unsupported_version");
        string id = S(p, "message_id"), sender = S(p, "sender_id"), recipient = S(p, "recipient_id"), nonce = S(p, "nonce"), state = S(p, "state");
        if (!V2Format.IsUuid(id) || !V2Format.IsPeerId(sender) || !V2Format.IsPeerId(recipient) || !V2Format.IsNonce22(nonce) || state != "read" || !Signature(p, "sig", out var signature)) return new("invalid_payload");
        if (context.Peer.State != "paired") return new("unpaired_sender");
        if (!V2Format.TryDecodeBase64(context.Peer.PublicSpkiBase64, out var spki)) return new("key_mismatch");
        using var key = V2Crypto.OpenCanonicalSigningKey(spki);
        if (key is null || V2Crypto.PeerId(spki) != sender) return new("key_mismatch");
        if (!V2Crypto.Verify(key, V2Signing.Receipt(id, sender, recipient, nonce, state), signature)) return new("bad_signature");
        if (recipient != context.LocalRecipientId) return new("wrong_recipient");
        return new("ok", new(id, sender, recipient, nonce));
    }

    public V2Verification<V2VerifiedAck> VerifyMessageAck(ReadOnlySpan<byte> bytes, V2AckContext context) => VerifyAck(bytes, context, message: true);
    public V2Verification<V2VerifiedAck> VerifyReceiptAck(ReadOnlySpan<byte> bytes, V2AckContext context) => VerifyAck(bytes, context, message: false);
    private static V2Verification<V2VerifiedAck> VerifyAck(ReadOnlySpan<byte> bytes, V2AckContext context, bool message)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Parse(bytes, V2JsonLimits.Control, out var p, out _)) return new("ack_invalid");
        bool keys = message ? p.HasExactKeys("protocol", "version", "kind", "status", "message_id", "sender_id", "receiver_id", "content_fingerprint", "ack_sig") :
            p.HasExactKeys("protocol", "version", "kind", "status", "message_id", "sender_id", "receiver_id", "ack_sig");
        if (!keys || !Envelope(p, message ? "message.ack" : "receipt.ack")) return new("ack_invalid");
        string status = S(p, "status"), id = S(p, "message_id"), sender = S(p, "sender_id"), receiver = S(p, "receiver_id");
        string? fingerprint = message ? S(p, "content_fingerprint") : null;
        if (!(message ? status is "stored" or "duplicate" : status is "ok" or "duplicate" or "discarded") || !V2Format.IsUuid(id) || !V2Format.IsPeerId(sender) || !V2Format.IsPeerId(receiver) ||
            message && !V2Format.IsHex64(fingerprint) || !Signature(p, "ack_sig", out var signature) || !V2Format.TryDecodeBase64(context.PinnedReceiverSpkiBase64, out var spki)) return new("ack_invalid");
        using var key = V2Crypto.OpenCanonicalSigningKey(spki);
        if (key is null || V2Crypto.PeerId(spki) != receiver) return new("ack_invalid");
        byte[] input = message ? V2Signing.MessageAck(status, id, sender, receiver, fingerprint!) : V2Signing.ReceiptAck(status, id, sender, receiver);
        if (!V2Crypto.Verify(key, input, signature)) return new("ack_bad_signature");
        if (id != context.ExpectedMessageId || sender != context.ExpectedSenderId) return new("ack_invalid");
        if (message && fingerprint != context.ExpectedFingerprint) return new("AckMismatch");
        return new("ok", new(status, id, sender, receiver, fingerprint));
    }
}
