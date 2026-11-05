using NekoChat.Core.Attachments;

namespace NekoChat.Core.Storage;

public sealed record TeacherFileEntry(AttachmentRecord Attachment, string TeacherName, string CreatedUtc);
public sealed record TeacherFilePage(IReadOnlyList<TeacherFileEntry> Items, long? NextBeforeLocalSeq);

public sealed partial class ChatStore
{
    /// <summary>Local organization by current roster label; this does not authenticate a sender or grant access.</summary>
    public TeacherFilePage ListTeacherFiles(long? beforeLocalSeq = null, int pageSize = 50)
    {
        if (beforeLocalSeq is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeLocalSeq));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT a.localSeq,m.principal,m.direction,m.messageId,a.blobId,a.name,a.size,a.sha256,
                  a.caption,a.peerIp,a.peerPort,a.token,a.epoch,a.state,a.problem,a.completedUtc,
                  r.displayName,m.createdUtc
                FROM Attachment a JOIN ClassroomMessage m ON m.localSeq=a.localSeq
                JOIN RosterEntry r ON m.principal='roster:'||r.id AND r.isTeacher=1
                JOIN ClassroomConversation c ON c.principal=m.principal AND c.attachmentEpoch=a.epoch
                WHERE m.direction='incoming' AND (@before IS NULL OR m.localSeq<@before)
                ORDER BY m.localSeq DESC LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@before", (object?)beforeLocalSeq ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", pageSize + 1);
            using var reader = cmd.ExecuteReader(); var rows = new List<TeacherFileEntry>();
            while (reader.Read()) rows.Add(new(MapAttachment(reader), reader.GetString(16), reader.GetString(17)));
            bool more = rows.Count > pageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            return new TeacherFilePage(rows.AsReadOnly(), more ? rows[^1].Attachment.LocalSeq : null);
        });
    }
}
