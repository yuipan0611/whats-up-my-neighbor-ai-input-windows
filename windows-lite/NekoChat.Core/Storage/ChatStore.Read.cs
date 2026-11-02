namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    public ReadReceiptPolicy GetReadReceiptPolicy() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT enabled,generation FROM ReadReceiptPolicy WHERE singleton=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new StoreException(StoreErrorKind.Corrupt, "Read receipt policy is missing.");
        return new ReadReceiptPolicy(r.GetInt64(0) == 1, r.GetInt64(1));
    });

    public ReadPolicyChange SetReadReceiptPolicy(bool enabled) => Write("read-policy", ctx => {
        var old = ReadPolicy(ctx);
        if (old.Enabled != enabled && enabled && old.Generation == long.MaxValue) throw new InvalidOperationException("Read policy generation exhausted.");
        int dropped = enabled ? 0 : ctx.Exec("DELETE FROM ReceiptOutbox");
        long next = old.Enabled == enabled || old.Generation == long.MaxValue ? old.Generation : old.Generation + 1;
        if (old.Enabled != enabled) ctx.Exec("UPDATE ReadReceiptPolicy SET enabled=@enabled,generation=@generation WHERE singleton=1",
            ("@enabled", enabled ? 1 : 0), ("@generation", next));
        return (new ReadPolicyChange(new(enabled, next), dropped), old.Enabled != enabled || dropped != 0);
    });

    /// <summary>Caller supplies only incoming rows passing real viewport geometry. Use VisibleReadSession to reject stale UI callbacks.</summary>
    public VisibleReadResult MarkVisibleRead(string conversationId, IReadOnlyList<long> visibleLocalSeqs, VisibleReadContext context, Func<bool>? visibilityStillCurrent = null)
    {
        ValidateConversationId(conversationId); ArgumentNullException.ThrowIfNull(visibleLocalSeqs); ArgumentNullException.ThrowIfNull(context);
        if (visibleLocalSeqs.Count > 200) throw new ArgumentOutOfRangeException(nameof(visibleLocalSeqs));
        var ids = visibleLocalSeqs.Distinct().ToArray();
        if (ids.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(visibleLocalSeqs));
        if (context.ActiveConversationId != conversationId || !context.UserOpened || !context.WindowActive || context.Minimized || !context.ViewportReady)
            return new(0, 0, "visibility-guard");
        if (ids.Length == 0) return new(0, 0, "empty");
        return Write("visible-read", ctx => {
            // Revalidate after acquiring SQLite's writer lock, which may have waited for another writer.
            if (visibilityStillCurrent is not null && !visibilityStillCurrent()) return (new VisibleReadResult(0, 0, "stale-view"), false);
            var policy = ReadPolicy(ctx); string? local = CurrentIdentity(ctx); int marked = 0, queued = 0;
            foreach (long seq in ids)
            {
                var rows = ctx.Query("SELECT senderId,recipientId FROM Message WHERE localSeq=@seq AND conversationId=@conv AND direction='incoming' AND localReadAt IS NULL",
                    r => (Sender: r.GetString(0), Recipient: r.GetString(1)), ("@seq", seq), ("@conv", conversationId));
                if (rows.Count == 0) continue;
                marked += ctx.Exec("UPDATE Message SET localReadAt=@now WHERE localSeq=@seq AND localReadAt IS NULL", ("@now", ctx.NowUtc), ("@seq", seq));
                var row = rows[0];
                if (policy.Enabled && local == row.Recipient && IsPaired(ctx, row.Sender))
                    queued += ctx.Exec("INSERT OR IGNORE INTO ReceiptOutbox(originalLocalSeq,conversationId,recipientId,receiptKind,createdUtc) VALUES(@seq,@conv,@peer,'read',@now)",
                        ("@seq", seq), ("@conv", conversationId), ("@peer", row.Sender), ("@now", ctx.NowUtc));
            }
            return (new VisibleReadResult(marked, queued, marked > 0 ? "marked" : "unchanged"), marked > 0);
        });
    }

    public IReadOnlyList<PendingReadReceipt> ListPendingReadReceipts(int limit = 50)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT m.localSeq,m.messageId,m.recipientId,m.senderId,s.generation FROM ReceiptOutbox o JOIN Message m ON m.localSeq=o.originalLocalSeq AND m.conversationId=o.conversationId AND m.senderId=o.recipientId JOIN LocalIdentity l ON l.singleton=1 AND l.id=m.recipientId JOIN Peer p ON p.id=m.senderId AND p.pairingState='paired' JOIN ReadReceiptPolicy s ON s.singleton=1 AND s.enabled=1 WHERE m.direction='incoming' AND m.localReadAt IS NOT NULL AND o.receiptKind='read' ORDER BY m.localSeq LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit); using var r = cmd.ExecuteReader(); var rows = new List<PendingReadReceipt>();
            while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
            return rows.AsReadOnly();
        });
    }

    /// <summary>Only after the transport verifies receipt.ack signature, nonce, status and all id bindings.</summary>
    public bool CompleteReadReceipt(PendingReadReceipt receipt)
        => CompleteReadReceiptCore(receipt, null);

    public bool CompleteV2ReadReceipt(PendingReadReceipt receipt, NekoChat.Core.V2.V2VerifiedAck ack, PeerSecuritySnapshot expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(ack); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (ack.Status is not ("ok" or "duplicate" or "discarded") || ack.ContentFingerprint is not null || ack.MessageId != receipt.MessageId ||
            ack.SenderId != receipt.ReaderId || ack.ReceiverId != receipt.OriginalSenderId) return false;
        return CompleteReadReceiptCore(receipt, new(expected, receipt.OriginalSenderId, receipt.ReaderId, stillCurrent));
    }

    private bool CompleteReadReceiptCore(PendingReadReceipt receipt, SecurityWriteFence? fence)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OriginalLocalSeq <= 0 || receipt.PolicyGeneration < 0 || !StoreIds.IsValid(receipt.MessageId) ||
            !StoreIds.IsValid(receipt.ReaderId) || !StoreIds.IsValid(receipt.OriginalSenderId)) throw new ArgumentException("Invalid receipt token.", nameof(receipt));
        return Write("complete-receipt", ctx => {
            var policy = ReadPolicy(ctx);
            if (!policy.Enabled || policy.Generation != receipt.PolicyGeneration || CurrentIdentity(ctx) != receipt.ReaderId || !IsPaired(ctx, receipt.OriginalSenderId))
                return (false, false);
            int deleted = ctx.Exec("DELETE FROM ReceiptOutbox WHERE originalLocalSeq=@seq AND recipientId=@peer AND EXISTS(SELECT 1 FROM Message m WHERE m.localSeq=ReceiptOutbox.originalLocalSeq AND m.conversationId=ReceiptOutbox.conversationId AND m.direction='incoming' AND m.senderId=@peer AND m.recipientId=@reader AND m.messageId=@id AND m.localReadAt IS NOT NULL)",
                ("@seq", receipt.OriginalLocalSeq), ("@peer", receipt.OriginalSenderId), ("@reader", receipt.ReaderId), ("@id", receipt.MessageId));
            return (deleted != 0, deleted != 0);
        }, guardRejected: fence is null ? null : () => false, transactionGuard: fence is null ? null : ctx => SecurityMatches(ctx, fence));
    }

    private static ReadReceiptPolicy ReadPolicy(WriteContext ctx)
    {
        var rows = ctx.Query("SELECT enabled,generation FROM ReadReceiptPolicy WHERE singleton=1", r => new ReadReceiptPolicy(r.GetInt64(0) == 1, r.GetInt64(1)));
        return rows.Count == 1 ? rows[0] : throw new StoreException(StoreErrorKind.Corrupt, "Read receipt policy is missing.");
    }
}
