using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const int InboxSnippetRunes = 200;

    /// <summary>
    /// One read-only, materialized snapshot (a single SELECT; no write, no migration, no Peer or security interpretation) of every classroom conversation:
    /// all roster rows (even with no history), 名單外 sources that have a message, draft, actionable attempt or v1 history, and conversations of
    /// deleted roster rows (readable, CanSend=false). Order: roster rows as in the roster list, then deleted-row conversations, then 名單外 by recent activity.
    /// Nothing here authorises sending: every send still needs a fresh hello and a recorded route.
    /// </summary>
    public ClassroomInboxSnapshot GetClassroomInboxSnapshot() => Read(c => {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            WITH subjects(principal,kind,rosterId,legacyRow,legacyIp,displayName,studentNumber,isTeacher,endpoint) AS (
              SELECT 'roster:'||r.id,'roster',r.id,r.id,NULL,coalesce(r.displayName,'未命名同學'),r.studentNumber,r.isTeacher,r.endpoint FROM RosterEntry r
              UNION ALL
              SELECT cc.principal,'removed',substr(cc.principal,8),NULL,NULL,d.displayName,NULL,0,NULL FROM ClassroomConversation cc LEFT JOIN RosterRemoved d ON d.rosterEntryId=substr(cc.principal,8)
                WHERE cc.principal GLOB 'roster:*' AND NOT EXISTS(SELECT 1 FROM RosterEntry r WHERE 'roster:'||r.id=cc.principal)
              UNION ALL
              SELECT 'ip:'||u.ip,'ip',NULL,NULL,u.ip,NULL,NULL,0,u.ip FROM (SELECT substr(principal,4) AS ip FROM ClassroomConversation WHERE principal GLOB 'ip:*'
                UNION SELECT sourceIp FROM LegacyMessage WHERE rosterEntryId IS NULL) u
            )
            SELECT s.principal,s.kind,s.rosterId,s.displayName,s.studentNumber,s.isTeacher,s.endpoint,
              (SELECT count(*) FROM ClassroomMessage x WHERE x.principal=s.principal AND x.direction='incoming' AND x.localReadAt IS NULL),
              (SELECT count(*) FROM LegacyMessage x WHERE x.localReadAt IS NULL AND ((s.legacyRow IS NOT NULL AND x.rosterEntryId=s.legacyRow) OR (s.legacyIp IS NOT NULL AND x.rosterEntryId IS NULL AND x.sourceIp=s.legacyIp))),
              m.rawText,m.createdUtc,l.rawText,l.receivedUtc,
              d.rawText,coalesce(cc.draftEpoch,0),coalesce(cc.draftRevision,0),
              a.messageId,a.attemptState,a.rawSnapshot,a.tryCount,a.draftEpoch,a.draftRevision,
              (SELECT route FROM ClassroomAttemptRoute rt WHERE rt.messageId=a.messageId ORDER BY rt.tryCount DESC LIMIT 1),
              s.legacyRow,s.legacyIp
            FROM subjects s
              LEFT JOIN ClassroomConversation cc ON cc.principal=s.principal
              LEFT JOIN ClassroomDraft d ON d.principal=s.principal
              LEFT JOIN ClassroomMessage m ON m.localSeq=(SELECT x.localSeq FROM ClassroomMessage x WHERE x.principal=s.principal ORDER BY x.localSeq DESC LIMIT 1)
              LEFT JOIN LegacyMessage l ON l.legacySeq=(SELECT x.legacySeq FROM LegacyMessage x WHERE (s.legacyRow IS NOT NULL AND x.rosterEntryId=s.legacyRow) OR (s.legacyIp IS NOT NULL AND x.rosterEntryId IS NULL AND x.sourceIp=s.legacyIp) ORDER BY x.legacySeq DESC LIMIT 1)
              LEFT JOIN ClassroomAttempt a ON a.messageId=(SELECT x.messageId FROM ClassroomAttempt x WHERE x.principal=s.principal AND x.attemptState IN ('sending','unconfirmed','failed','cancelled') ORDER BY x.updatedUtc DESC,x.rowid DESC LIMIT 1)
            WHERE s.kind='roster' OR m.localSeq IS NOT NULL OR l.legacySeq IS NOT NULL OR d.principal IS NOT NULL OR a.messageId IS NOT NULL
            """;
        using var r = cmd.ExecuteReader(); var rows = new List<(ClassroomInboxEntry Entry, string Kind)>();
        while (r.Read())
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            if (!ClassroomPrincipalKey.TryParse(r.GetString(0), out var principal)) continue;
            string kind = r.GetString(1); bool removed = kind == "removed";
            string? classroomText = Text(9), classroomUtc = Text(10), legacyText = Text(11), legacyUtc = Text(12);
            bool legacyNewer = legacyUtc is not null && (classroomUtc is null || string.CompareOrdinal(legacyUtc, classroomUtc) >= 0);
            string? latest = legacyNewer ? legacyText : classroomText;
            if (latest is not null && latest.EnumerateRunes().Count() > InboxSnippetRunes) latest = string.Concat(latest.EnumerateRunes().Take(InboxSnippetRunes).Select(x => x.ToString())) + "…";
            ClassroomRoute? route = Text(22) is { } wire ? wire == "legacy" ? ClassroomRoute.Legacy : ClassroomRoute.Classroom : null;
            var attempt = Text(16) is null ? null : new ClassroomAttemptSummary(r.GetString(16), r.GetString(17), r.GetString(18), checked((int)r.GetInt64(19)), r.GetInt64(20), r.GetInt64(21), route);
            string? endpoint = Text(6);
            bool canSend = !removed && endpoint is not null;
            LegacyHistoryScope? scope = Text(23) is { } row ? new(row, null) : Text(24) is { } ip ? new(null, ip) : null;
            rows.Add((new ClassroomInboxEntry(principal, kind == "roster" ? Text(2) : removed ? Text(2) : null, Text(3), Text(4), r.GetInt64(5) != 0, removed, endpoint, canSend,
                r.GetInt64(7), r.GetInt64(8), latest, legacyNewer ? legacyUtc : classroomUtc, legacyNewer,
                new DraftState(Text(13), r.GetInt64(14), r.GetInt64(15)), attempt, scope), kind));
        }
        static string? StudentKey(ClassroomInboxEntry e) => e.StudentNumber?.TrimStart('0') is { } number ? number.Length == 0 ? "0" : number : null;
        var roster = rows.Where(x => x.Kind == "roster").Select(x => x.Entry).OrderByDescending(e => e.IsTeacher).ThenBy(e => StudentKey(e) is null).ThenBy(e => StudentKey(e)?.Length ?? 0)
            .ThenBy(StudentKey, StringComparer.Ordinal).ThenBy(e => e.RosterEntryId, StringComparer.Ordinal);
        var deleted = rows.Where(x => x.Kind == "removed").Select(x => x.Entry).OrderBy(e => e.DisplayName, StringComparer.Ordinal).ThenBy(e => e.Principal.Value, StringComparer.Ordinal);
        var unknown = rows.Where(x => x.Kind == "ip").Select(x => x.Entry).OrderByDescending(e => e.LatestUtc ?? "", StringComparer.Ordinal).ThenBy(e => e.Principal.Value, StringComparer.Ordinal);
        return new ClassroomInboxSnapshot(roster.Concat(deleted).Concat(unknown));
    });
}
