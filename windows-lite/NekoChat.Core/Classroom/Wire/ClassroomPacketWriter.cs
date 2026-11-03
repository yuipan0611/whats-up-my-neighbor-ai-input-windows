using System.Security.Cryptography;
using System.Text.Json;
using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Wire;

/// <summary>
/// Builds classroom wire bodies. Every field is validated with the same rules the receiver applies, so a packet this class returns always passes
/// <see cref="ClassroomRequestValidator"/> / <see cref="ClassroomResponseValidator"/> shape checks; invalid input throws <see cref="ArgumentException"/>.
/// There is no key, no signature and no sender-identity field (contract §0, §2.3).
/// </summary>
public static class ClassroomPacketWriter
{
    public static string NewToken() => V2Format.ToBase64Url(RandomNumberGenerator.GetBytes(16));
    public static string NewMessageId() => Guid.NewGuid().ToString("D");

    private static Dictionary<string, object?> Envelope(string kind) => new(StringComparer.Ordinal) { ["protocol"] = ClassroomWire.Protocol, ["version"] = ClassroomWire.Version, ["kind"] = kind };
    private static void Require(bool valid) { if (!valid) throw new ArgumentException("Invalid classroom packet field."); }
    private static byte[] Bytes(Dictionary<string, object?> packet) => JsonSerializer.SerializeToUtf8Bytes(packet);

    public static byte[] HelloRequest(string clientNonce)
    {
        Require(V2Format.IsNonce22(clientNonce));
        var p = Envelope("hello"); p["client_nonce"] = clientNonce; return Bytes(p);
    }

    public static byte[] HelloOk(string clientNonce, string session, string receiverIp, string observedSenderIp, IReadOnlyList<string> capabilities)
    {
        Require(V2Format.IsNonce22(clientNonce) && V2Format.IsNonce22(session) && ClassroomWire.IsIPv4(receiverIp) && ClassroomWire.IsIPv4(observedSenderIp) &&
            capabilities.Count <= ClassroomWire.MaxCapabilities && capabilities.All(V2Format.IsCapability) && capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Count);
        var p = Envelope("hello.ok"); p["client_nonce"] = clientNonce; p["session"] = session; p["receiver_ip"] = receiverIp; p["observed_sender_ip"] = observedSenderIp;
        p["capabilities"] = capabilities.ToArray(); p["max_text_bytes"] = ClassroomWire.MaxTextBytes; return Bytes(p);
    }

    public static byte[] MessageSend(string messageId, string session, string toIp, string text, string? reaction = null)
    {
        Require(V2Format.IsUuid(messageId) && V2Format.IsNonce22(session) && ClassroomWire.IsIPv4(toIp) && (reaction is null || V2Format.IsReactionId(reaction)) &&
            V2Text.Check(text, ClassroomWire.MaxTextBytes) == V2TextProblem.None);
        var p = Envelope("message.send"); p["message_id"] = messageId; p["session"] = session; p["to_ip"] = toIp; p["text"] = text; p["reaction"] = reaction; return Bytes(p);
    }

    /// <param name="status">stored | duplicate. The hash is computed here from the stored content, never passed in.</param>
    public static byte[] MessageAck(string status, string messageId, string session, string observedSenderIp, string receiverIp, string? reaction, string text)
    {
        Require(status is "stored" or "duplicate" && V2Format.IsUuid(messageId) && V2Format.IsNonce22(session) && ClassroomWire.IsIPv4(observedSenderIp) && ClassroomWire.IsIPv4(receiverIp) &&
            (reaction is null || V2Format.IsReactionId(reaction)));
        var p = Envelope("message.ack"); p["status"] = status; p["message_id"] = messageId; p["session"] = session; p["observed_sender_ip"] = observedSenderIp;
        p["receiver_ip"] = receiverIp; p["content_sha256"] = ClassroomWire.ContentSha256(messageId, reaction, text); return Bytes(p);
    }

    public static byte[] ReceiptRequest(string messageId, string session, string toIp)
    {
        Require(V2Format.IsUuid(messageId) && V2Format.IsNonce22(session) && ClassroomWire.IsIPv4(toIp));
        var p = Envelope("receipt"); p["message_id"] = messageId; p["session"] = session; p["to_ip"] = toIp; p["receipt_kind"] = "read"; return Bytes(p);
    }

    public static byte[] ReceiptAck(string status, string messageId, string session, string observedSenderIp, string receiverIp)
    {
        Require(status is "ok" or "duplicate" or "discarded" && V2Format.IsUuid(messageId) && V2Format.IsNonce22(session) && ClassroomWire.IsIPv4(observedSenderIp) && ClassroomWire.IsIPv4(receiverIp));
        var p = Envelope("receipt.ack"); p["status"] = status; p["message_id"] = messageId; p["session"] = session; p["observed_sender_ip"] = observedSenderIp; p["receiver_ip"] = receiverIp; return Bytes(p);
    }

    /// <summary>Error body and its HTTP status (contract §10). The body carries the code and nothing else.</summary>
    public static (int Status, byte[] Body) Error(string code)
    {
        Require(ClassroomWire.TryErrorStatus(code, out _));
        ClassroomWire.TryErrorStatus(code, out int status);
        var p = Envelope("error"); p["code"] = code; return (status, Bytes(p));
    }
}
