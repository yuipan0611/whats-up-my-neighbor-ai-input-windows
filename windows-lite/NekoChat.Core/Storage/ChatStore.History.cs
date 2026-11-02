using System.Globalization;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    /// <summary>Materialized ascending page. Opening history never marks messages read.</summary>
    public MessagePage ListMessages(string conversationId, long? beforeLocalSeq = null, int pageSize = 50)
    {
        ValidateConversationId(conversationId);
        if (beforeLocalSeq is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeLocalSeq));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT localSeq,conversationId,direction,senderId,recipientId,messageId,rawText,kind,reaction,contentFingerprint,createdUtc,localReadAt,remoteStoredAt,remoteReadAt FROM Message WHERE conversationId=@conv AND (@before IS NULL OR localSeq<@before) ORDER BY localSeq DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@conv", conversationId);
            cmd.Parameters.AddWithValue("@before", (object?)beforeLocalSeq ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", pageSize + 1);
            using var r = cmd.ExecuteReader(); var rows = new List<StoredMessage>(pageSize + 1);
            while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), Convert.ToHexString((byte[])r.GetValue(9)).ToLowerInvariant(),
                r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13)));
            bool more = rows.Count > pageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            rows.Reverse();
            return new MessagePage(rows.AsReadOnly(), more ? rows[0].LocalSeq : null);
        });
    }

    /// <summary>Human-confirmed local clear. Logical deletion commits even when a reader prevents WAL truncation.</summary>
    public ClearResult ClearConversation(string conversationId)
    {
        ValidateConversationId(conversationId);
        lock (_gate)
        {
            var result = Write<ClearResult?>("clear-conversation", ctx => {
                object? epochValue = ctx.Scalar("SELECT draftEpoch FROM Conversation WHERE id=@id", ("@id", conversationId));
                if (epochValue is null) return (null, false);
                long epoch = Convert.ToInt64(epochValue, CultureInfo.InvariantCulture);
                if (epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
                int receipts = ctx.Exec("DELETE FROM ReceiptOutbox WHERE conversationId=@id", ("@id", conversationId));
                int attempts = ctx.Exec("DELETE FROM ManualSendAttempt WHERE conversationId=@id", ("@id", conversationId));
                int drafts = ctx.Exec("DELETE FROM Draft WHERE conversationId=@id", ("@id", conversationId));
                int messages = ctx.Exec("DELETE FROM Message WHERE conversationId=@id", ("@id", conversationId));
                ctx.Exec("UPDATE Conversation SET draftEpoch=draftEpoch+1 WHERE id=@id", ("@id", conversationId));
                return (new ClearResult(messages, drafts, attempts, receipts, false, epoch + 1), true);
            });
            if (result is null) return new(0, 0, 0, 0, false, 0);
            bool truncated = CheckpointLocked();
            CheckWalSize("clear");
            return result with { WalTruncated = truncated };
        }
    }
}
