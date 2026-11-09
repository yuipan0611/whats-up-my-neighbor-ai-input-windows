namespace NekoChat.Core;

/// D-32: the old toggle was hidden, so a value without explicit choice is not consent to opt out.
public enum ReactionNotificationKind { None, Text, Image }

public static class ReactionPreviewPolicy
{
    public static bool AfterLoad(bool enabled, bool explicitChoice) => !explicitChoice || enabled;

    public static bool ShowImage(bool enabled, ClassroomReaction? reaction, bool isTeacher) =>
        Notification(enabled, reaction, isTeacher) == ReactionNotificationKind.Image;

    public static ReactionNotificationKind Notification(bool enabled, ClassroomReaction? reaction, bool isTeacher) =>
        reaction is { TeacherOnly: true } && !isTeacher ? ReactionNotificationKind.None :
        enabled && reaction is not null ? ReactionNotificationKind.Image : ReactionNotificationKind.Text;

    public static string? HistoryLabel(string? reactionId, bool isTeacher, bool outgoing = false) =>
        !outgoing && !isTeacher && ClassroomReactions.Find(reactionId) is { TeacherOnly: true } reaction
            ? "課堂反應：" + reaction.Text + "（只有老師電腦會跳出貓咪圖）" : null;
}
