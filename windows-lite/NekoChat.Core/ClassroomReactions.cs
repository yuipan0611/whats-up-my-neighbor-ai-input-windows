namespace NekoChat.Core;

public sealed record ClassroomReaction(string Id, string Text, string ImageFile, bool TeacherOnly);

public static class ClassroomReactions
{
    public const string DefaultTeacherIp = "192.168.24.240";
    public const double DisplaySeconds = 2;
    public static IReadOnlyList<ClassroomReaction> All { get; } = Array.AsReadOnly<ClassroomReaction>([
        new("too-fast", "講太快了", "raised-paw.png", true),
        new("cant-keep-up", "跟不上", "close-up.png", true),
        new("dont-understand", "聽不懂", "close-up.png", true),
        new("say-again", "再講一次", "raised-paw.png", true),
        new("stop-scrolling", "你再給我滑！", "raised-paw.png", false)
    ]);

    public static ClassroomReaction? Find(string? id) => All.FirstOrDefault(r => r.Id == id);

    public static ClassroomReaction? MatchText(string text)
    {
        var normalized = text.Trim().TrimEnd('!', '！', '.', '。', '?', '？', '~', '～', '…').TrimEnd();
        var id = normalized switch
        {
            "講太快" or "講太快了" or "太快了" => "too-fast",
            "跟不上" or "我跟不上" => "cant-keep-up",
            "聽不懂" or "我聽不懂" => "dont-understand",
            "再講一次" or "請再講一次" => "say-again",
            "你再給我滑" => "stop-scrolling",
            _ => null
        };
        return Find(id);
    }

    public static ClassroomReaction? ForIncoming(string text, string? reactionId, bool isTeacher)
    {
        var matched = MatchText(text);
        // Unknown future sticker IDs remain ordinary text, even if the caption matches a keyword.
        var reaction = reactionId is null ? matched : Find(reactionId);
        if (reaction is null || matched?.Id != reaction.Id || reaction.TeacherOnly && !isTeacher) return null;
        return reaction;
    }
}

public sealed record ReactionNotice(ClassroomReaction Reaction, string SenderIp, int Count = 1);

/// Bounded presentation state. Monotonic seconds are supplied by the caller.
public sealed class ReactionInbox
{
    private readonly LinkedList<ReactionNotice> pending = new();
    public ReactionNotice? Current { get; private set; }
    public double Deadline { get; private set; }
    public int PendingCount => pending.Count;

    public void Enqueue(ClassroomReaction reaction, string senderIp, double now)
    {
        if (Current is { } active && active.Reaction.Id == reaction.Id && now < Deadline)
        {
            Current = active with { Count = Math.Min(999, active.Count + 1) };
            return;
        }
        for (var node = pending.First; node is not null; node = node.Next)
        {
            if (node.Value.Reaction.Id != reaction.Id) continue;
            node.Value = node.Value with { Count = Math.Min(999, node.Value.Count + 1) };
            return;
        }
        if (pending.Count < ClassroomReactions.All.Count) pending.AddLast(new ReactionNotice(reaction, senderIp));
    }

    public ReactionNotice? TryStart(double now)
    {
        if (Current is not null || pending.First is not { } first) return null;
        Current = first.Value;
        pending.RemoveFirst();
        Deadline = now + ClassroomReactions.DisplaySeconds;
        return Current;
    }

    public void Complete() => Current = null;
}
