namespace NekoChat.Core.Storage;

/// <summary>
/// Additive migration 7 (CM-10). Frozen migration 1 and every older table are untouched: old key-based history stays where it is and
/// is never reassigned to a roster row. Classroom conversations are keyed by principal, not by Peer, so no Peer row is needed
/// (and no RegisterPeer(...Paired) shortcut exists).
/// </summary>
internal static class ClassroomSchema
{
    /// <summary>
    /// CM-14b: a v1 peer's "delivered" is recorded by an explicit column, never inferred from remoteStoredAt being null (an unconfirmed send is null too).
    /// Such a message has no stored/read timestamps and can never receive any.
    /// </summary>
    internal const string LegacyDeliveredSql = """
        ALTER TABLE ClassroomMessage ADD COLUMN legacyDeliveredAt TEXT;
        CREATE TRIGGER ClassroomMessage_legacy_insert BEFORE INSERT ON ClassroomMessage
        WHEN NEW.legacyDeliveredAt IS NOT NULL AND (NEW.direction <> 'outgoing' OR NEW.remoteStoredAt IS NOT NULL OR NEW.remoteReadAt IS NOT NULL)
        BEGIN SELECT RAISE(ABORT, 'A legacy-delivered message is outgoing and has no stored/read timestamps'); END;
        CREATE TRIGGER ClassroomMessage_legacy_immutable BEFORE UPDATE OF legacyDeliveredAt ON ClassroomMessage
        BEGIN SELECT RAISE(ABORT, 'legacyDeliveredAt is write-once at insert'); END;
        CREATE TRIGGER ClassroomMessage_legacy_no_receipt BEFORE UPDATE OF remoteStoredAt, remoteReadAt ON ClassroomMessage
        WHEN OLD.legacyDeliveredAt IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'A legacy-delivered message never gets stored/read receipts'); END;
        """;

    /// <summary>CM-17: the name a roster row had when it was removed, so its conversation keeps a label. Written in the same transaction as the delete.</summary>
    internal const string RemovedSql = """
        CREATE TABLE RosterRemoved(
          rosterEntryId TEXT PRIMARY KEY CHECK (length(rosterEntryId) BETWEEN 1 AND 64),
          displayName   TEXT NOT NULL CHECK (length(displayName) BETWEEN 1 AND 256),
          removedUtc    TEXT NOT NULL
        ) STRICT;
        CREATE TRIGGER RosterRemoved_immutable BEFORE UPDATE ON RosterRemoved
        BEGIN SELECT RAISE(ABORT, 'Removed roster snapshots are immutable'); END;
        """;

    internal const string Sql = """
        CREATE TABLE ClassroomConversation(
          principal     TEXT PRIMARY KEY CHECK (length(principal) BETWEEN 4 AND 80 AND (principal GLOB 'roster:*' OR principal GLOB 'ip:*')),
          draftEpoch    INTEGER NOT NULL DEFAULT 0 CHECK (draftEpoch >= 0),
          draftRevision INTEGER NOT NULL DEFAULT 0 CHECK (draftRevision >= 0),
          createdUtc    TEXT NOT NULL
        ) STRICT;
        CREATE TRIGGER ClassroomConversation_draftguard_monotonic
        BEFORE UPDATE OF draftEpoch, draftRevision ON ClassroomConversation
        WHEN NEW.draftEpoch < OLD.draftEpoch OR NEW.draftRevision < OLD.draftRevision
        BEGIN SELECT RAISE(ABORT, 'Draft guard must not move backwards'); END;
        CREATE TRIGGER ClassroomConversation_no_delete BEFORE DELETE ON ClassroomConversation
        BEGIN SELECT RAISE(ABORT, 'Classroom conversation rows are never deleted (draft guard lives here)'); END;
        CREATE TRIGGER ClassroomConversation_key BEFORE UPDATE OF principal ON ClassroomConversation
        BEGIN SELECT RAISE(ABORT, 'Classroom principal is immutable'); END;

        CREATE TABLE ClassroomMessage(
          localSeq           INTEGER PRIMARY KEY AUTOINCREMENT,
          principal          TEXT NOT NULL REFERENCES ClassroomConversation(principal) ON DELETE RESTRICT,
          direction          TEXT NOT NULL CHECK (direction IN ('incoming','outgoing')),
          messageId          TEXT NOT NULL CHECK (length(messageId) BETWEEN 1 AND 64),
          rawText            TEXT NOT NULL CHECK (length(CAST(rawText AS BLOB)) > 0),
          kind               TEXT NOT NULL DEFAULT 'text' CHECK (length(kind) > 0),
          reaction           TEXT,
          contentFingerprint BLOB NOT NULL CHECK (length(contentFingerprint) = 32),
          sourceIp           TEXT,
          createdUtc         TEXT NOT NULL,
          localReadAt        TEXT,
          remoteStoredAt     TEXT,
          remoteReadAt       TEXT,
          UNIQUE (principal, direction, messageId),
          CHECK (direction = 'outgoing' OR (remoteStoredAt IS NULL AND remoteReadAt IS NULL)),
          CHECK (direction = 'incoming' OR localReadAt IS NULL)
        ) STRICT;
        CREATE INDEX ClassroomMessage_conv_seq ON ClassroomMessage(principal, localSeq);
        CREATE INDEX ClassroomMessage_unread ON ClassroomMessage(principal) WHERE direction = 'incoming' AND localReadAt IS NULL;
        CREATE TRIGGER ClassroomMessage_immutable
        BEFORE UPDATE OF localSeq, direction, messageId, rawText, kind, reaction, contentFingerprint, sourceIp, createdUtc ON ClassroomMessage
        BEGIN SELECT RAISE(ABORT, 'Classroom message content is immutable'); END;
        CREATE TRIGGER ClassroomMessage_set_once
        BEFORE UPDATE OF localReadAt, remoteStoredAt, remoteReadAt ON ClassroomMessage
        WHEN (OLD.localReadAt IS NOT NULL AND NEW.localReadAt IS NOT OLD.localReadAt)
          OR (OLD.remoteStoredAt IS NOT NULL AND NEW.remoteStoredAt IS NOT OLD.remoteStoredAt)
          OR (OLD.remoteReadAt IS NOT NULL AND NEW.remoteReadAt IS NOT OLD.remoteReadAt)
        BEGIN SELECT RAISE(ABORT, 'Classroom message timestamps are set-once'); END;

        CREATE TABLE ClassroomDraft(
          principal  TEXT PRIMARY KEY REFERENCES ClassroomConversation(principal) ON DELETE RESTRICT,
          rawText    TEXT NOT NULL CHECK (length(CAST(rawText AS BLOB)) > 0),
          updatedUtc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE ClassroomAttempt(
          messageId          TEXT PRIMARY KEY CHECK (length(messageId) BETWEEN 1 AND 64),
          principal          TEXT NOT NULL REFERENCES ClassroomConversation(principal) ON DELETE RESTRICT,
          rawSnapshot        TEXT NOT NULL CHECK (length(CAST(rawSnapshot AS BLOB)) > 0),
          kind               TEXT NOT NULL DEFAULT 'text' CHECK (length(kind) > 0),
          reaction           TEXT,
          contentFingerprint BLOB NOT NULL CHECK (length(contentFingerprint) = 32),
          draftEpoch         INTEGER NOT NULL CHECK (draftEpoch >= 0),
          draftRevision      INTEGER NOT NULL CHECK (draftRevision >= 0),
          attemptState       TEXT NOT NULL CHECK (attemptState IN ('sending','unconfirmed','failed','cancelled','confirmed','superseded')),
          tryCount           INTEGER NOT NULL DEFAULT 1 CHECK (tryCount >= 1),
          createdUtc         TEXT NOT NULL,
          updatedUtc         TEXT NOT NULL
        ) STRICT;
        CREATE UNIQUE INDEX ClassroomAttempt_one_sending ON ClassroomAttempt(principal) WHERE attemptState = 'sending';
        CREATE TRIGGER ClassroomAttempt_immutable
        BEFORE UPDATE OF messageId, rawSnapshot, kind, reaction, contentFingerprint, createdUtc ON ClassroomAttempt
        BEGIN SELECT RAISE(ABORT, 'Classroom attempt snapshot is immutable'); END;
        CREATE TRIGGER ClassroomAttempt_draftbind
        BEFORE UPDATE OF draftEpoch, draftRevision ON ClassroomAttempt
        WHEN NOT (NEW.attemptState = 'sending' AND OLD.attemptState IN ('unconfirmed','failed','cancelled') AND NEW.tryCount = OLD.tryCount + 1)
        BEGIN SELECT RAISE(ABORT, 'Attempt draft binding changes only on a manual retry'); END;
        CREATE TRIGGER ClassroomAttempt_terminal
        BEFORE UPDATE OF attemptState ON ClassroomAttempt
        WHEN OLD.attemptState IN ('confirmed','superseded') AND NEW.attemptState <> OLD.attemptState
        BEGIN SELECT RAISE(ABORT, 'Confirmed or superseded attempt is terminal'); END;

        CREATE TABLE ClassroomReceiptOutbox(
          originalLocalSeq INTEGER PRIMARY KEY REFERENCES ClassroomMessage(localSeq) ON DELETE CASCADE,
          principal        TEXT NOT NULL REFERENCES ClassroomConversation(principal) ON DELETE RESTRICT,
          receiptKind      TEXT NOT NULL CHECK (receiptKind = 'read'),
          createdUtc       TEXT NOT NULL
        ) STRICT;

        CREATE TABLE ClassroomCleared(
          principal  TEXT NOT NULL CHECK (length(principal) BETWEEN 4 AND 80),
          direction  TEXT NOT NULL CHECK (direction IN ('incoming','outgoing')),
          messageId  TEXT NOT NULL CHECK (length(messageId) BETWEEN 1 AND 64),
          clearedUtc TEXT NOT NULL,
          PRIMARY KEY (principal, direction, messageId)
        ) STRICT;
        """;
}
