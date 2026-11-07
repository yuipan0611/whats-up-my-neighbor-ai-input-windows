using System.Windows;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private string? LocalMessageDeleteReason(InboxEntry entry, MessageView message)
    {
        if (_closingRequested) return "視窗正在關閉，暫時無法刪除。";
        if (_runtime is not { ArchivedIdentityFixture: false }) return "本機資料尚未就緒。";
        if (_selected?.Id != entry.Id || !_historyLoaded) return "正在切換對象，請重新選取此對話。";
        if (_sending || message.AttemptSending) return "傳送中，完成或停止後才能刪除。";
        if (message.Attachment?.State is "sending" or "receiving") return "附件傳輸中，請先取消傳輸。";
        return null;
    }

    private async Task ConfirmDeleteLocalMessageAsync(InboxEntry entry, MessageView message)
    {
        if (LocalMessageDeleteReason(entry, message) is { } reason) { SetStatus(reason); return; }
        if (ContactPrompt("只從這台電腦刪除此則訊息？\n不會收回或刪除對方已收到的內容。", "從本機刪除",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await DeleteLocalMessageAsync(entry, message);
    }

    private async Task DeleteLocalMessageAsync(InboxEntry entry, MessageView message)
    {
        if (LocalMessageDeleteReason(entry, message) is { } reason) { SetStatus(reason); return; }
        var runtime = _runtime!;
        int expectedTryCount = message.AttemptTryCount;
        await FlushDraftsAsync();
        if (!ReferenceEquals(runtime, _runtime) || LocalMessageDeleteReason(entry, message) is not null) { SetStatus("對話或傳送狀態已改變，請重新確認。"); return; }
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
