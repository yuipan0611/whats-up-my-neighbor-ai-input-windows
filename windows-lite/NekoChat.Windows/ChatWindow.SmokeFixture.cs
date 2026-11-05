using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    // These helpers select/load the real view and inspect its state. They never inject history or acknowledgements.
    internal InboxEntry? SmokeSelectedEntry => _selected;
    internal bool SmokeSendIdle => _activeSend.IsCompleted && !_sending;
    internal long? SmokeUnreadTotal => _snapshot?.TotalUnread;
    internal bool SmokeReadBusy => _readBusy;
    internal bool SmokeRefreshIdle => !_refreshing;
    internal object SmokeSendState => new { task = _activeSend.Status.ToString(), _sending, _attachmentDraftLoading, _attachmentBusy, draftLength = Draft.Text.Length, enabled = SendButton.IsEnabled };
    internal object SmokeReadState => new {
        _userOpened, _showInbox, _showTeacherFiles, _historyLoaded, _readBusy, _viewGeneration,
        active = IsActive, visible = IsVisible, minimized = WindowState.ToString(),
        settings = SettingsPane.Visibility.ToString(), storage = StorageStatus.Text,
        selected = _selected?.ConversationId, total = _snapshot?.TotalUnread,
        width = History.ActualWidth, height = History.ActualHeight,
        rows = History.Rows.Select(SmokeReadGeometry).ToArray()
    };
    private object SmokeReadGeometry(MessageView row)
    {
        if (History.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.FrameworkElement container)
            return new { row.Key, realized = false };
        var bounds = container.TransformToAncestor(History).TransformBounds(new System.Windows.Rect(container.RenderSize));
        var intersection = System.Windows.Rect.Intersect(new System.Windows.Rect(0, 0, History.ActualWidth, History.ActualHeight), bounds);
        return new {
            row.Key, row.Delivery, row.Sequence, container.ActualHeight, bounds.Y, bounds.Height,
            intersectionHeight = intersection.IsEmpty ? 0 : intersection.Height,
            unnormalizedReadVisible = !intersection.IsEmpty && ReadVisibility.IsVisible(bounds.Height, intersection.Height, 18, History.ActualHeight)
        };
    }
    internal void SmokeUsePeerPort(int port) => _peerPort = port;
    internal async Task SmokeOpenEntryAsync(Func<InboxEntry, bool> matches)
    {
        await RefreshInboxAsync();
        var entry = _snapshot?.Entries.SingleOrDefault(matches) ?? throw new InvalidOperationException("Fixture conversation missing.");
        await SelectEntryAsync(entry, userOpened: true);
    }
}
