namespace NekoChat.Core.Storage;

public sealed record ReadReceiptPolicy(bool Enabled, long Generation);
public sealed record ReadPolicyChange(ReadReceiptPolicy Policy, int DroppedReceipts);
public sealed record VisibleReadContext(string? ActiveConversationId, bool UserOpened, bool WindowActive, bool Minimized, bool ViewportReady);
public sealed record VisibleReadResult(int Marked, int Queued, string Reason);
/// <summary>Metadata-only delivery token; the wire layer signs a fresh nonce for every transmission.</summary>
public sealed record PendingReadReceipt(long OriginalLocalSeq, string MessageId, string ReaderId, string OriginalSenderId, long PolicyGeneration);

public static class ReadVisibility
{
    public static bool IsVisible(double messageHeight, double visibleHeight, double lineHeight, double viewportHeight) =>
        double.IsFinite(messageHeight) && double.IsFinite(visibleHeight) && double.IsFinite(lineHeight) && double.IsFinite(viewportHeight) &&
        messageHeight > 0 && viewportHeight > 0 && lineHeight > 0 && visibleHeight >= 0 &&
        visibleHeight <= Math.Min(messageHeight, viewportHeight) &&
        visibleHeight >= Math.Min(messageHeight / 2, viewportHeight / 2) && visibleHeight >= Math.Min(lineHeight, messageHeight);
}

/// <summary>WPF updates this on focus, conversation, minimize and viewport changes. Queued old events cannot mark a new view read.</summary>
public sealed class VisibleReadSession(IChatStore store)
{
    private readonly object _gate = new();
    private long _generation;
    private VisibleReadContext _context = new(null, false, false, false, false);
    public long Update(VisibleReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate) { long next = checked(_generation + 1); _context = context; return _generation = next; }
    }
    public VisibleReadResult Mark(string conversationId, long expectedGeneration, IReadOnlyList<long> visibleLocalSeqs)
    {
        VisibleReadContext context;
        lock (_gate)
        {
            if (expectedGeneration != _generation) return new(0, 0, "stale-view");
            context = _context;
        }
        // Focus/view updates must stay responsive while SQLite waits. The storage layer checks this again after BEGIN.
        return store.MarkVisibleRead(conversationId, visibleLocalSeqs, context, () => {
            lock (_gate) return expectedGeneration == _generation;
        });
    }
}
