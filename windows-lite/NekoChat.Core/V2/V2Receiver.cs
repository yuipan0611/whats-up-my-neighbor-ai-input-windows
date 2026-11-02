using System.Security.Cryptography;
using System.Text.Json;
using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

public sealed record V2DeliveryNotice(string SenderId, long LocalSeq);
public sealed record V2HttpResponse(int StatusCode, byte[] Body, V2DeliveryNotice? Notice = null)
{
    public static V2HttpResponse Error(string code) => new(code switch {
        "payload_too_large" => 413, "unpaired_sender" or "key_mismatch" or "bad_signature" => 403,
        "stale_nonce" or "wrong_recipient" or "conflict" => 409, "rate_limited" => 429,
        "storage_unavailable" or "receiver_not_ready" => 503, _ => 400 },
        JsonSerializer.SerializeToUtf8Bytes(new { protocol = "nekochat", version = 2, kind = "error", code }));
}

/// <summary>Borrowed store/key owner. The host must stop and drain its listener before disposing either dependency.</summary>
public sealed class V2Receiver
{
    private readonly IChatStore _store;
    private readonly V2PacketWriter _writer;
    private readonly V2Verifier _verifier;
    private readonly V2NonceStore _nonces;
    private long _generation;
    private int _ready = 1;
    public int MaxMessageBodyBytes => _verifier.MessageLimits.MaxBytes;
    public V2Receiver(IChatStore store, V2PacketWriter writer, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(writer);
        _store = store; _writer = writer; _verifier = new(writer.MaxTextBytes); _nonces = new(clock);
    }
    public void SetReady(bool ready)
    {
        Volatile.Write(ref _ready, 0); Interlocked.Increment(ref _generation); _nonces.Clear();
        if (ready) Volatile.Write(ref _ready, 1);
    }
    public V2HttpResponse Handle(string path, ReadOnlySpan<byte> body, string sourceEndpoint, Func<bool> connectionCurrent)
    {
        ArgumentNullException.ThrowIfNull(connectionCurrent);
        long generation = Volatile.Read(ref _generation);
        bool Live() => Volatile.Read(ref _ready) == 1 && Volatile.Read(ref _generation) == generation && connectionCurrent();
        if (!Live()) return V2HttpResponse.Error("receiver_not_ready");
        try
        {
            if (path == "/nekochat/v2/hello")
            {
                var request = _verifier.VerifyHelloRequest(body);
                if (!request.Success) return V2HttpResponse.Error(request.Code);
                var context = _store.GetPeerVerificationContext(request.Value!.SenderId);
                if (!OwnIdentity(context) || !Live()) return V2HttpResponse.Error("receiver_not_ready");
                string nonce = _nonces.Issue(context.Security);
                byte[] reply = _writer.HelloReply(request.Value.ClientNonce, nonce);
                return Live() ? new(200, reply) : V2HttpResponse.Error("receiver_not_ready");
            }
            if (path is not ("/nekochat/v2/message" or "/nekochat/v2/receipt")) return V2HttpResponse.Error("unsupported_version");
            var limits = path.EndsWith("/message", StringComparison.Ordinal) ? _verifier.MessageLimits : V2JsonLimits.Control;
            if (!V2Json.TryParseObject(body, limits, out var packet, out var error))
                return V2HttpResponse.Error(error == V2JsonError.TooLarge ? "payload_too_large" : "invalid_payload");
            // A malformed sender must still pass the verifier's structural/error-priority checks. No peer is created by this lookup.
            string lookup = packet!.TryGetString("sender_id", out var sender) && V2Format.IsPeerId(sender) ? sender : "invalid-v2-sender";
            var captured = _store.GetPeerVerificationContext(lookup);
            if (!OwnIdentity(captured) || !Live()) return V2HttpResponse.Error("receiver_not_ready");
            var incoming = new V2IncomingContext(_writer.PeerId, new(captured.Security.Pairing is { } state ? PairingStates.ToWire(state) : null, captured.PeerSpkiBase64));
            if (path.EndsWith("/message", StringComparison.Ordinal))
            {
                var verified = _verifier.VerifyMessage(body, incoming);
                if (!verified.Success) return V2HttpResponse.Error(verified.Code);
                var message = verified.Value!;
                if (!Live()) return V2HttpResponse.Error("receiver_not_ready");
                if (!_nonces.TryConsume(message, captured.Security)) return V2HttpResponse.Error("stale_nonce");
                var result = _store.ReceiveV2Message(message, captured.Security, Live, sourceEndpoint);
                if (!Live()) return V2HttpResponse.Error("receiver_not_ready");
                return result switch {
                    Stored stored => new(200, _writer.MessageAck("stored", message.MessageId, message.SenderId, message.ContentFingerprint), new(message.SenderId, stored.LocalSeq)),
                    Duplicate or ClearedDuplicate => new(200, _writer.MessageAck("duplicate", message.MessageId, message.SenderId, message.ContentFingerprint)),
                    Conflict => V2HttpResponse.Error("conflict"), WrongRecipient => V2HttpResponse.Error("wrong_recipient"),
                    UntrustedPeer => V2HttpResponse.Error("unpaired_sender"), InvalidPayload => V2HttpResponse.Error("invalid_payload"),
                    _ => V2HttpResponse.Error("receiver_not_ready") };
            }
            else
            {
                var verified = _verifier.VerifyReceipt(body, incoming);
                if (!verified.Success) return V2HttpResponse.Error(verified.Code);
                var receipt = verified.Value!;
                if (!Live()) return V2HttpResponse.Error("receiver_not_ready");
                if (!_nonces.TryConsume(receipt, captured.Security)) return V2HttpResponse.Error("stale_nonce");
                var result = _store.ApplyV2Receipt(receipt, captured.Security, Live);
                if (!Live()) return V2HttpResponse.Error("receiver_not_ready");
                if (result.Reason == "pending-local-confirmation") return V2HttpResponse.Error("storage_unavailable");
                if (result.Reason == "stale-security") return V2HttpResponse.Error("receiver_not_ready");
                if (result.Reason == "untrusted-peer") return V2HttpResponse.Error("unpaired_sender");
                string status = result.Changed ? "ok" : result.Reason == "unchanged" ? "duplicate" : "discarded";
                return new(200, _writer.ReceiptAck(status, receipt.MessageId, receipt.SenderId));
            }
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException) { return V2HttpResponse.Error("storage_unavailable"); }
        catch (Exception e) when (e is ObjectDisposedException or CryptographicException) { return V2HttpResponse.Error("receiver_not_ready"); }
    }
    private bool OwnIdentity(PeerVerificationContext captured) => captured.Security.LocalIdentityId == _writer.PeerId && captured.LocalSpkiBase64 == _writer.PublicSpkiBase64;
}
