using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Wire;

public enum ClassroomResponseKind { HelloOk, MessageAck, ReceiptAck }

/// <summary>What the sender knows about the request it made; every echo in the response is compared with this, never trusted.</summary>
public sealed record ClassroomSentRequest(string ClientNonce, string Session, string MessageId, string? Reaction, string Text);
public sealed record ClassroomResponseContext(string TargetIp, string OwnIp, ClassroomSentRequest Sent, string? RequiredCapability);

public sealed record ClassroomHelloInfo(string Session, string ReceiverIp, string ObservedSenderIp, IReadOnlyList<string> Capabilities);

/// <summary>
/// <see cref="Code"/>: <c>ok</c>, <c>invalid_response</c>, <c>ack_mismatch</c>, <c>capability_missing</c>, or <c>remote_error</c> (then <see cref="RemoteCode"/> is the
/// peer's declared error). <see cref="Status"/> is the ACK status (stored/duplicate or ok/duplicate/discarded) when Code is ok.
/// </summary>
public sealed record ClassroomResponseResult(string Code, string? RemoteCode = null, string? Status = null, ClassroomHelloInfo? Hello = null)
{
    public bool Ok => Code == ClassroomCodes.Ok;
    public string Label => RemoteCode is null ? Code : "remote:" + RemoteCode;
}

/// <summary>Sender-side validation of hello.ok / message.ack / receipt.ack / error (contract §5, §8, §9, §10).</summary>
public static class ClassroomResponseValidator
{
    private static readonly string[] HelloKeys = ["protocol", "version", "kind", "client_nonce", "session", "receiver_ip", "observed_sender_ip", "capabilities", "max_text_bytes"];
    private static readonly string[] MessageKeys = ["protocol", "version", "kind", "status", "message_id", "session", "observed_sender_ip", "receiver_ip", "content_sha256"];
    private static readonly string[] ReceiptKeys = ["protocol", "version", "kind", "status", "message_id", "session", "observed_sender_ip", "receiver_ip"];
    private static readonly string[] ErrorKeys = ["protocol", "version", "kind", "code"];

    public static string KindText(ClassroomResponseKind kind) => kind switch { ClassroomResponseKind.HelloOk => "hello.ok", ClassroomResponseKind.MessageAck => "message.ack", _ => "receipt.ack" };

    public static ClassroomResponseResult Validate(ClassroomResponseKind kind, int httpStatus, ReadOnlySpan<byte> body, ClassroomResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        static ClassroomResponseResult Invalid() => new(ClassroomCodes.InvalidResponse);
        if (!V2Json.TryParseObject(body, ClassroomWire.JsonLimits(ClassroomWire.ControlBodyMaxBytes), out var o, out _)) return Invalid();
        if (!o!.TryGetString("protocol", out var protocol) || protocol != ClassroomWire.Protocol || !o.TryGetNumberToken("version", out var version) || version != "1") return Invalid();
        if (!o.TryGetString("kind", out var kindText)) return Invalid();
        if (kindText == "error")
        {
            if (!o.HasExactKeys(ErrorKeys) || !o.TryGetString("code", out var code) || !ClassroomWire.TryErrorStatus(code, out int expected) || expected != httpStatus) return Invalid();
            return new(ClassroomCodes.RemoteError, code);
        }
        if (httpStatus != 200 || kindText != KindText(kind)) return Invalid();
        if (!o.HasExactKeys(kind switch { ClassroomResponseKind.HelloOk => HelloKeys, ClassroomResponseKind.MessageAck => MessageKeys, _ => ReceiptKeys })) return Invalid();
        if (!o.TryGetString("observed_sender_ip", out var observed) || !ClassroomWire.IsIPv4(observed) || !o.TryGetString("receiver_ip", out var receiver) || !ClassroomWire.IsIPv4(receiver) ||
            !o.TryGetString("session", out var session) || !V2Format.IsNonce22(session)) return Invalid();
        var sent = context.Sent;

        if (kind == ClassroomResponseKind.HelloOk)
        {
            if (!o.TryGetString("client_nonce", out var nonce) || !V2Format.IsNonce22(nonce) || !o.TryGetStringArray("capabilities", out var caps) || caps.Length > ClassroomWire.MaxCapabilities ||
                caps.Any(c => !V2Format.IsCapability(c)) || caps.Distinct(StringComparer.Ordinal).Count() != caps.Length ||
                !o.TryGetNumberToken("max_text_bytes", out var max) || max != ClassroomWire.MaxTextBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)) return Invalid();
            if (nonce != sent.ClientNonce || receiver != context.TargetIp || observed != context.OwnIp) return new(ClassroomCodes.AckMismatch);
            if (context.RequiredCapability is { } need && !caps.Contains(need, StringComparer.Ordinal)) return new(ClassroomCodes.CapabilityMissing);
            return new(ClassroomCodes.Ok, Hello: new(session, receiver, observed, Array.AsReadOnly(caps)));
        }

        if (!o.TryGetString("status", out var status) || !o.TryGetString("message_id", out var id) || !V2Format.IsUuid(id)) return Invalid();
        bool statusOk = kind == ClassroomResponseKind.MessageAck ? status is "stored" or "duplicate" : status is "ok" or "duplicate" or "discarded";
        if (!statusOk) return Invalid();
        string? hash = null;
        if (kind == ClassroomResponseKind.MessageAck && (!o.TryGetString("content_sha256", out hash) || !V2Format.IsHex64(hash))) return Invalid();
        if (id != sent.MessageId || session != sent.Session || receiver != context.TargetIp || observed != context.OwnIp) return new(ClassroomCodes.AckMismatch);
        if (kind == ClassroomResponseKind.MessageAck && hash != ClassroomWire.ContentSha256(sent.MessageId, sent.Reaction, sent.Text)) return new(ClassroomCodes.AckMismatch);
        return new(ClassroomCodes.Ok, Status: status);
    }
}
