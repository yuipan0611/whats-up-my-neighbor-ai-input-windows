using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private sealed record DraftBinding(string Peer, long Epoch, long Revision, string? Text);
    private sealed record AttemptRow(string Id, string Conversation, string Sender, string Recipient, string Text, string Kind,
        string? Reaction, byte[] Fingerprint, long Epoch, long Revision, string State, long Count);

    public DraftState? GetDraftState(string conversationId)
    {
        ValidateConversationId(conversationId);
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT d.rawText,c.draftEpoch,c.draftRevision FROM Conversation c LEFT JOIN Draft d ON d.conversationId=c.id WHERE c.id=@id";
            cmd.Parameters.AddWithValue("@id", conversationId); using var r = cmd.ExecuteReader();
            return r.Read() ? new DraftState(r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), r.GetInt64(2)) : null;
        });
    }

    public DraftSaveResult SaveDraft(string conversationId, string rawText, long draftEpoch, long draftRevision)
    {
        ValidateConversationId(conversationId); ValidateRevision(draftEpoch, draftRevision); ValidateDraftText(rawText);
        return Write("save-draft", ctx => {
            var binding = ReadBinding(ctx, conversationId);
            if (binding is null) return (new DraftSaveResult(false, "not-found", 0, 0), false);
            if (binding.Epoch != draftEpoch) return (new DraftSaveResult(false, "stale-epoch", binding.Epoch, binding.Revision), false);
            if (draftRevision <= binding.Revision) return (new DraftSaveResult(false, "stale-revision", binding.Epoch, binding.Revision), false);
            if (!DraftAllowed(rawText)) return (new DraftSaveResult(false, "draft-too-large", binding.Epoch, binding.Revision), false);
            ValidateJump(binding.Revision, draftRevision);
            WriteDraft(ctx, conversationId, rawText, draftRevision);
            return (new DraftSaveResult(true, "saved", draftEpoch, draftRevision), true);
        });
    }

    public BeginSendResult BeginManualSend(string conversationId, string rawText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null)
    {
        ValidateConversationId(conversationId); ValidateRevision(draftEpoch, draftRevision);
        if (!ValidSendPayload(rawText, kind, reaction)) return new InvalidSend("invalid-payload");
        return Write<BeginSendResult>("begin-send", ctx => StartNew(ctx, conversationId, rawText, kind, reaction, draftEpoch, draftRevision, false));
    }

    public BeginSendResult BeginManualRetry(string messageId, string currentText, string currentConversationId, long draftEpoch, long draftRevision,
        string kind = "text", string? reaction = null)
    {
        ValidateMessageId(messageId); ValidateConversationId(currentConversationId); ValidateRevision(draftEpoch, draftRevision);
        if (!ValidSendPayload(currentText, kind, reaction)) return new InvalidSend("invalid-payload");
        return Write<BeginSendResult>("retry-send", ctx => {
            var old = FindAttempt(ctx, messageId);
            if (old is null) return (new NothingToRetry(), false);
            if (old.State == "superseded") return (new AttemptSuperseded(messageId), false);
            if (old.State == "confirmed") return (new InvalidSend("already-confirmed"), false);
            if (old.State == "sending") return (new AlreadySending(messageId), false);
            if (CurrentIdentity(ctx) != old.Sender)
            {
                ctx.Exec("UPDATE ManualSendAttempt SET attemptState='superseded',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
                return (new AttemptSuperseded(messageId), true);
            }
            bool same = old.Conversation == currentConversationId && old.Text == currentText && old.Kind == kind && old.Reaction == reaction;
            if (!same) return StartNew(ctx, currentConversationId, currentText, kind, reaction, draftEpoch, draftRevision, old.State is "unconfirmed" or "cancelled");
            if (old.Count >= int.MaxValue) return (new InvalidSend("try-count-exhausted"), false);
            var binding = ReadBinding(ctx, currentConversationId);
            if (binding is null) return (new InvalidSend("conversation-not-found"), false);
            if (!IsPaired(ctx, old.Recipient)) return (new InvalidSend("peer-not-paired"), false);
            string? active = SendingId(ctx, currentConversationId);
            if (active is not null) return (new AlreadySending(active), false);
            if (!BindDraft(ctx, currentConversationId, binding, currentText, kind, draftEpoch, draftRevision))
                return (new StaleDraft(binding.Epoch, binding.Revision), false);
            ctx.Exec("UPDATE ManualSendAttempt SET attemptState='sending',tryCount=tryCount+1,draftEpoch=@epoch,draftRevision=@revision,updatedUtc=@now WHERE messageId=@id",
                ("@epoch", draftEpoch), ("@revision", draftRevision), ("@now", ctx.NowUtc), ("@id", messageId));
            return (new AttemptStarted(messageId, true, old.State is "unconfirmed" or "cancelled", checked((int)old.Count + 1), old.Text), true);
        });
    }

    public PreparedSend? GetPreparedSend(string messageId)
    {
        ValidateMessageId(messageId);
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT a.messageId,a.senderId,a.recipientId,a.rawSnapshot,a.kind,a.reaction,a.contentFingerprint,a.tryCount FROM ManualSendAttempt a JOIN LocalIdentity l ON l.singleton=1 AND l.id=a.senderId JOIN Peer p ON p.id=a.recipientId AND p.pairingState='paired' WHERE a.messageId=@id AND a.attemptState='sending'";
            cmd.Parameters.AddWithValue("@id", messageId); using var r = cmd.ExecuteReader();
            return r.Read() ? new PreparedSend(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), Convert.ToHexString((byte[])r.GetValue(6)).ToLowerInvariant(), checked((int)r.GetInt64(7))) : null;
        });
    }

    public CompleteResult CompleteAttempt(string messageId, AttemptOutcome outcome, int? expectedTryCount = null)
        => CompleteAttemptCore(messageId, outcome, expectedTryCount, null);

    public CompleteResult CompleteV2Send(NekoChat.Core.V2.V2VerifiedAck ack, PeerSecuritySnapshot expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(ack); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (ack.Status is not ("stored" or "duplicate") || ack.ContentFingerprint is null) return new Discarded("invalid-ack-kind");
        return CompleteAttemptCore(ack.MessageId, new RemoteStoredOutcome(_options.UtcNow(), Convert.FromHexString(ack.ContentFingerprint)), null,
            new(expected, ack.ReceiverId, ack.SenderId, stillCurrent));
    }

    private CompleteResult CompleteAttemptCore(string messageId, AttemptOutcome outcome, int? expectedTryCount, SecurityWriteFence? fence)
    {
        ValidateMessageId(messageId); ArgumentNullException.ThrowIfNull(outcome);
        if (expectedTryCount is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        if (outcome is not (RemoteStoredOutcome or UnconfirmedOutcome or FailedOutcome)) throw new ArgumentException("Unsupported attempt outcome.", nameof(outcome));
        byte[]? ack = outcome is RemoteStoredOutcome remote ? remote.AckContentFingerprint?.ToArray() : null;
        return Write<CompleteResult>("complete-attempt", ctx => {
            var old = FindAttempt(ctx, messageId);
            if (old is null) return (new Discarded("not-found"), false);
            if (fence is not null && (old.Sender != fence.Local || old.Recipient != fence.Peer)) return (new Discarded("ack-binding"), false);
            if (old.State == "superseded") return (new Discarded("identity-replaced"), false);
            if (old.State == "confirmed")
            {
                object? sequence = ctx.Scalar("SELECT localSeq FROM Message WHERE senderId=@sender AND messageId=@id AND direction='outgoing'",
                    ("@sender", old.Sender), ("@id", messageId));
                return sequence is null ? (new Discarded("missing-confirmed-message"), false) : (new AlreadyConfirmed(Convert.ToInt64(sequence, CultureInfo.InvariantCulture)), false);
            }
            if (CurrentIdentity(ctx) != old.Sender)
            {
                ctx.Exec("UPDATE ManualSendAttempt SET attemptState='superseded',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
                return (new Discarded("identity-replaced"), true);
            }
            if (outcome is not RemoteStoredOutcome)
            {
                if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new Discarded("stale-attempt"), false);
                string state = old.State == "cancelled" ? "cancelled" : outcome is UnconfirmedOutcome ? "unconfirmed" : "failed";
                if (state == old.State) return (new StateUpdated(state), false);
                ctx.Exec("UPDATE ManualSendAttempt SET attemptState=@state,updatedUtc=@now WHERE messageId=@id", ("@state", state), ("@now", ctx.NowUtc), ("@id", messageId));
                return (new StateUpdated(state), true);
            }
            if (ack is null || ack.Length != 32 || !CryptographicOperations.FixedTimeEquals(ack, old.Fingerprint))
            {
                if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new Discarded("stale-attempt"), false);
                if (old.State != "cancelled") ctx.Exec("UPDATE ManualSendAttempt SET attemptState='unconfirmed',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
                return (new AckMismatch(), old.State != "cancelled");
            }
            var binding = ReadBinding(ctx, old.Conversation);
            bool clear = old.Kind != "reaction" && binding is not null && binding.Epoch == old.Epoch && binding.Revision == old.Revision && binding.Text == old.Text;
            if (clear && binding!.Epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
            ctx.Exec("INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,kind,reaction,fingerprintAlg,contentFingerprint,createdUtc,remoteStoredAt) VALUES(@conv,'outgoing',@sender,@recipient,@id,@text,@kind,@reaction,@algorithm,@fingerprint,@now,@now)",
                ("@conv", old.Conversation), ("@sender", old.Sender), ("@recipient", old.Recipient), ("@id", old.Id), ("@text", old.Text),
                ("@kind", old.Kind), ("@reaction", old.Reaction), ("@algorithm", MessageFingerprint.Algorithm), ("@fingerprint", old.Fingerprint), ("@now", ctx.NowUtc));
            long sequenceId = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            ctx.Exec("UPDATE ManualSendAttempt SET attemptState='confirmed',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
            if (clear)
            {
                ctx.Exec("DELETE FROM Draft WHERE conversationId=@conv", ("@conv", old.Conversation));
                ctx.Exec("UPDATE Conversation SET draftEpoch=draftEpoch+1 WHERE id=@conv", ("@conv", old.Conversation));
            }
            return (new Confirmed(sequenceId, clear), true);
        }, guardRejected: fence is null ? null : () => new Discarded("stale-security"), transactionGuard: fence is null ? null : ctx => SecurityMatches(ctx, fence));
    }

    public CancelResult CancelAttempt(string messageId, int? expectedTryCount = null)
    {
        ValidateMessageId(messageId);
        if (expectedTryCount is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        return Write("cancel-attempt", ctx => {
            var old = FindAttempt(ctx, messageId);
            if (old is null) return (new CancelResult(false, "not-found"), false);
            if (old.State is "confirmed" or "superseded") return (new CancelResult(false, "terminal"), false);
            if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new CancelResult(false, "stale-attempt"), false);
            if (old.State == "cancelled") return (new CancelResult(false, "already-cancelled"), false);
            ctx.Exec("UPDATE ManualSendAttempt SET attemptState='cancelled',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
            return (new CancelResult(true, "cancelled"), true);
        });
    }

    public ReceiptApplyResult ApplyRemoteReceipt(string verifiedPeerId, string messageId, RemoteReceiptKind kind, string? originalSenderId = null)
        => ApplyReceipt(verifiedPeerId, messageId, kind, originalSenderId, null);

    public ReceiptApplyResult ApplyV2Receipt(NekoChat.Core.V2.V2VerifiedReceipt receipt, PeerSecuritySnapshot expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        return ApplyReceipt(receipt.SenderId, receipt.MessageId, RemoteReceiptKind.Read, receipt.RecipientId,
            new(expected, receipt.SenderId, receipt.RecipientId, stillCurrent));
    }

    private ReceiptApplyResult ApplyReceipt(string verifiedPeerId, string messageId, RemoteReceiptKind kind, string? originalSenderId, SecurityWriteFence? fence)
    {
        if (!StoreIds.IsValid(verifiedPeerId)) throw new ArgumentException("Invalid peer id.", nameof(verifiedPeerId));
        ValidateMessageId(messageId); if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (originalSenderId is not null && !StoreIds.IsValid(originalSenderId)) throw new ArgumentException("Invalid original sender.", nameof(originalSenderId));
        return Write("apply-receipt", ctx => {
            if (!IsPaired(ctx, verifiedPeerId)) return (new ReceiptApplyResult(false, "untrusted-peer"), false);
            var rows = ctx.Query("SELECT localSeq,remoteStoredAt,remoteReadAt FROM Message WHERE direction='outgoing' AND recipientId=@peer AND messageId=@id AND (@sender IS NULL OR senderId=@sender)",
                r => (Seq: r.GetInt64(0), Stored: !r.IsDBNull(1), Read: !r.IsDBNull(2)), ("@peer", verifiedPeerId), ("@id", messageId), ("@sender", originalSenderId));
            if (rows.Count == 0)
            {
                long pending = Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ManualSendAttempt a JOIN LocalIdentity l ON l.singleton=1 AND l.id=a.senderId WHERE a.recipientId=@peer AND a.messageId=@id AND (@sender IS NULL OR a.senderId=@sender) AND a.attemptState NOT IN ('confirmed','superseded')",
                    ("@peer", verifiedPeerId), ("@id", messageId), ("@sender", originalSenderId)), CultureInfo.InvariantCulture);
                return (new ReceiptApplyResult(false, pending > 0 ? "pending-local-confirmation" : "not-found"), false);
            }
            if (rows.Count != 1) return (new ReceiptApplyResult(false, "ambiguous-message"), false);
            var row = rows[0];
            if (row.Stored && (kind == RemoteReceiptKind.Stored || row.Read)) return (new ReceiptApplyResult(false, "unchanged", row.Seq), false);
            ctx.Exec("UPDATE Message SET remoteStoredAt=coalesce(remoteStoredAt,@now),remoteReadAt=CASE WHEN @read=1 THEN coalesce(remoteReadAt,@now) ELSE remoteReadAt END WHERE localSeq=@seq",
                ("@now", ctx.NowUtc), ("@read", kind == RemoteReceiptKind.Read ? 1 : 0), ("@seq", row.Seq));
            return (new ReceiptApplyResult(true, "applied", row.Seq), true);
        }, guardRejected: fence is null ? null : () => new ReceiptApplyResult(false, "stale-security"), transactionGuard: fence is null ? null : ctx => SecurityMatches(ctx, fence));
    }

    private (BeginSendResult Value, bool Commit) StartNew(WriteContext ctx, string conversation, string text, string kind, string? reaction, long epoch, long revision, bool mayHaveDelivered)
    {
        var binding = ReadBinding(ctx, conversation);
        if (binding is null) return (new InvalidSend("conversation-not-found"), false);
        string? sender = CurrentIdentity(ctx);
        if (sender is null) return (new InvalidSend("no-identity"), false);
        if (!IsPaired(ctx, binding.Peer)) return (new InvalidSend("peer-not-paired"), false);
        string? active = SendingId(ctx, conversation);
        if (active is not null) return (new AlreadySending(active), false);
        if (!BindDraft(ctx, conversation, binding, text, kind, epoch, revision)) return (new StaleDraft(binding.Epoch, binding.Revision), false);
        string? id = null;
        for (int i = 0; i < 3; i++)
        {
            string candidate = _options.NewId();
            if (!StoreIds.IsUuid(candidate)) return (new InvalidSend("id-generator-invalid"), false);   // rollback: the draft binding above is not kept
            long taken = Convert.ToInt64(ctx.Scalar("SELECT (SELECT count(*) FROM ManualSendAttempt WHERE messageId=@id)+(SELECT count(*) FROM Message WHERE senderId=@sender AND messageId=@id)+(SELECT count(*) FROM ClearedMessage WHERE senderId=@sender AND messageId=@id)",
                ("@id", candidate), ("@sender", sender)), CultureInfo.InvariantCulture);
            if (taken == 0) { id = candidate; break; }
        }
        if (id is null) return (new InvalidSend("id-allocation-failed"), false);   // bounded retries exhausted; whole transaction rolled back, no attempt overwritten
        ctx.Exec("INSERT INTO ManualSendAttempt(messageId,conversationId,senderId,recipientId,rawSnapshot,kind,reaction,fingerprintAlg,contentFingerprint,draftEpoch,draftRevision,attemptState,createdUtc,updatedUtc) VALUES(@id,@conv,@sender,@recipient,@text,@kind,@reaction,@algorithm,@fingerprint,@epoch,@revision,'sending',@now,@now)",
            ("@id", id), ("@conv", conversation), ("@sender", sender), ("@recipient", binding.Peer), ("@text", text), ("@kind", kind), ("@reaction", reaction),
            ("@algorithm", MessageFingerprint.Algorithm), ("@fingerprint", MessageFingerprint.Compute(binding.Peer, text, reaction)), ("@epoch", epoch), ("@revision", revision), ("@now", ctx.NowUtc));
        return (new AttemptStarted(id, false, mayHaveDelivered, 1, text), true);
    }

    private static DraftBinding? ReadBinding(WriteContext ctx, string conversation) => ctx.Query(
        "SELECT c.peerId,c.draftEpoch,c.draftRevision,d.rawText FROM Conversation c LEFT JOIN Draft d ON d.conversationId=c.id WHERE c.id=@id",
        r => new DraftBinding(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)), ("@id", conversation)).SingleOrDefault();
    private static string? CurrentIdentity(WriteContext ctx) => ctx.Scalar("SELECT id FROM LocalIdentity WHERE singleton=1") as string;
    private static bool IsPaired(WriteContext ctx, string peer) => ctx.Scalar("SELECT pairingState FROM Peer WHERE id=@id", ("@id", peer)) as string == "paired";
    private static string? SendingId(WriteContext ctx, string conversation) => ctx.Scalar("SELECT messageId FROM ManualSendAttempt WHERE conversationId=@conv AND attemptState='sending'", ("@conv", conversation)) as string;
    private static AttemptRow? FindAttempt(WriteContext ctx, string id) => ctx.Query(
        "SELECT messageId,conversationId,senderId,recipientId,rawSnapshot,kind,reaction,contentFingerprint,draftEpoch,draftRevision,attemptState,tryCount FROM ManualSendAttempt WHERE messageId=@id",
        r => new AttemptRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            (byte[])r.GetValue(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10), r.GetInt64(11)), ("@id", id)).SingleOrDefault();

    private static bool BindDraft(WriteContext ctx, string conversation, DraftBinding binding, string text, string kind, long epoch, long revision)
    {
        if (epoch != binding.Epoch || revision < binding.Revision) return false;
        if (kind == "reaction") return revision == binding.Revision; // Quick reaction buttons never overwrite the composer.
        if (revision == binding.Revision) return binding.Text == text;
        ValidateJump(binding.Revision, revision); WriteDraft(ctx, conversation, text, revision); return true;
    }
    private static void WriteDraft(WriteContext ctx, string conversation, string text, long revision)
    {
        if (text.Length == 0) ctx.Exec("DELETE FROM Draft WHERE conversationId=@id", ("@id", conversation));
        else ctx.Exec("INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES(@id,@text,@now) ON CONFLICT(conversationId) DO UPDATE SET rawText=excluded.rawText,updatedUtc=excluded.updatedUtc",
            ("@id", conversation), ("@text", text), ("@now", ctx.NowUtc));
        ctx.Exec("UPDATE Conversation SET draftRevision=@revision WHERE id=@id", ("@revision", revision), ("@id", conversation));
    }
    private bool ValidSendPayload(string text, string kind, string? reaction)
    {
        if (string.IsNullOrWhiteSpace(text) || !_options.TextValidator(text) || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            kind is null || kind.Length is < 1 or > 32 || kind[0] is not (>= 'a' and <= 'z') || kind.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')) ||
            !MessageFingerprint.ValidReaction(reaction) || kind == "reaction" && reaction is null) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; } catch (EncoderFallbackException) { return false; }
    }
    /// <summary>
    /// Draft size limit. No number is written here: by default a draft is acceptable when it is empty, or when the configured
    /// TextValidator (the send limit, D-06) accepts it, or when it is only whitespace that the same validator would accept
    /// next to one visible character. Inject ChatStoreOptions.DraftValidator to override.
    /// </summary>
    private bool DraftAllowed(string text)
    {
        if (_options.DraftValidator is { } custom) return custom(text);
        if (text.Length == 0 || _options.TextValidator(text)) return true;
        return string.IsNullOrWhiteSpace(text) && _options.TextValidator("x" + text);
    }

    private static void ValidateDraftText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new ArgumentException("Draft contains NUL.", nameof(text));
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); } catch (EncoderFallbackException) { throw new ArgumentException("Invalid draft Unicode.", nameof(text)); }
    }
    private static void ValidateConversationId(string id) { if (!StoreIds.IsUuid(id)) throw new ArgumentException("Invalid conversation id.", nameof(id)); }
    private static void ValidateMessageId(string id) { if (!StoreIds.IsValid(id)) throw new ArgumentException("Invalid message id.", nameof(id)); }
    private static void ValidateRevision(long epoch, long revision) { ArgumentOutOfRangeException.ThrowIfNegative(epoch); ArgumentOutOfRangeException.ThrowIfNegative(revision); }
    private static void ValidateJump(long old, long next) { if (next > old && next - old > 1_000_000) throw new ArgumentOutOfRangeException(nameof(next), "Draft revision jump too large."); }
}
