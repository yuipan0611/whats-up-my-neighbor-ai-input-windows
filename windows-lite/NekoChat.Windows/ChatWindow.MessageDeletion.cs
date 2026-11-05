using System.Windows;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private async Task ConfirmDeleteLocalMessageAsync(InboxEntry entry, MessageView message)
    {
        if (_runtime is null || _closingRequested || _sending || _selected?.Id != entry.Id) return;
        if (MessageBox.Show(this, "只從這台電腦刪除此則訊息？\n不會收回或刪除對方已收到的內容。", "從本機刪除",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await DeleteLocalMessageAsync(entry, message);
    }

    private async Task DeleteLocalMessageAsync(InboxEntry entry, MessageView message)
    {
        if (_runtime is not { ArchivedIdentityFixture: false } runtime || _closingRequested || _sending || _selected?.Id != entry.Id) return;
        int expectedTryCount = message.AttemptTryCount;
        await FlushDraftsAsync();
        if (_selected?.Id != entry.Id || !ReferenceEquals(runtime, _runtime) || _closingRequested || _sending) return;
        var buffer = message.AttemptId is null ? null : CurrentDraft();
        if (buffer is { Dirty: true } or { Clearing: true }) { SetStatus("請先儲存目前草稿，再刪除失敗訊息。"); return; }
        var ticket = buffer?.BeginClear(); UpdateComposer();
        try
        {
            var deleted = await runtime.Worker.Run(s => {
                if (!ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal)) return (new LocalDeleteResult(false, "not-found"), (DraftState?)null);
                LocalDeleteResult result;
                if (message.AttemptId is { } id)
                {
                    result = s.DeleteClassroomPendingMessage(principal, id, expectedTryCount);
                }
                else if (message.Legacy && entry.LegacyScope is { } scope) result = s.DeleteLegacyMessage(scope, message.Sequence);
                else result = s.DeleteClassroomMessage(principal, message.Sequence);
                return (result, message.AttemptId is null ? null : s.GetClassroomDraftState(principal));
            });
            if (buffer is not null && ticket is not null) buffer.CompleteClear(ticket, deleted.Item2);
            if (_selected?.Id == entry.Id)
            {
                if (deleted.Item1.Deleted || deleted.Item1.Reason == "not-found") History.Rows.Remove(message);
                if (buffer is not null) SetDraftText(buffer.Text);
            }
            SetStatus(deleted.Item1.Deleted ? "已從本機刪除，對方的訊息不受影響。" : "訊息狀態已改變，請重新確認。");
            await RefreshInboxAsync(true);
        }
        catch (Exception ex) { if (buffer is not null && ticket is not null) buffer.CompleteClear(ticket, null); ShowStorageError(ex); }
        finally { UpdateComposer(); ViewChanged(); }
    }
}
