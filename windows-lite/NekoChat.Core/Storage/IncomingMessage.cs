using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Storage;

/// <summary>Trusted transport metadata, never a raw deserialization target. The v2 adapter uses Kind="text".</summary>
public sealed record IncomingMessage(string VerifiedSenderPeerId, string MessageId, string RecipientId,
    string RawText, string Kind = "text", string? Reaction = null, string? SourceEndpoint = null);
public abstract record ReceiveResult;
public sealed record Stored(string MessageId, long LocalSeq, int UnreadCountForConversation) : ReceiveResult;
public sealed record Duplicate(string MessageId, long LocalSeq) : ReceiveResult;
/// <summary>Previously cleared key. No history, unread, notification or presence evidence is recreated.</summary>
public sealed record ClearedDuplicate(string MessageId) : ReceiveResult;
public sealed record Conflict(string MessageId) : ReceiveResult;
public sealed record WrongRecipient : ReceiveResult;
public sealed record UntrustedPeer : ReceiveResult;
public sealed record InvalidPayload(string Reason) : ReceiveResult;
public sealed record PresenceWriteResult(bool Written, string Reason);

public static class MessageFingerprint
{
    public const string Algorithm = "nekochat-v2-content";

    public static byte[] Compute(string recipientId, string rawText, string? reaction = null)
    {
        if (!StoreIds.IsValid(recipientId)) throw new ArgumentException("Invalid recipient id.", nameof(recipientId));
        if (!ValidReaction(reaction)) throw new ArgumentException("Invalid reaction.", nameof(reaction));
        ArgumentNullException.ThrowIfNull(rawText);
        var encoding = new UTF8Encoding(false, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(encoding.GetBytes(Algorithm + "\n" + recipientId + "\n" + (reaction ?? "") + "\n"));
        hash.AppendData(encoding.GetBytes(rawText));
        return hash.GetHashAndReset();
    }

    internal static bool ValidReaction(string? value) => value is null || value.Length is >= 1 and <= 32 &&
        value[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9') &&
        value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
