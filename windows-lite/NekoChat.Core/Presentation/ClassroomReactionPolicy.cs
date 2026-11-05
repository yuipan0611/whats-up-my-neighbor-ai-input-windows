namespace NekoChat.Core.Presentation;

public static class ClassroomReactionPolicy
{
    public static bool CanSend(bool isTeacher, bool removed, bool canSend, bool teacherOnly) =>
        !removed && canSend && isTeacher == teacherOnly;
}
