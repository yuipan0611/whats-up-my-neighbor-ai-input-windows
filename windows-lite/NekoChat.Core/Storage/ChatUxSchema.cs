namespace NekoChat.Core.Storage;

internal static class ChatUxSchema
{
    internal const string Sql = """
        CREATE TABLE ClassroomArchivedContact(
          rosterEntryId TEXT PRIMARY KEY REFERENCES RosterRemoved(rosterEntryId) ON DELETE CASCADE,
          endpoint TEXT,
          studentNumber TEXT,
          source TEXT
        ) STRICT;
        CREATE TABLE ClassroomAutoSource(ip TEXT PRIMARY KEY, rosterEntryId TEXT NOT NULL) STRICT;
        ALTER TABLE ReadReceiptPolicy ADD COLUMN explicitChoice INTEGER NOT NULL DEFAULT 0 CHECK(explicitChoice IN (0,1));
        ALTER TABLE ReadReceiptPolicy ADD COLUMN noticeShown INTEGER NOT NULL DEFAULT 0 CHECK(noticeShown IN (0,1));
        """;
    internal const string TeacherIndex = "CREATE UNIQUE INDEX RosterEntry_one_teacher ON RosterEntry(isTeacher) WHERE isTeacher=1;";
}
