using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    // These helpers select/load the real view and inspect its state. They never inject history or acknowledgements.
    internal InboxEntry? SmokeSelectedEntry => _selected;
    internal bool SmokeSendIdle => _activeSend.IsCompleted && !_sending;
    internal void SmokeUsePeerPort(int port) => _peerPort = port;
    internal async Task SmokeOpenEntryAsync(Func<InboxEntry, bool> matches)
    {
        await RefreshInboxAsync();
        var entry = _snapshot?.Entries.SingleOrDefault(matches) ?? throw new InvalidOperationException("Fixture conversation missing.");
        await SelectEntryAsync(entry, userOpened: true);
    }
}
