using System.Net.Http;
using System.Net.NetworkInformation;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Classroom.Transport;

/// <summary>Durable user-initiated sends. Drafts are never consulted by background retries.</summary>
public sealed class ClassroomOutbox : IAsyncDisposable
{
    private readonly ChatStore _store;
    private readonly ClassroomSender _sender;
    private readonly AttachmentService? _attachments;
    private readonly Func<string, int, string> _ownIp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task)> _jobs = [];
    private readonly object _gate = new();
    private readonly Task _loop;
    public event Action? Changed;
    public event Action<Exception>? Failed;
    public event Action? Recovered;
    private int _failed;
    private int _wakeRequested;
    private void ReportFailure(Exception error) { Interlocked.Exchange(ref _failed, 1); try { Failed?.Invoke(error); } catch { } }
    private void ReportRecovery() { if (Interlocked.Exchange(ref _failed, 0) != 0) { try { Recovered?.Invoke(); } catch { } } }
    private void Finish(string id, bool success, bool retryable, string? error) { _store.FinishOutbox(id, success, retryable, error); ReportRecovery(); }

    public ClassroomOutbox(ChatStore store, ClassroomSender sender, AttachmentService? attachments, Func<string, int, string> ownIp)
    {
        _store = store; _sender = sender; _attachments = attachments; _ownIp = ownIp;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailable;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        _loop = Task.Run(RunAsync);
    }

    private void NetworkAvailable(object? sender, NetworkAvailabilityEventArgs args) { if (args.IsAvailable) NetworkAddressChanged(sender, EventArgs.Empty); }
    private void NetworkAddressChanged(object? sender, EventArgs args) {
        if (_lifetime.IsCancellationRequested) return;
        try { Wake(); } catch (Exception error) { ReportFailure(error); }
    }
    public void Wake() => Interlocked.Exchange(ref _wakeRequested, 1);
    public void Cancel(string id)
    {
        _store.CancelOutbox(id);
        lock (_gate) if (_jobs.TryGetValue(id, out var job)) _ = job.Cancellation.CancelAsync();
        Notify();
    }
    private void Notify() { try { Changed?.Invoke(); } catch { /* A view cannot change a committed send. */ } }
    private async Task RunAsync()
    {
        try
        {
            bool recovering = true;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                try
                {
                    if (recovering) { _store.RecoverOutbox(); _store.WakeOutbox(); recovering = false; }
                    if (Interlocked.Exchange(ref _wakeRequested, 0) != 0) _store.WakeOutbox();
                    foreach (var candidate in _store.ListOutbox().Where(x => x.State is "queued" or "sending"))
                    {
                        lock (_gate)
                        {
                            if (_jobs.Count >= 4) break;
                            if (_jobs.ContainsKey(candidate.MessageId)) continue;
                            if (candidate.State == "sending") { _store.RecoverOutboxItem(candidate.MessageId); continue; }
                            var claimed = _store.ClaimOutbox(candidate.MessageId);
                            if (claimed is null) { if (_store.ListOutbox().SingleOrDefault(x => x.MessageId == candidate.MessageId)?.State == "complete") ReportRecovery(); continue; }
                            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                            var task = Task.Run(() => SendAsync(claimed, cancellation.Token));
                            _jobs.Add(candidate.MessageId, (cancellation, task));
                            _ = task.ContinueWith(completed => {
                                if (completed.Exception is { } error) { ReportFailure(error); }
                                lock (_gate) _jobs.Remove(candidate.MessageId);
                                cancellation.Dispose(); Notify();
                            }, TaskScheduler.Default);
                        }
                    }
                }
                catch (Exception error) { ReportFailure(error); }
            } while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task SendAsync(OutboxItem item, CancellationToken cancellation)
    {
        Notify();
        try
        {
            if (item.AttachmentSequence is { } sequence)
            {
                if (_attachments is null) { Finish(item.MessageId, false, false, "attachment_unavailable"); return; }
                var row = _store.GetAttachment(sequence);
                if (row is null || row.Direction == "outgoing" && !_attachments.Vault.Exists(row.File)) { Finish(item.MessageId, false, false, "missing_file"); return; }
                if (row.Direction == "incoming") await _attachments.DownloadAsync(row.LocalSeq).WaitAsync(cancellation).ConfigureAwait(false);
                else await _attachments.RetryOfferAsync(row, () => _store.OutboxCurrent(item.MessageId), cancellation).ConfigureAwait(false);
                Finish(item.MessageId, true, false, null);
            }
            else
            {
                var prepared = _store.GetClassroomPreparedSend(item.MessageId);
                if (prepared is null) { Finish(item.MessageId, false, false, "missing_attempt"); return; }
                var result = await _sender.SendPreparedAsync(prepared, item.PeerPort, _ownIp(item.TargetIp, item.PeerPort),
                    () => _store.OutboxCurrent(item.MessageId), cancellation).ConfigureAwait(false);
                bool uncertainLegacy = result.Route == ClassroomRoute.Legacy && result.Completion is not (Confirmed or AlreadyConfirmed or StateUpdated { NewState: "failed" });
                bool retry = !uncertainLegacy && IsRetryable(result.RemoteCode ?? result.Code);
                Finish(item.MessageId, result.Success || result.Completion is Confirmed or AlreadyConfirmed, retry, result.Code);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // App shutdown keeps queued work; an explicit user cancellation already persisted cancelled.
            Finish(item.MessageId, false, true, "interrupted");
        }
        catch (Exception error)
        {
            string code = error is AttachmentException attachment ? attachment.Code : error is LocalEndpointResolutionException ? "network_error" : error is HttpRequestException ? "network_error" : error is TaskCanceledException ? "timeout" : "send_failed";
            if (item.AttachmentSequence is null && _store.GetClassroomPreparedSend(item.MessageId) is { } active)
                _store.CompleteClassroomAttempt(item.MessageId, new FailedOutcome(code), null, active.TryCount);
            Finish(item.MessageId, false, IsRetryable(code), code);
        }
        finally { Notify(); }
    }

    public static bool IsRetryable(string code) => code is "network_unconfirmed" or "transient" or "network_error" or "timeout" or "transfer_failed" or "receiver_not_ready" or "busy" or "rate_limited" or "storage_unavailable" or "stale_session" or "local_endpoint_unknown";

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailable;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        Task[] jobs; lock (_gate) jobs = _jobs.Values.Select(x => x.Task).ToArray();
        try { await Task.WhenAll(jobs).ConfigureAwait(false); }
        catch (Exception error) { ReportFailure(error); }
        finally { _lifetime.Dispose(); }
    }
}
