using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const string LegacyCaptureSql = "SELECT r.id,rs.revision,coalesce(s.revision,0) FROM (SELECT @ip AS ip) a LEFT JOIN RosterEntry r ON r.endpoint=a.ip LEFT JOIN RosterSecurityRevision rs ON rs.rosterId=r.id LEFT JOIN LegacyScopeRevision s ON s.scopeKey=CASE WHEN r.id IS NULL THEN 'legacy-ip:'||a.ip ELSE 'legacy-row:'||r.id END";
    private const string LegacyScopeWhere = "((@row IS NOT NULL AND rosterEntryId=@row) OR (@ip IS NOT NULL AND rosterEntryId IS NULL AND sourceIp=@ip))";
    private static LegacyReceiveContext MapLegacyCapture(SqliteDataReader r, string ip) => new(ip, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2));
    private static void ValidateLegacyIp(string ip) { if (!LanInvitation.PrivateIPv4(ip)) throw new ArgumentException("Invalid legacy LAN source.", nameof(ip)); }
    private static void ValidateLegacyScope(LegacyHistoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if ((scope.RosterEntryId is null) == (scope.UnknownSourceIp is null) || scope.RosterEntryId is { } row && !StoreIds.IsUuid(row)) throw new ArgumentException("Invalid legacy scope.");
        if (scope.UnknownSourceIp is { } ip) ValidateLegacyIp(ip);
    }
    public LegacyReceiveContext GetLegacyReceiveContext(string sourceIp)
    {
        ValidateLegacyIp(sourceIp);
        return Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = LegacyCaptureSql; cmd.Parameters.AddWithValue("@ip", sourceIp); using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new StoreException(StoreErrorKind.Corrupt, "Legacy context is missing."); return MapLegacyCapture(r, sourceIp); });
    }
    public LegacyReceiveResult ReceiveLegacyMessage(DirectMessage message, LegacyReceiveContext expected, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent); ValidateLegacyIp(expected.SourceIp);
        if (!Protocol.ValidText(message.Text)) return new(false, "invalid-text");
        try
        {
            var utf8 = new UTF8Encoding(false, true); _ = utf8.GetByteCount(message.Text);
            if (message.Reaction is { } reaction && utf8.GetByteCount(reaction) > LegacyDirectProtocol.MaxBodyBytes) return new(false, "invalid-reaction");
        }
        catch (EncoderFallbackException) { return new(false, "invalid-unicode"); }
        bool Guard(WriteContext ctx) => stillCurrent() && ctx.Query(LegacyCaptureSql, r => MapLegacyCapture(r, expected.SourceIp), ("@ip", expected.SourceIp)).Single() == expected;
        string? limit = null;
        var legacyResult = Write("legacy-receive", ctx => {
            // CM-11: v1 messages from sources that are not on the roster share the classroom off-roster budget; refuse the new one, never delete.
            if (expected.RosterEntryId is null && OffRosterLimit(ctx, expected.SourceIp, Encoding.UTF8.GetByteCount(message.Text)) is { } hit)
            { limit = hit; return (new LegacyReceiveResult(false, "offroster-limit"), false); }
            ctx.Exec("INSERT OR IGNORE INTO LegacyScopeRevision(scopeKey) VALUES(@scope)", ("@scope", expected.Scope.ContextId));
            ctx.Exec("INSERT INTO LegacyMessage(scopeKey,sourceIp,rosterEntryId,rawText,reaction,receivedUtc) VALUES(@scope,@ip,@row,@text,@reaction,@now)",
                ("@scope", expected.Scope.ContextId), ("@ip", expected.SourceIp), ("@row", expected.RosterEntryId), ("@text", message.Text), ("@reaction", message.Reaction), ("@now", ctx.NowUtc));
            return (new LegacyReceiveResult(true, "stored-local-only", Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture)), true);
        }, guardRejected: () => new LegacyReceiveResult(false, "stale-legacy-scope"), transactionGuard: Guard);
        if (limit is not null) _options.DiagnosticSink?.Invoke("classroom-offroster-limit scope=" + limit);
        return legacyResult;
    }
    public IReadOnlyList<LegacyHistorySummary> ListLegacyHistories(long? beforeLastLegacySeq = null, int limit = 200)
    {
        if (beforeLastLegacySeq is <= 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = "WITH g AS (SELECT rosterEntryId,CASE WHEN rosterEntryId IS NULL THEN sourceIp END AS unknownIp,max(legacySeq) AS lastSeq,count(*) AS total,sum(CASE WHEN localReadAt IS NULL THEN 1 ELSE 0 END) AS unread FROM LegacyMessage GROUP BY rosterEntryId,unknownIp) SELECT g.rosterEntryId,g.unknownIp,g.lastSeq,g.total,g.unread,m.rawText,m.reaction,m.receivedUtc FROM g JOIN LegacyMessage m ON m.legacySeq=g.lastSeq WHERE @before IS NULL OR g.lastSeq<@before ORDER BY g.lastSeq DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@before", (object?)beforeLastLegacySeq ?? DBNull.Value); cmd.Parameters.AddWithValue("@limit", limit); using var r = cmd.ExecuteReader(); var rows = new List<LegacyHistorySummary>();
            while (r.Read()) rows.Add(new(new(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));
            return rows.AsReadOnly();
        });
    }
    public LegacyMessagePage ListLegacyMessages(LegacyHistoryScope scope, long? beforeLegacySeq = null, int pageSize = 50)
    {
        ValidateLegacyScope(scope); if (beforeLegacySeq is <= 0 || pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT legacySeq,sourceIp,rosterEntryId,rawText,reaction,receivedUtc,localReadAt FROM LegacyMessage WHERE " + LegacyScopeWhere + " AND (@before IS NULL OR legacySeq<@before) ORDER BY legacySeq DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@row", (object?)scope.RosterEntryId ?? DBNull.Value); cmd.Parameters.AddWithValue("@ip", (object?)scope.UnknownSourceIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@before", (object?)beforeLegacySeq ?? DBNull.Value); cmd.Parameters.AddWithValue("@limit", pageSize + 1); using var r = cmd.ExecuteReader(); var rows = new List<LegacyStoredMessage>();
            while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
            bool more = rows.Count > pageSize; if (more) rows.RemoveAt(rows.Count - 1); rows.Reverse();
            return new LegacyMessagePage(rows.AsReadOnly(), more ? rows[0].LegacySeq : null);
        });
    }
    public VisibleReadResult MarkLegacyVisibleRead(LegacyHistoryScope scope, IReadOnlyList<long> visibleLegacySeqs, VisibleReadContext context, Func<bool> stillCurrent)
    {
        ValidateLegacyScope(scope); ArgumentNullException.ThrowIfNull(visibleLegacySeqs); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (visibleLegacySeqs.Count > 200 || visibleLegacySeqs.Any(seq => seq <= 0)) throw new ArgumentOutOfRangeException(nameof(visibleLegacySeqs));
        if (context.ActiveConversationId != scope.ContextId || !context.UserOpened || !context.WindowActive || context.Minimized || !context.ViewportReady) return new(0, 0, "visibility-guard");
        return Write("legacy-visible-read", ctx => {
            int marked = 0;
            foreach (long seq in visibleLegacySeqs.Distinct()) marked += ctx.Exec("UPDATE LegacyMessage SET localReadAt=@now WHERE legacySeq=@seq AND localReadAt IS NULL AND " + LegacyScopeWhere,
                ("@now", ctx.NowUtc), ("@seq", seq), ("@row", scope.RosterEntryId), ("@ip", scope.UnknownSourceIp));
            return (new VisibleReadResult(marked, 0, marked > 0 ? "marked" : "unchanged"), marked > 0);
        }, guardRejected: () => new(0, 0, "stale-view"), transactionGuard: _ => stillCurrent());
    }
    public LegacyClearResult ClearLegacyHistory(LegacyHistoryScope scope)
    {
        ValidateLegacyScope(scope);
        lock (_gate)
        {
            int count = Write("legacy-clear", ctx => (ClearLegacyScope(ctx, scope), true));
            bool truncated = CheckpointLocked(); CheckWalSize("legacy-clear"); return new(count, truncated);
        }
    }
    private static int ClearLegacyScope(WriteContext ctx, LegacyHistoryScope scope)
    {
        var keys = ctx.Query("SELECT DISTINCT scopeKey FROM LegacyMessage WHERE " + LegacyScopeWhere, r => r.GetString(0), ("@row", scope.RosterEntryId), ("@ip", scope.UnknownSourceIp));
        foreach (string key in keys.Append(scope.ContextId).Distinct(StringComparer.Ordinal))
            ctx.Exec("INSERT INTO LegacyScopeRevision(scopeKey,revision) VALUES(@key,1) ON CONFLICT(scopeKey) DO UPDATE SET revision=revision+1", ("@key", key));
        return ctx.Exec("DELETE FROM LegacyMessage WHERE " + LegacyScopeWhere, ("@row", scope.RosterEntryId), ("@ip", scope.UnknownSourceIp));
    }
    private static int ClearLegacyForConversation(WriteContext ctx, string conversation)
    {
        var rows = ctx.Query("SELECT r.id FROM RosterEntry r JOIN Conversation c ON c.peerId=r.peerId WHERE c.id=@conv", r => r.GetString(0), ("@conv", conversation));
        int cleared = 0; foreach (string row in rows) cleared += ClearLegacyScope(ctx, new(row, null)); return cleared;
    }
}
