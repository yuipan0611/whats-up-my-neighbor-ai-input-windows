using System.Windows;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private Task _activeSend = Task.CompletedTask;
    private Task SendCurrentAsync(bool retry)
    { if (_closingRequested || !_activeSend.IsCompleted) return Task.CompletedTask; return _activeSend = SendCoreAsync(retry); }
    private async Task SendCoreAsync(bool retry)
    {
        if (_runtime is null || !_runtime.CanSend || _sending || _selected is not { ConversationId: { } conversation } entry || CurrentDraft() is not { Clearing: false } buffer) return;
        var runtime = _runtime; var sent = buffer.Capture(); if (string.IsNullOrWhiteSpace(sent.Text)) return;
        if (entry.Attempt is { State: not "sending" } previous && previous.RawSnapshot != sent.Text &&
            MessageBox.Show(this, "前一次訊息可能已送達。這次內容不同，會建立新的訊息；是否仍要傳送？", "傳送新內容", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetSending(true);
        try
        {
            bool reuse = entry.Attempt is { State: not "sending" } pending && pending.RawSnapshot == sent.Text;
            var begun = await runtime.Worker.Run(s => {
                var state = s.GetDraftState(conversation);
                if (state is null) return (BeginSendResult)new InvalidSend("conversation-not-found");
                if (state != new DraftState(sent.Text, sent.Epoch, sent.Revision))
                {
                    var saved = s.SaveDraft(conversation, sent.Text, sent.Epoch, sent.Revision);
                    if (!saved.Saved) return new StaleDraft(saved.CurrentEpoch, saved.CurrentRevision);
                }
                return reuse ? s.BeginManualRetry(entry.Attempt!.MessageId, sent.Text, conversation, sent.Epoch, sent.Revision) : s.BeginManualSend(conversation, sent.Text, sent.Epoch, sent.Revision);
            });
            if (begun is not AttemptStarted started) { SetStatus(begun is StaleDraft ? "草稿版本已改變；未傳送，請保留目前文字後重新讀取。" : "無法建立手動傳送，請檢查配對狀態後重試。"); return; }
            buffer.CompleteWrite(sent, new(true, "saved-with-attempt", sent.Epoch, sent.Revision));
            var prepared = await runtime.Worker.Run(s => s.GetPreparedSend(started.MessageId));
            var endpoint = await runtime.Worker.Run(s => entry.RosterId is { } row ? s.GetPresenceTarget(row)?.EffectiveEndpoint : entry.Endpoint);
            if (prepared is null || endpoint is null)
            {
                await runtime.Worker.Run(s => s.CompleteAttempt(started.MessageId, new FailedOutcome("missing_endpoint"), started.TryCount));
                SetStatus("缺少對方位址，未傳送；草稿仍在本機。"); return;
            }
            SetStatus(started.ReusedId ? "手動重送中，沿用原訊息 id…" : "手動傳送中，等待對方儲存確認…");
            await RefreshInboxAsync(); var outcome = await runtime.SendAsync(prepared, endpoint, _peerPort, conversation, buffer, sent);
            if (outcome.Completion is Confirmed { DraftCleared: true } && _selected?.ConversationId == conversation) SetDraftText(buffer.Text);
            SetStatus(outcome.Code == "ok" ? "已驗證對方的儲存確認。" : outcome.Code switch {
                "unpaired_sender" or "identity_conflict" or "hello_key_mismatch" => "身分或配對不符，請重新核對；未確認送達。",
                "payload_too_large" => "訊息超過對方可接受的上限，請修改後手動傳送。",
                "storage_unavailable" => "本機無法儲存確認；對方可能已收到，請檢查後手動重送。",
                _ => "尚未確認送達；不會自動重送。要再送請按重送。" });
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { SetSending(false); await RefreshInboxAsync(refreshHistory: true); }
    }
    private async void RetryAttemptClick(object sender, RoutedEventArgs e) => await SendCurrentAsync(true);
    private async void CancelAttemptClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _selected?.Attempt is not { } attempt || _sending) return;
        try { var result = await _runtime.Worker.Run(s => s.CancelAttempt(attempt.MessageId, attempt.TryCount)); SetStatus(result.Cancelled ? "已取消當次嘗試；草稿保留。若訊息已送到對方，無法撤回。" : "當次嘗試已改變，請重新讀取。"); await RefreshInboxAsync(); }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private async void ClearConversationClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _selected is not { } entry) return;
        if (MessageBox.Show(this, "清除此對話的本機訊息、草稿、手動傳送嘗試與未送回條？\n身分與設定保留；不會刪除對方已收到的訊息。", "清除對話", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var buffer = CurrentDraft(); DraftClearTicket? ticket = buffer?.BeginClear(); Interlocked.Increment(ref _viewGeneration);
        _runtime.Reads.Update(new(null, false, false, false, false)); UpdateComposer();
        try
        {
            var fresh = await _runtime.Worker.Run(s => {
                if (entry.ConversationId is { } conv) { s.ClearConversation(conv); return s.GetDraftState(conv); }
                if (entry.LegacyScope is { } scope) s.ClearLegacyHistory(scope); return null;
            });
            if (buffer is not null && ticket is not null) buffer.CompleteClear(ticket, fresh);
            if (entry.Id == _selected?.Id || entry.ConversationId is not null && entry.ConversationId == _selected?.ConversationId)
            { History.Rows.Clear(); _historyLoaded = false; SetDraftText(buffer?.Text ?? ""); ViewChanged(); }
            SetStatus("本機對話已清除；身分與設定保留。"); await RefreshInboxAsync(true);
        }
        catch (Exception ex) { if (buffer is not null && ticket is not null) buffer.CompleteClear(ticket, null); ShowStorageError(ex); }
        finally { UpdateComposer(); ViewChanged(); }
    }
    internal async Task PrepareForShutdownAsync()
    {
        if (_stopping) return;
        BeginClosing(); await _activeSend;
        await FlushForExitAsync(); _stopping = true; Interlocked.Increment(ref _viewGeneration);
        _runtime?.Reads.Update(new(null, false, false, true, false));
        if (_runtime?.Presence is { } presence) { presence.StateChanged -= PresenceChanged; presence.SetView(false, [], null); }
        UpdateComposer();
    }
    internal void BeginClosing() { _closingRequested = true; IsEnabled = false; _saveTimer.Stop(); _readTimer.Stop(); _userOpened = false; ViewChanged(); UpdateComposer(); }
    internal Task FlushForExitAsync()
    {
        if (_runtime is null) return Task.CompletedTask;
        var runtime = _runtime;
        var writes = _drafts.Where(p => p.Value.Dirty && !p.Value.Clearing).Select(p => (p.Key, Buffer: p.Value, Ticket: p.Value.Capture())).ToArray();
        return Task.WhenAll(writes.Select(write => runtime.Worker.Run(s => {
            var saved = write.Ticket.Persist(s, write.Key); write.Buffer.CompleteWrite(write.Ticket, saved);
            if (!saved.Saved && write.Buffer.IsCurrent(write.Ticket) && write.Buffer.Dirty) throw new InvalidOperationException("Draft could not be saved before exit."); return saved;
        }, CancellationToken.None)));
    }
}
