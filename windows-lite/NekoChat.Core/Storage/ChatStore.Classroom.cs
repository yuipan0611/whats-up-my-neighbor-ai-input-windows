using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Storage;

/// <summary>
/// Classroom-mode conversations (CM-10/11/12, PMCM R2/R4). Everything is keyed by <see cref="ClassroomPrincipalKey"/>, decided from the
/// address of the accepted socket. No key, signature, Peer row or pairing state is involved, and nothing here loosens the signed V2 path.
/// Same guards as the verified path: one immediate write transaction per operation, set-once timestamps, draft epoch/revision, terminal attempts.
/// </summary>
public sealed partial class ChatStore
{
    private sealed record ClassroomBinding(long Epoch, long Revision, string? Text);
    private sealed record ClassroomAttemptRow(string Id, string Principal, string Text, string Kind, string? Reaction, byte[] Fingerprint, long Epoch, long Revision, string State, long Count);
    private const string ClassroomFingerprintAlgorithm = "nekochat-classroom-content";

    public static byte[] ClassroomFingerprint(string rawText, string? reaction)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var utf8 = new UTF8Encoding(false, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(utf8.GetBytes(ClassroomFingerprintAlgorithm + "\n" + (reaction ?? "") + "\n")); hash.AppendData(utf8.GetBytes(rawText));
        return hash.GetHashAndReset();
    }

    private static void RequirePrincipal(ClassroomPrincipalKey principal) { if (principal.Value is null) throw new ArgumentException("Principal is required.", nameof(principal)); }
    private static ClassroomPrincipalKey ResolveClassroomPrincipal(WriteContext ctx, string canonicalIp) =>
        ctx.Scalar("SELECT id FROM RosterEntry WHERE endpoint=@ip", ("@ip", canonicalIp)) is string id ? ClassroomPrincipalKey.ForRosterEntry(id) : ClassroomPrincipalKey.ForSourceIp(canonicalIp);

    /// <summary>Which conversation an address belongs to right now. Null for an address that is not an acceptable classroom source.</summary>
    public ClassroomPrincipalKey? ResolveClassroomPrincipal(string sourceIp)
    {
        if (ClassroomPrincipalKey.ForSource(sourceIp) is not { } key) return null;
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id FROM RosterEntry WHERE endpoint=@ip"; cmd.Parameters.AddWithValue("@ip", key.SourceIp);
            return cmd.ExecuteScalar() is string id ? ClassroomPrincipalKey.ForRosterEntry(id) : key;
        });
    }

    // ---- receive ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="observedSourceIp"/> must be the canonical IPv4 of the remote address of the socket (<c>ClassroomSource.Canonical</c>) that delivered the message (never a value from the body).
    /// The caller sends the ACK only after this returns Stored, Duplicate or ClearedDuplicate.
    /// </summary>
    public ClassroomReceiveResult ReceiveClassroomMessage(string observedSourceIp, ClassroomIncoming message, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (ClassroomPrincipalKey.ForSource(observedSourceIp) is not { } source)
        { _options.DiagnosticSink?.Invoke("classroom-source-rejected"); return new ClassroomRejected("invalid-source"); }
        if (!StoreIds.IsValid(message.MessageId)) return new ClassroomRejected("invalid-id");
        if (string.IsNullOrWhiteSpace(message.Text) || !_options.TextValidator(message.Text) || message.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return new ClassroomRejected("invalid-text");
        if (!MessageFingerprint.ValidReaction(message.Reaction)) return new ClassroomRejected("invalid-reaction");
        byte[] fingerprint;
        try { fingerprint = ClassroomFingerprint(message.Text, message.Reaction); }
        catch (EncoderFallbackException) { return new ClassroomRejected("invalid-unicode"); }
        string ip = source.SourceIp!; string? limit = null;
        ClassroomReceiveResult result;
        try { result = ClassroomReceiveTransaction(ip, message, fingerprint, r => limit = r, stillCurrent); }
        catch (StoreException e) when (e.Kind == StoreErrorKind.ConstraintViolation && e.SqliteExtendedCode is 1555 or 2067)
        { result = ClassroomReceiveTransaction(ip, message, fingerprint, r => limit = r, stillCurrent); }
        if (limit is not null) _options.DiagnosticSink?.Invoke("classroom-offroster-limit scope=" + limit);   // size-only: no address, no content
        return result;
    }

    private ClassroomReceiveResult ClassroomReceiveTransaction(string ip, ClassroomIncoming message, byte[] fingerprint, Action<string> limitHit, Func<bool> stillCurrent) => Write<ClassroomReceiveResult>("classroom-receive", ctx =>
    {
        var principal = ResolveClassroomPrincipal(ctx, ip);   // roster join and insert share one transaction: no stale attribution
        if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomCleared WHERE principal=@p AND direction='incoming' AND messageId=@id", ("@p", principal.Value), ("@id", message.MessageId)), CultureInfo.InvariantCulture) != 0)
            return (new ClassroomClearedDuplicate(principal, message.MessageId), false);
        var prior = ctx.Query("SELECT localSeq,rawText,reaction,contentFingerprint FROM ClassroomMessage WHERE principal=@p AND direction='incoming' AND messageId=@id",
            r => (Seq: r.GetInt64(0), Text: r.GetString(1), Reaction: r.IsDBNull(2) ? null : r.GetString(2), Fingerprint: (byte[])r.GetValue(3)), ("@p", principal.Value), ("@id", message.MessageId));
        if (prior.Count != 0)
        {
            var old = prior[0];
            bool same = old.Text == message.Text && old.Reaction == message.Reaction && CryptographicOperations.FixedTimeEquals(old.Fingerprint, fingerprint);
            return (same ? new ClassroomDuplicate(principal, message.MessageId, old.Seq) : new ClassroomConflict(principal, message.MessageId), false);
        }
        if (!principal.IsRoster && OffRosterLimit(ctx, principal.SourceIp!, Encoding.UTF8.GetByteCount(message.Text)) is { } hit)
        { limitHit(hit); return (new ClassroomRejected("offroster-limit"), false); }   // CM-11: refuse the new message, never delete stored rows
        EnsureClassroomConversation(ctx, principal);
        ctx.Exec("INSERT INTO ClassroomMessage(principal,direction,messageId,rawText,kind,reaction,contentFingerprint,sourceIp,createdUtc) VALUES(@p,'incoming',@id,@text,'text',@reaction,@fp,@ip,@now)",
            ("@p", principal.Value), ("@id", message.MessageId), ("@text", message.Text), ("@reaction", message.Reaction), ("@fp", fingerprint), ("@ip", ip), ("@now", ctx.NowUtc));
        long seq = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        int unread = Convert.ToInt32(ctx.Scalar("SELECT count(*) FROM ClassroomMessage WHERE principal=@p AND direction='incoming' AND localReadAt IS NULL", ("@p", principal.Value)), CultureInfo.InvariantCulture);
        return (new ClassroomStored(principal, message.MessageId, seq, unread), true);
    }, guardRejected: () => new ClassroomRejected("receiver-not-ready"), transactionGuard: _ => stillCurrent());   // checked again right before COMMIT (listener stopped or request cancelled while waiting for the writer)

    /// <summary>
    /// Which bound the NEW message would exceed, or null (CM-11). One shared budget for everything that is not on the roster: classroom conversations
    /// (<c>ip:</c> principals) and v1 LegacyMessage rows with no roster owner. Roster members are never bounded; stored rows are never deleted.
    /// </summary>
    private string? OffRosterLimit(WriteContext ctx, string ip, int newBytes)
    {
        var limits = _options.OffRoster;
        var (sources, perSource, total, bytes, known) = ctx.Query(
            "WITH u AS (SELECT sourceIp AS ip,length(CAST(rawText AS BLOB)) AS n FROM LegacyMessage WHERE rosterEntryId IS NULL" + (_migrations.Count >= 7 ? " UNION ALL SELECT substr(principal,4),length(CAST(rawText AS BLOB)) FROM ClassroomMessage WHERE direction='incoming' AND principal GLOB 'ip:*'" : "") + ") SELECT count(DISTINCT ip),coalesce(sum(CASE WHEN ip=@ip THEN 1 ELSE 0 END),0),count(*),coalesce(sum(n),0),coalesce(max(ip=@ip),0) FROM u",
            r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) == 1), ("@ip", ip)).Single();
        if (!known && sources >= limits.MaxSources) return "sources";
        if (perSource >= limits.MaxMessagesPerSource) return "source";
        if (total >= limits.MaxMessagesTotal) return "total";
        if (bytes + newBytes > limits.MaxTotalBytes) return "bytes";
        return null;
    }

    private bool EnsureClassroomConversation(WriteContext ctx, ClassroomPrincipalKey principal)
    {
        if (principal.IsRoster && ctx.Scalar("SELECT count(*) FROM RosterEntry WHERE id=@id", ("@id", principal.RosterEntryId)) is not (long and 1)) return false;
        ctx.Exec("INSERT OR IGNORE INTO ClassroomConversation(principal,createdUtc) VALUES(@p,@now)", ("@p", principal.Value), ("@now", ctx.NowUtc));
        return true;
    }

    // ---- drafts -----------------------------------------------------------------------------------------------------------------------

    public DraftState GetClassroomDraftState(ClassroomPrincipalKey principal)
    {
        RequirePrincipal(principal);
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT d.rawText,c.draftEpoch,c.draftRevision FROM ClassroomConversation c LEFT JOIN ClassroomDraft d ON d.principal=c.principal WHERE c.principal=@p";
            cmd.Parameters.AddWithValue("@p", principal.Value); using var r = cmd.ExecuteReader();
            return r.Read() ? new DraftState(r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), r.GetInt64(2)) : new DraftState(null, 0, 0);
        });
    }

    /// <summary>CM-12: a draft is kept up to 1 MiB of UTF-8, never truncated; the send limit applies only when sending.</summary>
    public DraftSaveResult SaveClassroomDraft(ClassroomPrincipalKey principal, string rawText, long draftEpoch, long draftRevision)
    {
        RequirePrincipal(principal); ValidateRevision(draftEpoch, draftRevision); ValidateDraftText(rawText);
        if (Encoding.UTF8.GetByteCount(rawText) > ClassroomLimits.MaxDraftBytes) { var current = GetClassroomDraftState(principal); return new DraftSaveResult(false, "draft-too-large", current.Epoch, current.Revision); }
        return Write("classroom-save-draft", ctx => {
            if (!EnsureClassroomConversation(ctx, principal)) return (new DraftSaveResult(false, "not-found", 0, 0), false);
            var binding = ReadClassroomBinding(ctx, principal.Value)!;
            if (binding.Epoch != draftEpoch) return (new DraftSaveResult(false, "stale-epoch", binding.Epoch, binding.Revision), false);
            if (draftRevision <= binding.Revision) return (new DraftSaveResult(false, "stale-revision", binding.Epoch, binding.Revision), false);
            ValidateJump(binding.Revision, draftRevision);
            WriteClassroomDraft(ctx, principal.Value, rawText, draftRevision);
            return (new DraftSaveResult(true, "saved", draftEpoch, draftRevision), true);
        });
    }

    private static ClassroomBinding? ReadClassroomBinding(WriteContext ctx, string principal) => ctx.Query(
        "SELECT c.draftEpoch,c.draftRevision,d.rawText FROM ClassroomConversation c LEFT JOIN ClassroomDraft d ON d.principal=c.principal WHERE c.principal=@p",
        r => new ClassroomBinding(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)), ("@p", principal)).SingleOrDefault();
    private static void WriteClassroomDraft(WriteContext ctx, string principal, string text, long revision)
    {
        if (text.Length == 0) ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", principal));
        else ctx.Exec("INSERT INTO ClassroomDraft(principal,rawText,updatedUtc) VALUES(@p,@text,@now) ON CONFLICT(principal) DO UPDATE SET rawText=excluded.rawText,updatedUtc=excluded.updatedUtc",
            ("@p", principal), ("@text", text), ("@now", ctx.NowUtc));
        ctx.Exec("UPDATE ClassroomConversation SET draftRevision=@revision WHERE principal=@p", ("@revision", revision), ("@p", principal));
    }
    private static bool BindClassroomDraft(WriteContext ctx, string principal, ClassroomBinding binding, string text, string kind, long epoch, long revision)
    {
        if (epoch != binding.Epoch || revision < binding.Revision) return false;
        if (kind == "reaction") return revision == binding.Revision;
        if (revision == binding.Revision) return binding.Text == text;
        ValidateJump(binding.Revision, revision); WriteClassroomDraft(ctx, principal, text, revision); return true;
    }

    // ---- manual send ------------------------------------------------------------------------------------------------------------------

    /// <summary>The address a send to this conversation goes to right now. Null when there is none (roster row without an address, or deleted).</summary>
    private static string? ClassroomTarget(WriteContext ctx, ClassroomPrincipalKey principal) => principal.IsRoster
        ? ctx.Scalar("SELECT endpoint FROM RosterEntry WHERE id=@id", ("@id", principal.RosterEntryId)) as string
        : principal.SourceIp;
    private static string NoTargetReason(WriteContext ctx, ClassroomPrincipalKey principal) =>
        !principal.IsRoster || Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM RosterEntry WHERE id=@id", ("@id", principal.RosterEntryId)), CultureInfo.InvariantCulture) != 0 ? "no-address"
        : _removedTableExists(ctx) && Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM RosterRemoved WHERE rosterEntryId=@id", ("@id", principal.RosterEntryId)), CultureInfo.InvariantCulture) != 0 ? "removed" : "not-found";
    private static bool _removedTableExists(WriteContext ctx) => Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='RosterRemoved'"), CultureInfo.InvariantCulture) == 1;

    public BeginSendResult BeginClassroomSend(ClassroomPrincipalKey principal, string rawText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null)
    {
        RequirePrincipal(principal); ValidateRevision(draftEpoch, draftRevision);
        if (!ValidSendPayload(rawText, kind, reaction)) return new InvalidSend("invalid-payload");
        return Write<BeginSendResult>("classroom-begin-send", ctx => StartClassroomAttempt(ctx, principal, rawText, kind, reaction, draftEpoch, draftRevision, false));
    }

    public BeginSendResult BeginClassroomRetry(string messageId, ClassroomPrincipalKey principal, string currentText, long draftEpoch, long draftRevision, string kind = "text", string? reaction = null)
    {
        ValidateMessageId(messageId); RequirePrincipal(principal); ValidateRevision(draftEpoch, draftRevision);
        if (!ValidSendPayload(currentText, kind, reaction)) return new InvalidSend("invalid-payload");
        return Write<BeginSendResult>("classroom-retry-send", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null) return (new NothingToRetry(), false);
            if (old.State == "superseded") return (new AttemptSuperseded(messageId), false);
            if (old.State == "confirmed") return (new InvalidSend("already-confirmed"), false);
            if (old.State == "sending") return (new AlreadySending(messageId), false);
            bool same = old.Principal == principal.Value && old.Text == currentText && old.Kind == kind && old.Reaction == reaction;
            bool mayHave = old.State is "unconfirmed" or "cancelled";
            if (!same) return StartClassroomAttempt(ctx, principal, currentText, kind, reaction, draftEpoch, draftRevision, mayHave);
            if (old.Count >= int.MaxValue) return (new InvalidSend("try-count-exhausted"), false);
            if (!EnsureClassroomConversation(ctx, principal)) return (new InvalidSend(NoTargetReason(ctx, principal)), false);
            if (ClassroomTarget(ctx, principal) is null) return (new InvalidSend(NoTargetReason(ctx, principal)), false);
            var binding = ReadClassroomBinding(ctx, principal.Value)!;
            if (ctx.Scalar("SELECT messageId FROM ClassroomAttempt WHERE principal=@p AND attemptState='sending'", ("@p", principal.Value)) is string active) return (new AlreadySending(active), false);
            if (!BindClassroomDraft(ctx, principal.Value, binding, currentText, kind, draftEpoch, draftRevision)) return (new StaleDraft(binding.Epoch, binding.Revision), false);
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='sending',tryCount=tryCount+1,draftEpoch=@epoch,draftRevision=@revision,updatedUtc=@now WHERE messageId=@id",
                ("@epoch", draftEpoch), ("@revision", draftRevision), ("@now", ctx.NowUtc), ("@id", messageId));
            return (new AttemptStarted(messageId, true, mayHave, checked((int)old.Count + 1), old.Text), true);
        });
    }

    private (BeginSendResult Value, bool Commit) StartClassroomAttempt(WriteContext ctx, ClassroomPrincipalKey principal, string text, string kind, string? reaction, long epoch, long revision, bool mayHaveDelivered)
    {
        if (!EnsureClassroomConversation(ctx, principal)) return (new InvalidSend(NoTargetReason(ctx, principal)), false);
        if (ClassroomTarget(ctx, principal) is null) return (new InvalidSend(NoTargetReason(ctx, principal)), false);   // CM-04: no address, no sending
        var binding = ReadClassroomBinding(ctx, principal.Value)!;
        if (ctx.Scalar("SELECT messageId FROM ClassroomAttempt WHERE principal=@p AND attemptState='sending'", ("@p", principal.Value)) is string active) return (new AlreadySending(active), false);
        if (!BindClassroomDraft(ctx, principal.Value, binding, text, kind, epoch, revision)) return (new StaleDraft(binding.Epoch, binding.Revision), false);
        string? id = null;
        for (int i = 0; i < 3; i++)
        {
            string candidate = _options.NewId();
            if (!StoreIds.IsUuid(candidate)) return (new InvalidSend("id-generator-invalid"), false);
            long taken = Convert.ToInt64(ctx.Scalar("SELECT (SELECT count(*) FROM ClassroomAttempt WHERE messageId=@id)+(SELECT count(*) FROM ClassroomMessage WHERE principal=@p AND direction='outgoing' AND messageId=@id)+(SELECT count(*) FROM ClassroomCleared WHERE principal=@p AND direction='outgoing' AND messageId=@id)",
                ("@id", candidate), ("@p", principal.Value)), CultureInfo.InvariantCulture);
            if (taken == 0) { id = candidate; break; }
        }
        if (id is null) return (new InvalidSend("id-allocation-failed"), false);
        ctx.Exec("INSERT INTO ClassroomAttempt(messageId,principal,rawSnapshot,kind,reaction,contentFingerprint,draftEpoch,draftRevision,attemptState,createdUtc,updatedUtc) VALUES(@id,@p,@text,@kind,@reaction,@fp,@epoch,@revision,'sending',@now,@now)",
            ("@id", id), ("@p", principal.Value), ("@text", text), ("@kind", kind), ("@reaction", reaction), ("@fp", ClassroomFingerprint(text, reaction)), ("@epoch", epoch), ("@revision", revision), ("@now", ctx.NowUtc));
        return (new AttemptStarted(id, false, mayHaveDelivered, 1, text), true);
    }

    private static ClassroomAttemptRow? FindClassroomAttempt(WriteContext ctx, string id) => ctx.Query(
        "SELECT messageId,principal,rawSnapshot,kind,reaction,contentFingerprint,draftEpoch,draftRevision,attemptState,tryCount FROM ClassroomAttempt WHERE messageId=@id",
        r => new ClassroomAttemptRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), (byte[])r.GetValue(5), r.GetInt64(6), r.GetInt64(7), r.GetString(8), r.GetInt64(9)), ("@id", id)).SingleOrDefault();

    /// <summary>The attempt to hand to the transport, with the address resolved at this moment (it may differ from when the attempt began).</summary>
    public ClassroomPreparedSend? GetClassroomPreparedSend(string messageId)
    {
        ValidateMessageId(messageId);
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT a.principal,a.rawSnapshot,a.kind,a.reaction,a.contentFingerprint,a.tryCount,CASE WHEN a.principal GLOB 'ip:*' THEN substr(a.principal,4) ELSE (SELECT r.endpoint FROM RosterEntry r WHERE 'roster:'||r.id=a.principal) END,(SELECT route FROM ClassroomAttemptRoute x WHERE x.messageId=a.messageId AND x.tryCount=a.tryCount) FROM ClassroomAttempt a WHERE a.messageId=@id AND a.attemptState='sending'";
            cmd.Parameters.AddWithValue("@id", messageId); using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(6) || !ClassroomPrincipalKey.TryParse(r.GetString(0), out var principal)) return null;
            return new ClassroomPreparedSend(messageId, principal, r.GetString(6), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                Convert.ToHexString((byte[])r.GetValue(4)).ToLowerInvariant(), checked((int)r.GetInt64(5)), r.IsDBNull(7) ? null : r.GetString(7) == "legacy" ? ClassroomRoute.Legacy : ClassroomRoute.Classroom);
        });
    }

    /// <summary>
    /// <paramref name="ackSourceIp"/> is the remote address of the connection that carried the stored-ACK. The ACK only counts when that address
    /// maps to the same conversation the attempt belongs to (an ACK from some other address is discarded).
    /// </summary>
    public CompleteResult CompleteClassroomAttempt(string messageId, AttemptOutcome outcome, string? ackSourceIp = null, int? expectedTryCount = null)
    {
        ValidateMessageId(messageId); ArgumentNullException.ThrowIfNull(outcome);
        if (expectedTryCount is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        if (outcome is not (RemoteStoredOutcome or UnconfirmedOutcome or FailedOutcome)) throw new ArgumentException("Unsupported attempt outcome.", nameof(outcome));
        byte[]? ack = outcome is RemoteStoredOutcome remote ? remote.AckContentFingerprint?.ToArray() : null;
        var ackKey = ClassroomPrincipalKey.ForSource(ackSourceIp);
        return Write<CompleteResult>("classroom-complete-attempt", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null) return (new Discarded("not-found"), false);
            if (old.State == "superseded") return (new Discarded("superseded"), false);
            if (old.State == "confirmed")
            {
                object? sequence = ctx.Scalar("SELECT localSeq FROM ClassroomMessage WHERE principal=@p AND direction='outgoing' AND messageId=@id", ("@p", old.Principal), ("@id", messageId));
                return sequence is null ? (new Discarded("missing-confirmed-message"), false) : (new AlreadyConfirmed(Convert.ToInt64(sequence, CultureInfo.InvariantCulture)), false);
            }
            if (outcome is not RemoteStoredOutcome)
            {
                if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new Discarded("stale-attempt"), false);
                string state = old.State == "cancelled" ? "cancelled" : outcome is UnconfirmedOutcome ? "unconfirmed" : "failed";
                if (state == old.State) return (new StateUpdated(state), false);
                ctx.Exec("UPDATE ClassroomAttempt SET attemptState=@state,updatedUtc=@now WHERE messageId=@id", ("@state", state), ("@now", ctx.NowUtc), ("@id", messageId));
                return (new StateUpdated(state), true);
            }
            // NCM1e: a stored result is only meaningful for a try that went out on the classroom route (v1 can never produce one).
            if (ClassroomRouteOf(ctx, messageId, anyTry: true, ClassroomRoute.Classroom) is { } routeProblem) return (new Discarded(routeProblem), false);
            // The stored-ACK must come from the conversation's own address, resolved inside this transaction.
            if (ackKey is null || ResolveClassroomPrincipal(ctx, ackKey.Value.SourceIp!).Value != old.Principal) return (new Discarded("ack-binding"), false);
            if (ack is null || ack.Length != 32 || !CryptographicOperations.FixedTimeEquals(ack, old.Fingerprint))
            {
                if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new Discarded("stale-attempt"), false);
                if (old.State != "cancelled") ctx.Exec("UPDATE ClassroomAttempt SET attemptState='unconfirmed',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
                return (new AckMismatch(), old.State != "cancelled");
            }
            var binding = ReadClassroomBinding(ctx, old.Principal);
            bool clear = old.Kind != "reaction" && binding is not null && binding.Epoch == old.Epoch && binding.Revision == old.Revision && binding.Text == old.Text;
            if (clear && binding!.Epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
            ctx.Exec("INSERT INTO ClassroomMessage(principal,direction,messageId,rawText,kind,reaction,contentFingerprint,createdUtc,remoteStoredAt) VALUES(@p,'outgoing',@id,@text,@kind,@reaction,@fp,@now,@now)",
                ("@p", old.Principal), ("@id", old.Id), ("@text", old.Text), ("@kind", old.Kind), ("@reaction", old.Reaction), ("@fp", old.Fingerprint), ("@now", ctx.NowUtc));
            long seq = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='confirmed',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
            if (clear)
            {
                ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", old.Principal));
                ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal=@p", ("@p", old.Principal));
            }
            return (new Confirmed(seq, clear), true);
        });
    }

    /// <summary>Null when the wanted route was recorded (any try for stored results, the current try for legacy); otherwise no-route or route-mismatch.</summary>
    private static string? ClassroomRouteOf(WriteContext ctx, string messageId, bool anyTry, ClassroomRoute wanted, long currentTry = 0)
    {
        string route = wanted == ClassroomRoute.Legacy ? "legacy" : "classroom";
        var rows = ctx.Query("SELECT route FROM ClassroomAttemptRoute WHERE messageId=@id" + (anyTry ? "" : " AND tryCount=@try"), r => r.GetString(0), ("@id", messageId), ("@try", currentTry));
        if (rows.Count == 0) return "no-route";
        return rows.Contains(route) ? null : "route-mismatch";
    }

    /// <summary>
    /// NCM1e: write, once, which route this try takes. <paramref name="helloObservedUtc"/> is when the hello that decided it was answered; it must not
    /// be older than the try (so a hello from before the send, or from history, cannot route it). A try that went out as classroom may not later go
    /// out as v1 for the same message (CM-14); the other direction is allowed (the peer was upgraded).
    /// </summary>
    public ClassroomRouteResult RecordClassroomRoute(string messageId, int tryCount, ClassroomRoute route, DateTime helloObservedUtc)
    {
        ValidateMessageId(messageId); if (tryCount < 1) throw new ArgumentOutOfRangeException(nameof(tryCount)); if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        string wire = route == ClassroomRoute.Legacy ? "legacy" : "classroom";
        string hello = Iso(helloObservedUtc.ToUniversalTime());
        return Write("classroom-record-route", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null) return (new ClassroomRouteResult(false, "not-found"), false);
            if (old.Count != tryCount) return (new ClassroomRouteResult(false, "stale-attempt"), false);
            var existing = ctx.Query("SELECT route FROM ClassroomAttemptRoute WHERE messageId=@id AND tryCount=@try", r => r.GetString(0), ("@id", messageId), ("@try", tryCount));
            if (existing.Count != 0) return (new ClassroomRouteResult(false, existing[0] == wire ? "already-recorded" : "route-conflict"), false);
            if (old.State != "sending") return (new ClassroomRouteResult(false, "not-sending"), false);
            string started = Convert.ToString(ctx.Scalar("SELECT updatedUtc FROM ClassroomAttempt WHERE messageId=@id", ("@id", messageId)), CultureInfo.InvariantCulture)!;
            if (string.CompareOrdinal(hello, started) < 0) return (new ClassroomRouteResult(false, "hello-before-attempt"), false);
            if (string.CompareOrdinal(hello, Iso(_options.UtcNow().ToUniversalTime().AddSeconds(5))) > 0) return (new ClassroomRouteResult(false, "hello-in-future"), false);
            if (route == ClassroomRoute.Legacy && Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomAttemptRoute WHERE messageId=@id AND route='classroom'", ("@id", messageId)), CultureInfo.InvariantCulture) != 0)
                return (new ClassroomRouteResult(false, "classroom-may-have-delivered"), false);
            ctx.Exec("INSERT INTO ClassroomAttemptRoute(messageId,tryCount,route,helloObservedUtc,recordedUtc) VALUES(@id,@try,@route,@hello,@now)",
                ("@id", messageId), ("@try", tryCount), ("@route", wire), ("@hello", hello), ("@now", ctx.NowUtc));
            return (new ClassroomRouteResult(true, "recorded"), true);
        });
    }

    /// <summary>
    /// CM-14b: the v1 peer answered "delivered". Records the message in history as 舊版・已送出 (explicit column; no remoteStoredAt, no remoteReadAt, no receipts ever),
    /// makes the attempt terminal, and clears the draft only if it is unchanged since the send began. v1 carries no content proof, so the only binding is
    /// that the reply came from the conversation's own address. Not a RemoteStoredOutcome: nothing here says the peer stored or read anything.
    /// </summary>
    public CompleteResult CompleteClassroomLegacyDelivered(string messageId, string deliveredFromIp, int? expectedTryCount = null)
    {
        ValidateMessageId(messageId);
        if (expectedTryCount is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        var from = ClassroomPrincipalKey.ForSource(deliveredFromIp);
        return Write<CompleteResult>("classroom-legacy-delivered", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null) return (new Discarded("not-found"), false);
            if (old.State == "superseded") return (new Discarded("superseded"), false);
            if (old.State == "confirmed")
            {
                object? sequence = ctx.Scalar("SELECT localSeq FROM ClassroomMessage WHERE principal=@p AND direction='outgoing' AND messageId=@id", ("@p", old.Principal), ("@id", messageId));
                return sequence is null ? (new Discarded("missing-confirmed-message"), false) : (new AlreadyConfirmed(Convert.ToInt64(sequence, CultureInfo.InvariantCulture)), false);
            }
            if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new Discarded("stale-attempt"), false);
            // NCM1e: delivered is the sync reply of the CURRENT try, which must have been routed to v1 by a hello done after the try began.
            if (ClassroomRouteOf(ctx, messageId, anyTry: false, ClassroomRoute.Legacy, old.Count) is { } legacyProblem) return (new Discarded(legacyProblem), false);
            if (from is null || ResolveClassroomPrincipal(ctx, from.Value.SourceIp!).Value != old.Principal) return (new Discarded("ack-binding"), false);
            var binding = ReadClassroomBinding(ctx, old.Principal);
            bool clear = old.Kind != "reaction" && binding is not null && binding.Epoch == old.Epoch && binding.Revision == old.Revision && binding.Text == old.Text;
            if (clear && binding!.Epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
            ctx.Exec("INSERT INTO ClassroomMessage(principal,direction,messageId,rawText,kind,reaction,contentFingerprint,createdUtc,legacyDeliveredAt) VALUES(@p,'outgoing',@id,@text,@kind,@reaction,@fp,@now,@now)",
                ("@p", old.Principal), ("@id", old.Id), ("@text", old.Text), ("@kind", old.Kind), ("@reaction", old.Reaction), ("@fp", old.Fingerprint), ("@now", ctx.NowUtc));
            long seq = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='confirmed',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
            if (clear)
            {
                ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", old.Principal));
                ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal=@p", ("@p", old.Principal));
            }
            return (new Confirmed(seq, clear), true);
        });
    }

    public CancelResult CancelClassroomAttempt(string messageId, int? expectedTryCount = null)
    {
        ValidateMessageId(messageId);
        if (expectedTryCount is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        return Write("classroom-cancel-attempt", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null) return (new CancelResult(false, "not-found"), false);
            if (old.State is "confirmed" or "superseded") return (new CancelResult(false, "terminal"), false);
            if (expectedTryCount.HasValue && expectedTryCount != old.Count) return (new CancelResult(false, "stale-attempt"), false);
            if (old.State == "cancelled") return (new CancelResult(false, "already-cancelled"), false);
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='cancelled',updatedUtc=@now WHERE messageId=@id", ("@now", ctx.NowUtc), ("@id", messageId));
            return (new CancelResult(true, "cancelled"), true);
        });
    }

    // ---- remote receipts and local read -----------------------------------------------------------------------------------------------

    /// <summary>A read receipt arrived from <paramref name="sourceIp"/> (socket address). It only applies to messages we sent to that same conversation.</summary>
    public ReceiptApplyResult ApplyClassroomReceipt(string sourceIp, string messageId, RemoteReceiptKind kind)
    {
        ValidateMessageId(messageId); if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (ClassroomPrincipalKey.ForSource(sourceIp) is not { } key) return new(false, "invalid-source");
        return Write("classroom-apply-receipt", ctx => {
            var principal = ResolveClassroomPrincipal(ctx, key.SourceIp!);
            var rows = ctx.Query("SELECT localSeq,remoteStoredAt,remoteReadAt,legacyDeliveredAt FROM ClassroomMessage WHERE principal=@p AND direction='outgoing' AND messageId=@id",
                r => (Seq: r.GetInt64(0), Stored: !r.IsDBNull(1), Read: !r.IsDBNull(2), Legacy: !r.IsDBNull(3)), ("@p", principal.Value), ("@id", messageId));
            if (rows.Count == 0)
            {
                long pending = Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomAttempt WHERE principal=@p AND messageId=@id AND attemptState NOT IN ('confirmed','superseded')", ("@p", principal.Value), ("@id", messageId)), CultureInfo.InvariantCulture);
                return (new ReceiptApplyResult(false, pending > 0 ? "pending-local-confirmation" : "not-found"), false);
            }
            var row = rows[0];
            if (row.Legacy) return (new ReceiptApplyResult(false, "legacy-no-receipts", row.Seq), false);   // CM-14b: a v1 peer cannot have stored or read this message
            if (row.Stored && (kind == RemoteReceiptKind.Stored || row.Read)) return (new ReceiptApplyResult(false, "unchanged", row.Seq), false);
            ctx.Exec("UPDATE ClassroomMessage SET remoteStoredAt=coalesce(remoteStoredAt,@now),remoteReadAt=CASE WHEN @read=1 THEN coalesce(remoteReadAt,@now) ELSE remoteReadAt END WHERE localSeq=@seq",
                ("@now", ctx.NowUtc), ("@read", kind == RemoteReceiptKind.Read ? 1 : 0), ("@seq", row.Seq));
            return (new ReceiptApplyResult(true, "applied", row.Seq), true);
        });
    }

    public VisibleReadResult MarkClassroomVisibleRead(ClassroomPrincipalKey principal, IReadOnlyList<long> visibleLocalSeqs, VisibleReadContext context, Func<bool>? visibilityStillCurrent = null)
    {
        RequirePrincipal(principal); ArgumentNullException.ThrowIfNull(visibleLocalSeqs); ArgumentNullException.ThrowIfNull(context);
        if (visibleLocalSeqs.Count > 200) throw new ArgumentOutOfRangeException(nameof(visibleLocalSeqs));
        var ids = visibleLocalSeqs.Distinct().ToArray();
        if (ids.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(visibleLocalSeqs));
        if (context.ActiveConversationId != principal.Value || !context.UserOpened || !context.WindowActive || context.Minimized || !context.ViewportReady) return new(0, 0, "visibility-guard");
        if (ids.Length == 0) return new(0, 0, "empty");
        return Write("classroom-visible-read", ctx => {
            if (visibilityStillCurrent is not null && !visibilityStillCurrent()) return (new VisibleReadResult(0, 0, "stale-view"), false);
            var policy = ReadPolicy(ctx); int marked = 0, queued = 0;
            foreach (long seq in ids)
            {
                if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomMessage WHERE localSeq=@seq AND principal=@p AND direction='incoming' AND localReadAt IS NULL", ("@seq", seq), ("@p", principal.Value)), CultureInfo.InvariantCulture) == 0) continue;
                marked += ctx.Exec("UPDATE ClassroomMessage SET localReadAt=@now WHERE localSeq=@seq AND localReadAt IS NULL", ("@now", ctx.NowUtc), ("@seq", seq));
                if (policy.Enabled)   // D-10: outbound read receipts are opt-in; nothing is queued while off
                    queued += ctx.Exec("INSERT OR IGNORE INTO ClassroomReceiptOutbox(originalLocalSeq,principal,receiptKind,createdUtc) VALUES(@seq,@p,'read',@now)", ("@seq", seq), ("@p", principal.Value), ("@now", ctx.NowUtc));
            }
            return (new VisibleReadResult(marked, queued, marked > 0 ? "marked" : "unchanged"), marked > 0);
        }, guardRejected: () => new VisibleReadResult(0, 0, "stale-view"), transactionGuard: _ => visibilityStillCurrent is null || visibilityStillCurrent());   // also right before COMMIT: a view that went away never leaves a read mark or receipt
    }

    /// <summary>Receipts to send, each with the conversation's CURRENT address; ones without an address are skipped (they stay queued).</summary>
    public IReadOnlyList<PendingClassroomReceipt> ListPendingClassroomReceipts(int limit = 50)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT o.originalLocalSeq,m.messageId,o.principal,t.ip,s.generation FROM ClassroomReceiptOutbox o JOIN ClassroomMessage m ON m.localSeq=o.originalLocalSeq AND m.principal=o.principal AND m.direction='incoming' AND m.localReadAt IS NOT NULL JOIN ReadReceiptPolicy s ON s.singleton=1 AND s.enabled=1 JOIN (SELECT p.principal AS principal,CASE WHEN p.principal GLOB 'ip:*' THEN substr(p.principal,4) ELSE (SELECT r.endpoint FROM RosterEntry r WHERE 'roster:'||r.id=p.principal) END AS ip FROM ClassroomConversation p) t ON t.principal=o.principal AND t.ip IS NOT NULL ORDER BY o.originalLocalSeq LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit); using var r = cmd.ExecuteReader(); var rows = new List<PendingClassroomReceipt>();
            while (r.Read()) if (ClassroomPrincipalKey.TryParse(r.GetString(2), out var key)) rows.Add(new(r.GetInt64(0), r.GetString(1), key, r.GetString(3), r.GetInt64(4)));
            return rows.AsReadOnly();
        });
    }

    /// <summary>After the other side acknowledged the receipt. Returns false when the policy changed or the row is already gone.</summary>
    public bool CompleteClassroomReadReceipt(PendingClassroomReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OriginalLocalSeq <= 0 || receipt.PolicyGeneration < 0 || receipt.Principal.Value is null || !StoreIds.IsValid(receipt.MessageId)) throw new ArgumentException("Invalid receipt token.", nameof(receipt));
        return Write("classroom-complete-receipt", ctx => {
            var policy = ReadPolicy(ctx);
            if (!policy.Enabled || policy.Generation != receipt.PolicyGeneration) return (false, false);
            int deleted = ctx.Exec("DELETE FROM ClassroomReceiptOutbox WHERE originalLocalSeq=@seq AND principal=@p AND EXISTS(SELECT 1 FROM ClassroomMessage m WHERE m.localSeq=ClassroomReceiptOutbox.originalLocalSeq AND m.principal=@p AND m.direction='incoming' AND m.messageId=@id AND m.localReadAt IS NOT NULL)",
                ("@seq", receipt.OriginalLocalSeq), ("@p", receipt.Principal.Value), ("@id", receipt.MessageId));
            return (deleted != 0, deleted != 0);
        });
    }

    // ---- reading history --------------------------------------------------------------------------------------------------------------

    public ClassroomMessagePage ListClassroomMessages(ClassroomPrincipalKey principal, long? beforeLocalSeq = null, int pageSize = 50)
    {
        RequirePrincipal(principal);
        if (beforeLocalSeq is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeLocalSeq));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT localSeq,direction,messageId,rawText,kind,reaction,sourceIp,createdUtc,localReadAt,remoteStoredAt,remoteReadAt,legacyDeliveredAt FROM ClassroomMessage WHERE principal=@p AND (@before IS NULL OR localSeq<@before) ORDER BY localSeq DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@p", principal.Value); cmd.Parameters.AddWithValue("@before", (object?)beforeLocalSeq ?? DBNull.Value); cmd.Parameters.AddWithValue("@limit", pageSize + 1);
            using var r = cmd.ExecuteReader(); var rows = new List<ClassroomStoredMessage>();
            while (r.Read()) rows.Add(new(r.GetInt64(0), principal, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11)));
            bool more = rows.Count > pageSize; if (more) rows.RemoveAt(rows.Count - 1); rows.Reverse();
            return new ClassroomMessagePage(rows.AsReadOnly(), more ? rows[0].LocalSeq : null);
        });
    }

    /// <summary>One row per conversation that has messages, newest first. Names for roster principals come from the roster; ip principals show as 名單外・IP.</summary>
    public IReadOnlyList<ClassroomConversationSummary> ListClassroomConversations(int limit = 200)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "WITH g AS (SELECT principal,max(localSeq) AS lastSeq,count(*) AS total,sum(CASE WHEN direction='incoming' AND localReadAt IS NULL THEN 1 ELSE 0 END) AS unread FROM ClassroomMessage GROUP BY principal) SELECT g.principal,g.lastSeq,g.total,g.unread,m.rawText,m.createdUtc,r.displayName,d.displayName FROM g JOIN ClassroomMessage m ON m.localSeq=g.lastSeq LEFT JOIN RosterEntry r ON 'roster:'||r.id=g.principal LEFT JOIN RosterRemoved d ON 'roster:'||d.rosterEntryId=g.principal ORDER BY g.lastSeq DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit); using var r = cmd.ExecuteReader(); var rows = new List<ClassroomConversationSummary>();
            while (r.Read()) if (ClassroomPrincipalKey.TryParse(r.GetString(0), out var key)) rows.Add(new(key, r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? r.IsDBNull(7) ? null : r.GetString(7) : r.GetString(6), key.IsRoster && r.IsDBNull(6)));
            return rows.AsReadOnly();
        });
    }

    /// <summary>
    /// Human-confirmed local clear (D-12): messages, draft, attempts and unsent receipts go in ONE transaction; identity and settings stay.
    /// Cleared message ids are remembered so a late resend recreates nothing. The draft epoch moves forward.
    /// </summary>
    public ClassroomClearResult ClearClassroomConversation(ClassroomPrincipalKey principal)
    {
        RequirePrincipal(principal);
        lock (_gate)
        {
            var result = Write<ClassroomClearResult?>("classroom-clear", ctx => {
                object? epochValue = ctx.Scalar("SELECT draftEpoch FROM ClassroomConversation WHERE principal=@p", ("@p", principal.Value));
                long epoch = epochValue is null ? -1 : Convert.ToInt64(epochValue, CultureInfo.InvariantCulture);
                if (epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
                int legacy = principal.IsRoster ? ClearLegacyScope(ctx, new(principal.RosterEntryId, null)) : ClearLegacyScope(ctx, new(null, principal.SourceIp));
                if (epochValue is null) return (legacy == 0 ? null : new ClassroomClearResult(0, 0, 0, 0, 0, legacy), legacy != 0);
                ctx.Exec("INSERT OR IGNORE INTO ClassroomCleared(principal,direction,messageId,clearedUtc) SELECT principal,direction,messageId,@now FROM ClassroomMessage WHERE principal=@p", ("@now", ctx.NowUtc), ("@p", principal.Value));
                ctx.Exec("INSERT OR IGNORE INTO ClassroomCleared(principal,direction,messageId,clearedUtc) SELECT principal,'outgoing',messageId,@now FROM ClassroomAttempt WHERE principal=@p", ("@now", ctx.NowUtc), ("@p", principal.Value));
                int receipts = ctx.Exec("DELETE FROM ClassroomReceiptOutbox WHERE principal=@p", ("@p", principal.Value));
                int attempts = ctx.Exec("DELETE FROM ClassroomAttempt WHERE principal=@p", ("@p", principal.Value));
                int drafts = ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", principal.Value));
                int messages = ctx.Exec("DELETE FROM ClassroomMessage WHERE principal=@p", ("@p", principal.Value));
                ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal=@p", ("@p", principal.Value));
                return (new ClassroomClearResult(messages, drafts, attempts, receipts, epoch + 1, legacy), true);
            });
            if (result is null) return new(0, 0, 0, 0, 0, 0);
            CheckpointLocked(); CheckWalSize("classroom-clear");
            return result;
        }
    }
}
