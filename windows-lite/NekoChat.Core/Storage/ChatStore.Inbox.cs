namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    /// <summary>One materialized SELECT snapshot. No implicit read marks, trust, drafts or history writes.</summary>
    public InboxSnapshot GetInboxSnapshot() => Read(c => {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            WITH subjects(contextId,rosterId,legacyRow,legacyIp,displayName,studentNumber,isTeacher,endpoint,peerId) AS (
              SELECT 'row:'||r.id,r.id,r.id,NULL,coalesce(r.displayName,'未命名同學'),r.studentNumber,r.isTeacher,r.endpoint,r.peerId FROM RosterEntry r
              UNION ALL
              SELECT 'peer:'||p.id,NULL,NULL,NULL,'未命名同學',NULL,0,p.lastSeenEndpoint,p.id FROM Peer p
                WHERE NOT EXISTS(SELECT 1 FROM RosterEntry r WHERE r.peerId=p.id)
              UNION ALL
              SELECT 'legacy-ip:'||m.sourceIp,NULL,NULL,m.sourceIp,'未驗證來源 '||m.sourceIp,NULL,0,m.sourceIp,NULL FROM LegacyMessage m
                WHERE m.rosterEntryId IS NULL GROUP BY m.sourceIp
            )
            SELECT s.contextId,s.rosterId,s.displayName,s.studentNumber,s.isTeacher,s.endpoint,s.peerId,c.id,p.pairingState,
              (SELECT count(*) FROM Message x WHERE x.conversationId=c.id AND x.direction='incoming' AND x.localReadAt IS NULL),
              (SELECT count(*) FROM LegacyMessage x WHERE (x.rosterEntryId=s.legacyRow OR (x.rosterEntryId IS NULL AND x.sourceIp=s.legacyIp)) AND x.localReadAt IS NULL),
              m.rawText,m.createdUtc,l.rawText,l.receivedUtc,d.rawText,c.draftEpoch,c.draftRevision,
              a.messageId,a.attemptState,a.rawSnapshot,a.tryCount,a.draftEpoch,a.draftRevision,s.legacyRow,s.legacyIp
            FROM subjects s LEFT JOIN Peer p ON p.id=s.peerId LEFT JOIN Conversation c ON c.peerId=s.peerId
              LEFT JOIN Draft d ON d.conversationId=c.id
              LEFT JOIN Message m ON m.localSeq=(SELECT x.localSeq FROM Message x WHERE x.conversationId=c.id ORDER BY x.localSeq DESC LIMIT 1)
              LEFT JOIN LegacyMessage l ON l.legacySeq=(SELECT x.legacySeq FROM LegacyMessage x WHERE x.rosterEntryId=s.legacyRow OR (x.rosterEntryId IS NULL AND x.sourceIp=s.legacyIp) ORDER BY x.legacySeq DESC LIMIT 1)
              LEFT JOIN ManualSendAttempt a ON a.messageId=(SELECT x.messageId FROM ManualSendAttempt x WHERE x.conversationId=c.id ORDER BY x.rowid DESC LIMIT 1) AND a.attemptState IN ('sending','unconfirmed','failed','cancelled')
            """;
        using var r = cmd.ExecuteReader(); var rows = new List<InboxEntry>();
        while (r.Read())
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            string? trustedUtc = Text(12), legacyUtc = Text(14);
            bool legacy = legacyUtc is not null && (trustedUtc is null || string.CompareOrdinal(legacyUtc, trustedUtc) >= 0);
            PairingState? pairing = Text(8) is { } value && PairingStates.TryParse(value, out var state) ? state : null;
            rows.Add(new(r.GetString(0), Text(1), r.GetString(2), Text(3), r.GetInt64(4) != 0, Text(5), Text(6), Text(7), pairing,
                r.GetInt64(9), r.GetInt64(10), legacy ? Text(13) : Text(11), legacy ? legacyUtc : trustedUtc, legacy,
                r.IsDBNull(7) ? null : new(Text(15), r.GetInt64(16), r.GetInt64(17)),
                r.IsDBNull(18) ? null : new(r.GetString(18), r.GetString(19), r.GetString(20), checked((int)r.GetInt64(21)), r.GetInt64(22), r.GetInt64(23)),
                Text(24) is { } legacyRow ? new(legacyRow, null) : Text(25) is { } ip ? new(null, ip) : null));
        }
        static string? StudentKey(InboxEntry e) => e.StudentNumber?.TrimStart('0') is { } number ? number.Length == 0 ? "0" : number : null;
        return new InboxSnapshot(rows.OrderByDescending(e => e.IsTeacher).ThenBy(e => StudentKey(e) is null).ThenBy(e => StudentKey(e)?.Length ?? 0)
            .ThenBy(StudentKey, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal));
    });
}
