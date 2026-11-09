namespace NekoChat.Core;

/// D-32: the old toggle was hidden, so a value without explicit choice is not consent to opt out.
public static class ReactionPreviewPolicy
{
    public static bool AfterLoad(bool enabled, bool explicitChoice) => !explicitChoice || enabled;

    public static bool ShowImage(bool enabled, ClassroomReaction? reaction, bool isTeacher) =>
        enabled && reaction is not null && (!reaction.TeacherOnly || isTeacher);
}
