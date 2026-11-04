namespace NekoChat.Core.Storage;

internal static class ClassroomFilesSchema
{
    internal const string Sql = """
        CREATE TABLE ClassroomFileNode(singleton INTEGER PRIMARY KEY CHECK(singleton=1), nodeId TEXT NOT NULL) STRICT;
        CREATE TABLE ClassroomFileOperation(
          seq INTEGER PRIMARY KEY AUTOINCREMENT, shareId TEXT NOT NULL CHECK(length(shareId)=64),
          kind TEXT NOT NULL CHECK(kind IN ('add','withdraw')), json TEXT NOT NULL,
          UNIQUE(shareId,kind)
        ) STRICT;
        CREATE TABLE ClassroomFileLocal(
          shareId TEXT PRIMARY KEY, protectedKey BLOB, blobId TEXT,
          state TEXT NOT NULL DEFAULT 'available' CHECK(state IN ('available','receiving','failed','cancelled')),
          problem TEXT
        ) STRICT;
        """;
}
