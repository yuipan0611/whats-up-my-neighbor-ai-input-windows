using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    public ReceiveResult ReceiveVerifiedMessage(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!StoreIds.IsValid(message.VerifiedSenderPeerId) || !StoreIds.IsValid(message.RecipientId) || !StoreIds.IsValid(message.MessageId))
            return new InvalidPayload("invalid-id");
        if (string.IsNullOrWhiteSpace(message.RawText) || !_options.TextValidator(message.RawText) ||
            message.RawText.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return new InvalidPayload("invalid-text");
        if (message.Kind is null || message.Kind.Length is < 1 or > 32 ||
            message.Kind[0] is not (>= 'a' and <= 'z') || message.Kind.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')))
            return new InvalidPayload("invalid-kind");
        if (!MessageFingerprint.ValidReaction(message.Reaction)) return new InvalidPayload("invalid-reaction");
        if (message.SourceEndpoint is not null) ValidateEndpoint(message.SourceEndpoint);
        byte[] fingerprint;
        try { fingerprint = MessageFingerprint.Compute(message.RecipientId, message.RawText, message.Reaction); }
        catch (EncoderFallbackException) { return new InvalidPayload("invalid-unicode"); }
        // Immediate transactions serialize all writers. The unique index remains the final defense;
        // one bounded retry also handles a competing writer or a generated conversation-id collision.
        try { return ReceiveTransaction(message, fingerprint); }
        catch (StoreException e) when (e.Kind == StoreErrorKind.ConstraintViolation && e.SqliteExtendedCode is 1555 or 2067)
        { return ReceiveTransaction(message, fingerprint); }
    }

    private ReceiveResult ReceiveTransaction(IncomingMessage message, byte[] fingerprint) => Write<ReceiveResult>("receive", ctx =>
    {
        if (!string.Equals(ctx.Scalar("SELECT id FROM LocalIdentity WHERE singleton=1") as string, message.RecipientId, StringComparison.Ordinal))
            return (new WrongRecipient(), false);
        string? state = ctx.Scalar("SELECT pairingState FROM Peer WHERE id=@id", ("@id", message.VerifiedSenderPeerId)) as string;
        if (state != "paired") return (new UntrustedPeer(), false);
        if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClearedMessage WHERE senderId=@sender AND messageId=@id",
            ("@sender", message.VerifiedSenderPeerId), ("@id", message.MessageId)), CultureInfo.InvariantCulture) != 0)
            return (new ClearedDuplicate(message.MessageId), false);
        var prior = ctx.Query("SELECT localSeq,direction,recipientId,rawText,kind,reaction,fingerprintAlg,contentFingerprint FROM Message WHERE senderId=@sender AND messageId=@id",
            r => (Seq: r.GetInt64(0), Direction: r.GetString(1), Recipient: r.GetString(2), Text: r.GetString(3), Kind: r.GetString(4),
                Reaction: r.IsDBNull(5) ? null : r.GetString(5), Algorithm: r.GetString(6), Fingerprint: (byte[])r.GetValue(7)),
            ("@sender", message.VerifiedSenderPeerId), ("@id", message.MessageId));
        if (prior.Count != 0)
        {
            var old = prior[0];
            bool same = old.Direction == "incoming" && old.Recipient == message.RecipientId && old.Text == message.RawText &&
                old.Kind == message.Kind && old.Reaction == message.Reaction && old.Algorithm == MessageFingerprint.Algorithm &&
                CryptographicOperations.FixedTimeEquals(old.Fingerprint, fingerprint);
            return (same ? new Duplicate(message.MessageId, old.Seq) : new Conflict(message.MessageId), false);
        }
        var conversation = ConversationFor(ctx, message.VerifiedSenderPeerId);
        ctx.Exec("INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,kind,reaction,fingerprintAlg,contentFingerprint,createdUtc) VALUES(@conv,'incoming',@sender,@recipient,@id,@text,@kind,@reaction,@algorithm,@fingerprint,@now)",
            ("@conv", conversation.ConversationId), ("@sender", message.VerifiedSenderPeerId), ("@recipient", message.RecipientId),
            ("@id", message.MessageId), ("@text", message.RawText), ("@kind", message.Kind), ("@reaction", message.Reaction),
            ("@algorithm", MessageFingerprint.Algorithm), ("@fingerprint", fingerprint), ("@now", ctx.NowUtc));
        long sequence = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        if (message.SourceEndpoint is not null)
            UpdateEvidence(ctx, message.VerifiedSenderPeerId, message.SourceEndpoint, install: true, MessagePathThrottle);
        int unread = Convert.ToInt32(ctx.Scalar("SELECT count(*) FROM Message WHERE conversationId=@conv AND direction='incoming' AND localReadAt IS NULL",
            ("@conv", conversation.ConversationId)), CultureInfo.InvariantCulture);
        return (new Stored(message.MessageId, sequence, unread), true);
    });

    public int GetUnreadCount(string conversationId)
    {
        if (!StoreIds.IsUuid(conversationId)) throw new ArgumentException("Invalid conversation id.", nameof(conversationId));
        return Read(c => {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT count(*) FROM Message WHERE conversationId=@conv AND direction='incoming' AND localReadAt IS NULL";
            command.Parameters.AddWithValue("@conv", conversationId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    public PresenceWriteResult ApplyPresenceEvidence(string peerId, string sourceEndpoint, bool installEvidenceAppVerified)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("Invalid peer id.", nameof(peerId));
        ValidateEndpoint(sourceEndpoint);
        return Write("presence-evidence", ctx => {
            var result = UpdateEvidence(ctx, peerId, sourceEndpoint, installEvidenceAppVerified, TimeSpan.FromSeconds(60));
            return (new PresenceWriteResult(result.Written, result.Reason), result.Written);
        });
    }

    private ConversationRef ConversationFor(WriteContext ctx, string peerId)
    {
        var found = ctx.Query("SELECT id,draftEpoch,draftRevision FROM Conversation WHERE peerId=@peer",
            r => new ConversationRef(r.GetString(0), peerId, r.GetInt64(1), r.GetInt64(2)), ("@peer", peerId));
        if (found.Count != 0) return found[0];
        string id = _options.NewId();
        if (!StoreIds.IsUuid(id)) throw new InvalidOperationException("NewId produced an invalid conversation UUID");
        ctx.Exec("INSERT INTO Conversation(id,peerId,createdUtc) VALUES(@id,@peer,@now)", ("@id", id), ("@peer", peerId), ("@now", ctx.NowUtc));
        return new(id, peerId, 0, 0);
    }

    private static void ValidateEndpoint(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid source endpoint.", nameof(value));
    }

    private PeerSeenResult UpdateEvidence(WriteContext ctx, string peerId, string endpoint, bool install, TimeSpan interval)
    {
        var rows = ctx.Query("SELECT pairingState,lastSeenEndpoint,lastSeenUtc,installEvidence FROM Peer WHERE id=@id",
            r => (State: r.GetString(0), Endpoint: r.IsDBNull(1) ? null : r.GetString(1), Seen: r.IsDBNull(2) ? null : r.GetString(2),
                Install: r.IsDBNull(3) ? null : r.GetString(3)), ("@id", peerId));
        if (rows.Count == 0) return new(false, "unknown-or-untrusted-peer", false, null);
        var row = rows[0];
        if (!PairingStates.TryParse(row.State, out var state))
        {
            Interlocked.Increment(ref _unknownPairingStates);
            _options.DiagnosticSink?.Invoke("unknown-pairing-state treated-as-untrusted");
            return new(false, "unknown-or-untrusted-peer", false, null);
        }
        if (state != PairingState.Paired) return new(false, "unknown-or-untrusted-peer", false, null);
        DateTime now = _options.UtcNow().ToUniversalTime();
        bool changed = row.Endpoint is not null && row.Endpoint != endpoint;
        bool firstInstall = install && row.Install is null;
        if (row.Seen is not null && row.Endpoint is not null && !changed && !firstInstall &&
            TryParseIso(row.Seen, out var seen) && seen <= now && now - seen < interval)
            return new(false, "throttled", false, null);
        ctx.Exec("UPDATE Peer SET lastSeenEndpoint=@endpoint,lastSeenUtc=@now,installEvidence=CASE WHEN @first=1 THEN 'appVerified' ELSE installEvidence END,installEvidenceUtc=CASE WHEN @first=1 THEN @now ELSE installEvidenceUtc END WHERE id=@id",
            ("@endpoint", endpoint), ("@now", Iso(now)), ("@first", firstInstall ? 1 : 0), ("@id", peerId));
        return new(true, "written", changed, changed ? row.Endpoint : null);
    }
}
