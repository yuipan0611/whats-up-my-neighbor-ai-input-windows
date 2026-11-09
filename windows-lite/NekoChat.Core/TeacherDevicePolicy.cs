namespace NekoChat.Core;

/// Local presentation role, independent of the roster's teacher recipient; grants no network authority.
public static class TeacherDevicePolicy
{
    public static bool Resolve(bool? current, bool explicitChoice, string? legacyTeacherIp,
        IReadOnlyCollection<string> localAddresses, bool teacherNode)
    {
        if (explicitChoice) return current == true;
        // Re-evaluated at every startup, even if a previous non-explicit resolution was false.
        if (teacherNode || localAddresses.Contains(ClassroomReactions.DefaultTeacherIp)) return true;
        if (current is { } migrated) return migrated;
        return !string.IsNullOrEmpty(legacyTeacherIp) && localAddresses.Contains(legacyTeacherIp);
    }

    public static bool IsTeacher(bool? current, bool explicitChoice, bool teacherNode) =>
        explicitChoice ? current == true : current == true || teacherNode;
}
