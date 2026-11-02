namespace NekoChat.Core.Storage;

internal static class LegacyMessageSchema
{
    internal const string Sql = """
        CREATE TABLE LegacyScopeRevision(
          scopeKey TEXT PRIMARY KEY CHECK(length(scopeKey) BETWEEN 1 AND 96),
          revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0)
        ) STRICT;
        CREATE TRIGGER LegacyScopeRevision_no_delete BEFORE DELETE ON LegacyScopeRevision
        BEGIN SELECT RAISE(ABORT,'Legacy clear revisions cannot be deleted'); END;
        CREATE TRIGGER LegacyScopeRevision_monotonic BEFORE UPDATE OF revision ON LegacyScopeRevision WHEN NEW.revision<OLD.revision
        BEGIN SELECT RAISE(ABORT,'Legacy clear revisions cannot decrease'); END;
        CREATE TRIGGER LegacyScopeRevision_key BEFORE UPDATE OF scopeKey ON LegacyScopeRevision WHEN NEW.scopeKey IS NOT OLD.scopeKey
        BEGIN SELECT RAISE(ABORT,'Legacy scope keys are immutable'); END;
        CREATE TABLE LegacyMessage(
          legacySeq INTEGER PRIMARY KEY AUTOINCREMENT,
          scopeKey TEXT NOT NULL REFERENCES LegacyScopeRevision(scopeKey) ON DELETE RESTRICT,
          sourceIp TEXT NOT NULL CHECK(length(sourceIp) BETWEEN 7 AND 15),
          rosterEntryId TEXT REFERENCES RosterEntry(id) ON DELETE SET NULL,
          rawText TEXT NOT NULL CHECK(length(CAST(rawText AS BLOB))>0),
          reaction TEXT,
          receivedUtc TEXT NOT NULL,
          localReadAt TEXT
        ) STRICT;
        CREATE INDEX LegacyMessage_roster_seq ON LegacyMessage(rosterEntryId,legacySeq DESC);
        CREATE INDEX LegacyMessage_unknown_seq ON LegacyMessage(sourceIp,legacySeq DESC) WHERE rosterEntryId IS NULL;
        """;
}
