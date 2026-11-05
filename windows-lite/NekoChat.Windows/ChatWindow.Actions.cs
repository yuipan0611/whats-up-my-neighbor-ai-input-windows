using NekoChat.Core.Diagnostics;
using System.Windows;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private Task _activeSend = Task.CompletedTask;
    private Task SendCurrentAsync(bool retry, PendingSendSummary? retryAttempt = null)
    {
        if (_showTeacherFiles || _closingRequested || !_activeSend.IsCompleted) {
            _runtime?.Diagnostics.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Deferred, !_activeSend.IsCompleted ? DiagnosticCode.OperationBusy : DiagnosticCode.StaleView);
            return Task.CompletedTask;
        }
        var send = _activeSend = SendBoundaryAsync(retry, retryAttempt);
        _ = send.ContinueWith(_ => {
            try {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => {
                    try { UpdateComposer(); }
                    catch (Exception error) when (DiagnosticFault.Catchable(error)) { ShowStorageError(error); }
                }));
            }
            catch (Exception error) when (DiagnosticFault.Catchable(error)) {
                _runtime?.Diagnostics.Record(DiagnosticStage.UiRefresh, DiagnosticOutcome.Failed, DiagnosticCode.NotificationFailed, error);
            }
        }, TaskScheduler.Default);
        return send;
    }
    private async Task SendBoundaryAsync(bool retry, PendingSendSummary? retryAttempt)
    {
        try { await SendCoreAsync(retry, retryAttempt); }
        catch (Exception error) when (DiagnosticFault.Catchable(error))
        { ShowStorageError(error); SetSending(false); }

    }
    private async Task SendCoreAsync(bool retry, PendingSendSummary? retryAttempt)
    {
        if (_attachmentDraftLoading || _runtime is null || !_runtime.CanSend || _sending || _selected is not { ConversationId: { } conversation } entry || CurrentDraft() is not { Clearing: false } buffer) {
            _runtime?.Diagnostics.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Deferred, _sending ? DiagnosticCode.OperationBusy : DiagnosticCode.UiNotReady);
            return;
        }
        var runtime = _runtime; var sent = buffer.Capture();
        if (!runtime.ArchivedIdentityFixture) { await QueueCurrentMessageAsync(runtime, entry, buffer); return; }
        if (_attachmentDraft is not null) { await SendAttachmentCoreAsync(runtime, entry, buffer, sent); return; }
        if (string.IsNullOrWhiteSpace(sent.Text)) return;
        if (retryAttempt is not null) entry = entry with { Attempt = retryAttempt };
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
            SetStatus(started.ReusedId ? "正在重試傳送…" : "手動傳送中，等待對方儲存確認…");
            await RefreshInboxAsync(); var transmission = runtime.SendAsync(prepared, endpoint, _peerPort, conversation, buffer, sent);
            UpdateComposer(); var outcome = await transmission;
            if (outcome.Completion is Confirmed { DraftCleared: true } && _selected?.ConversationId == conversation) SetDraftText(buffer.Text);
            if (outcome.Completion is Confirmed or AlreadyConfirmed) ClearRecoveredDiagnostic(DiagnosticStage.SendPrepare, writeConfirmed: true);
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
    private async Task QueueCurrentMessageAsync(ChatRuntime runtime, InboxEntry entry, DraftEditBuffer buffer)
    {
        if (!ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal) || _attachmentDraft is not null && runtime.Attachments is null) return;
        SetSending(true); DraftClearTicket? ticket = null;
        try {
            await FlushDraftsAsync();
            if (buffer.Dirty || buffer.Clearing || _selected?.Id != entry.Id || !ReferenceEquals(runtime, _runtime)) {
                runtime.Diagnostics.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Deferred, buffer.Dirty ? DiagnosticCode.DraftNotSaved : buffer.Clearing ? DiagnosticCode.OperationBusy : DiagnosticCode.StaleView);
                return;
            }
            var sent = buffer.Capture(); bool attachment = _attachmentDraft is not null;
            if (!attachment && string.IsNullOrWhiteSpace(sent.Text)) return;
            ticket = buffer.BeginClear(); UpdateComposer();
            var fresh = await runtime.QueueMessageAsync(principal, sent, _peerPort, attachment);
            buffer.CompleteClear(ticket, fresh.Draft); ticket = null;
            ClearRecoveredDiagnostic(DiagnosticStage.SendPrepare, writeConfirmed: true);
            if (_selected?.Id == entry.Id) SetDraftText(buffer.Text);
            SetStatus("訊息已保存在本機待送；暫時斷線會自動續送。");
        }
        catch (Exception error) { ShowStorageError(error); }
        finally {
            if (ticket is not null) buffer.CompleteClear(ticket, null);
            SetSending(false); await RefreshAttachmentDraftAsync(); await RefreshInboxAsync(true);
        }
    }
    private async void RetryAttemptClick(object sender, RoutedEventArgs e) => await SendCurrentAsync(true);
    private async void CancelAttemptClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is not { } runtime || _selected?.Attempt is not { } attempt || _closingRequested || !CancelAttemptButton.IsEnabled) return;
        try
        {
            var cancellation = runtime.CancelAttemptAsync(attempt.MessageId, attempt.TryCount); UpdateComposer();
            SetStatus(ClassroomSendUiPolicy.CancelInitiatedStatusText);
            var result = await cancellation;
            SetStatus(ClassroomSendUiPolicy.DecideCancelClickResult(result));
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
    internal async Task FlushForExitAsync()
    {
        await _draftSaveGate.WaitAsync();
        try { await FlushForExitCoreAsync(); }
        finally { _draftSaveGate.Release(); }
    }
    // Synchronous OnExit cannot wait for an autosave continuation on the exiting Dispatcher.
    internal Task FlushForDispatcherExitAsync() => FlushForExitCoreAsync();
    private Task FlushForExitCoreAsync()
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
            if (!saved.Saved) runtime.Diagnostics.Record(DiagnosticStage.StorageWrite, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(saved.Reason).Code);
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
        SetStatus(started.ReusedId ? "正在重試傳送…" : "教室傳送中，等待對方儲存確認…");
        await RefreshInboxAsync();
        ClassroomSendResult outcome;
        try
        {
            var transmission = runtime.SendClassroomAsync(prepared, _peerPort, conversation, buffer, sent);
            UpdateComposer();
            outcome = await transmission;
        }
        catch (Exception ex)
        {
            runtime.Diagnostics.Record(DiagnosticStage.SendConfirm, DiagnosticOutcome.Failed, error: ex, messageId: started.MessageId, attempt: started.TryCount);
            var decision = ClassroomSendUiPolicy.DecideException(ex);
            if (decision.Action == AttemptActionKind.MarkFailed)
            {
                await runtime.Worker.Run(s => s.CompleteClassroomAttempt(started.MessageId, new FailedOutcome(decision.FailedReason!), null, started.TryCount));
            }
            else if (decision.Action == AttemptActionKind.MarkUnconfirmed)
            {
                await runtime.Worker.Run(s => s.CompleteClassroomAttempt(started.MessageId, new UnconfirmedOutcome(), null, started.TryCount));
            }
            SetStatus(decision.StatusText);
            UpdateComposer();
            await RefreshInboxAsync();
            return;
        }
        if (outcome.Success) ClearRecoveredDiagnostic(DiagnosticStage.SendPrepare, writeConfirmed: true);
        var outcomeDecision = ClassroomSendUiPolicy.DecideOutcome(outcome);
        if (outcomeDecision.DraftCleared && _selected?.ConversationId == conversation) SetDraftText(buffer.Text);
        SetStatus(outcomeDecision.StatusText);
    }
}
