using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    // These helpers select/load the real view and inspect its state. They never inject history or acknowledgements.
    internal InboxEntry? SmokeSelectedEntry => _selected;
    internal bool SmokeSendIdle => _activeSend.IsCompleted && !_sending;
    internal Func<Task>? SmokeBeforeDraftWriteCompletion { get; set; }
    internal void SmokeUsePeerPort(int port) => _peerPort = port;
    internal async Task SmokeOpenEntryAsync(Func<InboxEntry, bool> matches)
    {
        await RefreshInboxAsync();
        // Refresh may coalesce this request into an already running refresh. Await its actual snapshot.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try { while (_refreshing) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Fixture inbox refresh did not finish"); }
        var entry = _snapshot?.Entries.SingleOrDefault(matches) ?? throw new InvalidOperationException("Fixture conversation missing.");
        await SelectEntryAsync(entry, userOpened: true);
    }
}
