using System.Security.Cryptography;
using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

/// <summary>Canonical domain-separated signing bytes. Validate fields before accepting a packet or signing application data.</summary>
public static class V2Signing
{
    public static byte[] Hello(string receiver, string clientNonce, string nonce, IEnumerable<string> capabilities, string maxTextToken, string? keyExchange) =>
        V2Format.Utf8("nekochat-v2\nhello.ok\n" + receiver + "\n" + clientNonce + "\n" + nonce + "\n" + string.Join(',', capabilities) + "\n" + maxTextToken + "\n" + (keyExchange ?? ""));
    public static byte[] Message(string id, string sender, string recipient, string nonce, string text, string? reaction) =>
        V2Format.Utf8("nekochat-v2\nmessage.send\n" + id + "\n" + sender + "\n" + recipient + "\n" + nonce + "\n" + V2Format.ToLowerHex(SHA256.HashData(V2Format.Utf8(text))) + "\n" + (reaction ?? ""));
    public static byte[] Receipt(string id, string sender, string recipient, string nonce, string state) =>
        V2Format.Utf8("nekochat-v2\nreceipt\n" + id + "\n" + sender + "\n" + recipient + "\n" + nonce + "\n" + state);
    public static byte[] MessageAck(string status, string id, string sender, string receiver, string fingerprint) =>
        V2Format.Utf8("nekochat-v2\nmessage.ack\n" + status + "\n" + id + "\n" + sender + "\n" + receiver + "\n" + fingerprint);
    public static byte[] ReceiptAck(string status, string id, string sender, string receiver) =>
        V2Format.Utf8("nekochat-v2\nreceipt.ack\n" + status + "\n" + id + "\n" + sender + "\n" + receiver);
    public static string Fingerprint(string recipient, string text, string? reaction = null)
    {
        if (!V2Format.IsPeerId(recipient)) throw new ArgumentException("Invalid recipient.", nameof(recipient));
        return V2Format.ToLowerHex(MessageFingerprint.Compute(recipient, text, reaction));
    }
}
