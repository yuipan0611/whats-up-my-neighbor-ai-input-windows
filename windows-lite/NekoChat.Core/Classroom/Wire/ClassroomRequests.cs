using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Wire;

public enum ClassroomRequestKind { Hello, Message, Receipt }

/// <summary>A request that passed every format check (not yet the session / recipient checks). Only the fields of its kind are set.</summary>
public sealed record ClassroomRequest(ClassroomRequestKind Kind, string? ClientNonce = null, string? MessageId = null, string? Session = null,
    string? ToIp = null, string? Text = null, string? Reaction = null);

public readonly record struct ClassroomSessionState(string SourceIp, TimeSpan Age);
public interface IClassroomSessionLookup { bool TryGet(string session, out ClassroomSessionState state); }

/// <summary><see cref="Code"/> is a wire error code or <c>ok</c>. On <c>ok</c> the caller must now consume the session atomically and only then touch storage (contract §7.10).</summary>
public sealed record ClassroomRequestResult(string Code, ClassroomRequest? Request)
{
    public bool Ok => Code == ClassroomCodes.Ok;
}

/// <summary>Receiver-side validation. Evaluation order is contract §7 and is pinned by the NCMW-v2 vectors.</summary>
public static class ClassroomRequestValidator
{
    private static readonly string[] HelloKeys = ["protocol", "version", "kind", "client_nonce"];
    private static readonly string[] MessageKeys = ["protocol", "version", "kind", "message_id", "session", "to_ip", "text", "reaction"];
    private static readonly string[] ReceiptKeys = ["protocol", "version", "kind", "message_id", "session", "to_ip", "receipt_kind"];

    public static string KindText(ClassroomRequestKind kind) => kind switch { ClassroomRequestKind.Hello => "hello", ClassroomRequestKind.Message => "message.send", _ => "receipt" };

    /// <param name="localIp">The accepted socket's local address (canonical IPv4).</param>
    /// <param name="sourceIp">The accepted socket's remote address after <see cref="ClassroomWire.CanonicalSource(string?)"/>. Never taken from the body.</param>
    public static ClassroomRequestResult Validate(ClassroomRequestKind kind, ReadOnlySpan<byte> body, string localIp, string sourceIp, IClassroomSessionLookup? sessions)
    {
        ArgumentNullException.ThrowIfNull(localIp); ArgumentNullException.ThrowIfNull(sourceIp);
        static ClassroomRequestResult Fail(string code) => new(code, null);
        int limit = kind == ClassroomRequestKind.Message ? ClassroomWire.MessageBodyMaxBytes : ClassroomWire.ControlBodyMaxBytes;
        if (body.Length > limit) return Fail(ClassroomCodes.PayloadTooLarge);
        if (!V2Json.TryParseObject(body, ClassroomWire.JsonLimits(limit), out var o, out var error)) return Fail(error == V2JsonError.TooLarge ? ClassroomCodes.PayloadTooLarge : ClassroomCodes.InvalidPayload);
        // protocol / version: wrong JSON type is malformed; right type with another value is another protocol (signed V2 lands here).
        if (!o!.TryGetString("protocol", out var protocol) || !o.TryGetNumberToken("version", out var version) || !IsIntegerToken(version)) return Fail(ClassroomCodes.InvalidPayload);
        if (protocol != ClassroomWire.Protocol || version != "1") return Fail(ClassroomCodes.UnsupportedVersion);
        if (!o.TryGetString("kind", out var kindText) || kindText != KindText(kind)) return Fail(ClassroomCodes.InvalidPayload);
        if (!o.HasExactKeys(kind switch { ClassroomRequestKind.Hello => HelloKeys, ClassroomRequestKind.Message => MessageKeys, _ => ReceiptKeys })) return Fail(ClassroomCodes.InvalidPayload);
        if (kind == ClassroomRequestKind.Hello)
            return o.TryGetString("client_nonce", out var nonce) && V2Format.IsNonce22(nonce) ? new(ClassroomCodes.Ok, new(kind, ClientNonce: nonce)) : Fail(ClassroomCodes.InvalidPayload);

        if (!o.TryGetString("message_id", out var id) || !V2Format.IsUuid(id) || !o.TryGetString("session", out var session) || !V2Format.IsNonce22(session) ||
            !o.TryGetString("to_ip", out var toIp) || !ClassroomWire.IsIPv4(toIp)) return Fail(ClassroomCodes.InvalidPayload);
        string? text = null, reaction = null;
        if (kind == ClassroomRequestKind.Message)
        {
            if (o.IsNull("reaction")) reaction = null;
            else if (!o.TryGetString("reaction", out reaction) || !V2Format.IsReactionId(reaction)) return Fail(ClassroomCodes.InvalidPayload);
            if (!o.TryGetString("text", out text)) return Fail(ClassroomCodes.InvalidPayload);
            switch (V2Text.Check(text, ClassroomWire.MaxTextBytes))
            {
                case V2TextProblem.None: break;
                case V2TextProblem.TooLarge: return Fail(ClassroomCodes.PayloadTooLarge);
                default: return Fail(ClassroomCodes.InvalidPayload);
            }
        }
        else if (!o.TryGetString("receipt_kind", out var receiptKind) || receiptKind != "read") return Fail(ClassroomCodes.InvalidPayload);

        if (!string.Equals(toIp, localIp, StringComparison.Ordinal)) return Fail(ClassroomCodes.WrongRecipient);
        if (sessions is null || !sessions.TryGet(session, out var state) || !string.Equals(state.SourceIp, sourceIp, StringComparison.Ordinal) ||
            state.Age < TimeSpan.Zero || state.Age >= ClassroomWire.SessionTtl) return Fail(ClassroomCodes.StaleSession);
        return new(ClassroomCodes.Ok, new(kind, MessageId: id, Session: session, ToIp: toIp, Text: text, Reaction: reaction));
    }

    /// <summary>JSON integer literal: optional '-' then digits; no fraction, no exponent (the parser already guarantees valid number syntax).</summary>
    internal static bool IsIntegerToken(string token) => token.Length > 0 && token.All(c => c is '-' or (>= '0' and <= '9'));
}
