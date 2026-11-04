namespace NekoChat.Core.Storage;

internal static class AttachmentSchema
{
    internal const string Sql = """
        ALTER TABLE ClassroomConversation ADD COLUMN attachmentEpoch INTEGER NOT NULL DEFAULT 0 CHECK(attachmentEpoch>=0);
        CREATE TABLE AttachmentDraft(
          principal TEXT PRIMARY KEY REFERENCES ClassroomConversation(principal),
          blobId TEXT NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL CHECK(size BETWEEN 0 AND 26214400),
          sha256 TEXT NOT NULL CHECK(length(sha256)=64), epoch INTEGER NOT NULL CHECK(epoch>=0)
        ) STRICT;
        CREATE TABLE Attachment(
          localSeq INTEGER PRIMARY KEY REFERENCES ClassroomMessage(localSeq) ON DELETE CASCADE,
          blobId TEXT NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL CHECK(size BETWEEN 0 AND 26214400),
          sha256 TEXT NOT NULL CHECK(length(sha256)=64), caption TEXT NOT NULL,
          peerIp TEXT NOT NULL, peerPort INTEGER NOT NULL CHECK(peerPort BETWEEN 1 AND 65535),
          token TEXT NOT NULL CHECK(length(token)=64), epoch INTEGER NOT NULL CHECK(epoch>=0),
          state TEXT NOT NULL CHECK(state IN ('sending','offered','receiving','complete','failed','cancelled','unconfirmed','unavailable')),
          problem TEXT, completedUtc TEXT
        ) STRICT;
        CREATE TRIGGER Attachment_completed_once BEFORE UPDATE OF completedUtc ON Attachment
        WHEN OLD.completedUtc IS NOT NULL AND NEW.completedUtc IS NOT OLD.completedUtc
        BEGIN SELECT RAISE(ABORT,'Verified completion time is set once'); END;
        CREATE TRIGGER Attachment_complete_terminal BEFORE UPDATE OF state ON Attachment
        WHEN OLD.state='complete' AND NEW.state NOT IN ('complete','unavailable')
        BEGIN SELECT RAISE(ABORT,'Completed bytes cannot become an unconfirmed transfer'); END;
        """;
}
