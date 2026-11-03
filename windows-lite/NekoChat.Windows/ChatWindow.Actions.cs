using System.Windows;
using NekoChat.Core.Classroom.Transport;
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
            if (!runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(conversation, out var principal))
            {
                await SendClassroomCoreAsync(runtime, entry, conversation, buffer, sent, principal);
                return;
            }

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
            await RefreshInboxAsync(); var transmission = runtime.SendAsync(prepared, endpoint, _peerPort, conversation, buffer, sent);
            UpdateComposer(); var outcome = await transmission;
            if (outcome.Completion is Confirmed { DraftCleared: true } && _selected?.ConversationId == conversation) SetDraftText(buffer.Text);
            SetStatus(outcome.Code == "ok" ? "已驗證對方的儲存確認。" : outcome.Code switch {
                "unpaired_sender" or "identity_conflict" or "hello_key_mismatch" => "身分或配對不符，請重新核對；未確認送達。",
                "cancelled" => "已取消當次網路操作；取消不會清除草稿，對方可能已收到。不會自動重送。",
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
        if (_runtime is not { } runtime || _selected?.Attempt is not { } attempt || _closingRequested || !CancelAttemptButton.IsEnabled) return;
        try
        {
            var cancellation = runtime.CancelAttemptAsync(attempt.MessageId, attempt.TryCount); UpdateComposer();
            SetStatus("已要求取消當次網路操作；等待工作結束，對方可能已收到。");
            var result = await cancellation;
            SetStatus(result.Cancelled ? "已取消，訊息未送出" : result.Reason == "unconfirmed" ? "已取消，但對方可能已收到；不會自動重送" : result.Reason == "terminal" ? "這次傳送可能已確認或被取代；取消不能撤回，請查看對話狀態。" : "當次嘗試已改變，未取消新的嘗試。請重新讀取。");
            await RefreshInboxAsync();
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { UpdateComposer(); }
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
                if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal))
                {
                    s.ClearClassroomConversation(principal);
                    if (entry.LegacyScope is { } legacyScope) s.ClearLegacyHistory(legacyScope);
                    return s.GetClassroomDraftState(principal);
                }
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
    internal void BeginClosing() { _closingRequested = true; _pairingWindow?.CloseSafely(); if (_runtime is not null) _ = _runtime.InvalidatePairingAsync("owner_closing"); IsEnabled = false; _saveTimer.Stop(); _readTimer.Stop(); _userOpened = false; ViewChanged(); UpdateComposer(); }
    internal bool HasUnsavedDrafts => _drafts.Values.Any(buffer => buffer.Dirty && !buffer.Clearing);
    internal Task FlushForExitAsync()
    {
        if (_runtime is null) return HasUnsavedDrafts ? Task.FromException(new InvalidOperationException("Draft storage is unavailable.")) : Task.CompletedTask;
        var runtime = _runtime;
        var writes = _drafts.Where(p => p.Value.Dirty && !p.Value.Clearing).Select(p => (p.Key, Buffer: p.Value, Ticket: p.Value.Capture())).ToArray();
        return Task.WhenAll(writes.Select(write => runtime.Worker.Run(s => {
            DraftSaveResult saved;
            if (!runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(write.Key, out var principal))
            {
                var cr = s.SaveClassroomDraft(principal, write.Ticket.Text, write.Ticket.Epoch, write.Ticket.Revision);
                saved = new DraftSaveResult(cr.Saved, cr.Reason, cr.CurrentEpoch, cr.CurrentRevision);
            }
            else
            {
                saved = write.Ticket.Persist(s, write.Key);
            }
            write.Buffer.CompleteWrite(write.Ticket, saved);
            if (!saved.Saved && write.Buffer.IsCurrent(write.Ticket) && write.Buffer.Dirty && saved.Reason != "draft-too-large")
                throw new InvalidOperationException("Draft could not be saved before exit.");
            return saved;
        }, CancellationToken.None)));
    }

    private async Task SendClassroomCoreAsync(ChatRuntime runtime, InboxEntry entry, string conversation, DraftEditBuffer buffer, DraftWriteSnapshot sent, ClassroomPrincipalKey principal)
    {
        bool reuse = entry.Attempt is { State: not "sending" } pending && pending.RawSnapshot == sent.Text;
        var begun = await runtime.Worker.Run(s => {
            var state = s.GetClassroomDraftState(principal);
            if (state != new DraftState(sent.Text, sent.Epoch, sent.Revision))
            {
                var saved = s.SaveClassroomDraft(principal, sent.Text, sent.Epoch, sent.Revision);
                if (!saved.Saved) return new StaleDraft(saved.CurrentEpoch, saved.CurrentRevision);
            }
            return reuse ? s.BeginClassroomRetry(entry.Attempt!.MessageId, principal, sent.Text, sent.Epoch, sent.Revision) : s.BeginClassroomSend(principal, sent.Text, sent.Epoch, sent.Revision);
        });
        if (begun is not AttemptStarted started) { SetStatus(begun is StaleDraft ? "草稿版本已改變；未傳送，請保留目前文字後重新讀取。" : "無法建立傳送嘗試，請重試。"); return; }
        buffer.CompleteWrite(sent, new(true, "saved-with-attempt", sent.Epoch, sent.Revision));
        var prepared = await runtime.Worker.Run(s => s.GetClassroomPreparedSend(started.MessageId));
        if (prepared is null || string.IsNullOrWhiteSpace(prepared.TargetIp))
        {
            await runtime.Worker.Run(s => s.CompleteClassroomAttempt(started.MessageId, new FailedOutcome("missing_endpoint"), null, started.TryCount));
            SetStatus("缺少對方位址，未傳送；草稿仍在本機。"); return;
        }
        SetStatus(started.ReusedId ? "手動重送中，沿用原訊息 id…" : "教室傳送中，等待對方儲存確認…");
        await RefreshInboxAsync();
        ClassroomSendResult outcome;
        try
        {
            var transmission = runtime.SendClassroomAsync(prepared, _peerPort, conversation, buffer, sent);
            UpdateComposer();
            outcome = await transmission;
        }
        catch (LocalEndpointResolutionException)
        {
            await runtime.Worker.Run(s => s.CompleteClassroomAttempt(started.MessageId, new FailedOutcome("local_endpoint_unknown"), null, started.TryCount));
            SetStatus("傳送失敗（本機端點未知）；草稿仍在本機，未傳送。");
            UpdateComposer();
            await RefreshInboxAsync();
            return;
        }
        catch (Exception)
        {
            // 其他例外一律不動 attempt，UI 顯示「傳送中斷，結果未確認；不會自動重送，要再送請按重送」，然後 RefreshInbox
            SetStatus("傳送中斷，結果未確認；不會自動重送，要再送請按重送。");
            UpdateComposer();
            await RefreshInboxAsync();
            return;
        }
        if (outcome.Completion is Confirmed { DraftCleared: true } && _selected?.ConversationId == conversation) SetDraftText(buffer.Text);
        SetStatus(outcome.Code == "ok" ? (outcome.Route == NekoChat.Core.Storage.ClassroomRoute.Classroom ? "已驗證對方的儲存確認。" : "已送達（舊版同學）。") : outcome.Code switch {
            "classroom-may-have-delivered" => "先前嘗試可能已送達教室，不降級為舊版重送；請確認後再試。",
            "cancelled" => outcome.Completion is null ? "已取消，訊息未送出" : "已取消，但對方可能已收到；不會自動重送",
            "payload_too_large" => "訊息超過對方可接受的上限，請修改後手動傳送。",
            "storage_unavailable" => "本機無法儲存確認；對方可能已收到，請檢查後手動重送。",
            _ => "尚未確認送達；不會自動重送。要再送請按重送。"
        });
    }
}
