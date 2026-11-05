using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    // These helpers select/load the real view and inspect its state. They never inject history or acknowledgements.
    internal InboxEntry? SmokeSelectedEntry => _selected;
    internal bool SmokeSendIdle => _activeSend.IsCompleted && !_sending;
    internal long? SmokeUnreadTotal => _snapshot?.TotalUnread;
    internal bool SmokeReadBusy => _readBusy;
    internal object SmokeReadState => new {
        _userOpened, _showInbox, _showTeacherFiles, _historyLoaded, _readBusy, _viewGeneration,
        active = IsActive, visible = IsVisible, minimized = WindowState.ToString(),
        settings = SettingsPane.Visibility.ToString(), storage = StorageStatus.Text,
        selected = _selected?.ConversationId, total = _snapshot?.TotalUnread,
        width = History.ActualWidth, height = History.ActualHeight,
        rows = History.Rows.Select(row => new {
            row.Key, row.Delivery, row.Sequence,
            height = (History.ItemContainerGenerator.ContainerFromItem(row) as System.Windows.FrameworkElement)?.ActualHeight
        }).ToArray()
    };
    internal void SmokeUsePeerPort(int port) => _peerPort = port;
    internal async Task SmokeOpenEntryAsync(Func<InboxEntry, bool> matches)
    {
        await RefreshInboxAsync();
        var entry = _snapshot?.Entries.SingleOrDefault(matches) ?? throw new InvalidOperationException("Fixture conversation missing.");
        await SelectEntryAsync(entry, userOpened: true);
    }
}
