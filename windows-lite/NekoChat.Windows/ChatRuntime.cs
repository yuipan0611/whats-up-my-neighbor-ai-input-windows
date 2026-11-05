using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NekoChat.Core;
using NekoChat.Core.Identity;
using NekoChat.Core.Presence;
using NekoChat.Core.Pairing;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Attachments;
using NekoChat.Core.ClassroomFiles;
using NekoChat.Windows.Identity;

namespace NekoChat.Windows;

/// <summary>Historical signed-V2 fixture configuration. Ordinary classroom startup never supplies this.</summary>
internal sealed record ChatWireConfiguration(int MaximumTextBytes);

/// <summary>Explicit conditional P1 policy/fixture activation; ordinary startup supplies null. Does not provision a key or choose L.</summary>
internal sealed record ChatPairingConfiguration;

internal sealed class ChatRuntime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ChatStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _network = [];
    private readonly Dictionary<(string Id, int Try), ManualSendOperation> _manualSends = [];
    private readonly ClassroomSendRegistry _classroomSends = new();
    private readonly V2PacketWriter? _writer;
    private readonly V2LanClient? _client;
    private Task? _disposal;
    private readonly PairingIdentity? _pairingIdentity;
    private Task _pairingDrain = Task.CompletedTask;
    internal PairingSessionHost? Pairing { get; }
    internal bool IsolatedData { get; }
    private long _wireGeneration;
    internal ChatStoreWorker Worker { get; }
    internal VisibleReadSession Reads { get; }
    internal ClassroomOutbox? Outbox { get; private set; }
    internal string? OutboxFailure { get; private set; }
    private IReadOnlyDictionary<string, OutboxItem> _outboxSnapshot = new Dictionary<string, OutboxItem>();
    private void RefreshOutboxSnapshot() => Volatile.Write(ref _outboxSnapshot, _store.ListOutbox().ToDictionary(row => row.MessageId, StringComparer.Ordinal));
    internal DirectLanListener Listener { get; }
    internal PresenceService? Presence { get; }
    internal ReadReceiptService? Receipts { get; }
    internal ClassroomReceiptService? ClassroomReceipts { get; }
    internal IdentityRecoveryFlow? IdentityRecovery { get; }
    internal bool ArchivedIdentityFixture { get; }
    private readonly Task _maintenance;
    internal event Action? DataChanged;
    internal event Action<long,long,long>? AttachmentProgress;
    internal int? MaximumTextBytes { get; }
    internal string IdentityStatus { get; }
    internal string? StartupWarning { get; }
    internal string? AttachmentWarning { get; }
    internal string DataDirectory { get; }
    internal ClassroomSender ClassroomSender { get; }
    internal AttachmentService? Attachments { get; }
    internal ClassroomFileService? ClassroomFiles { get; }
    internal event Action<string,long,long>? ShareProgress;
    internal bool CanSend => ArchivedIdentityFixture ? (_writer is not null && Listener.Running && !_lifetime.IsCancellationRequested) : (Listener.Running && !_lifetime.IsCancellationRequested);
    internal CancellationToken Lifetime => _lifetime.Token;
    internal bool IsStopping => _lifetime.IsCancellationRequested;
    internal Task<AttachmentDraft?> ReadAttachmentDraftAsync(ClassroomPrincipalKey principal) => Worker.Run(_ => _store.GetAttachmentDraft(principal));
    internal Task<bool> RemoveAttachmentDraftMetadataAsync(ClassroomPrincipalKey principal, string blob) => Worker.Run(_ => { _store.RemoveAttachmentDraft(principal, blob); return true; });
    internal long WireGeneration => Volatile.Read(ref _wireGeneration);
    private ChatRuntime(ChatStore store, string dataDirectory, string identityDirectory, int port, ChatWireConfiguration? configuration, ChatPairingConfiguration? pairingConfiguration, bool isolated, string? warning, bool loopbackOnly)
    {
        _store = store; IsolatedData = isolated; DataDirectory = dataDirectory; StartupWarning = warning; Reads = new(store);
        ClassroomSender = new ClassroomSender(store);
        ArchivedIdentityFixture = isolated && (configuration is not null || pairingConfiguration is not null);
        if (!isolated && (configuration is not null || pairingConfiguration is not null))
            throw new InvalidOperationException("Historical signed identity configuration requires an isolated fixture.");
        MaximumTextBytes = V2Text.ProductMaxUtf8Bytes;
        IdentityStatus = "教室區域網路・依名單 IP";
        V2Receiver? receiver = null;
        // R3: ordinary startup never opens a credential vault, inspects keys, or creates a recovery/pairing flow.
        if (ArchivedIdentityFixture)
        {
            var local = store.GetLocalIdentitySnapshot(); var vault = WindowsIdentityVault.Create(identityDirectory); var coordinator = new LocalIdentityCoordinator(store, vault);
            IdentityRecovery = new(store, vault);
            IdentityStatus = "歷史簽章測試（隔離資料）";
            if (configuration is not null && local.PublicSpkiBase64 is not null && coordinator.Inspect() is IdentityConsistent)
            {
                ECDsa? key = coordinator.OpenCurrentKey(() => true);
                try { _writer = new(key, configuration.MaximumTextBytes); key = null; } finally { key?.Dispose(); }
                MaximumTextBytes = configuration.MaximumTextBytes; var identities = new V2IdentityMonitor();
                receiver = new(store, _writer, identities: identities); _client = new(store, _writer, identities: identities);
                receiver.VerifiedActivity += _ => DataChanged?.Invoke();
                Presence = new(store, _client); Presence.AttachReceiver(receiver);
                Receipts = new(store, _client);
                Receipts.ReceiptsChanged += () => DataChanged?.Invoke();
            }
            if (pairingConfiguration is not null && coordinator.Inspect() is IdentityConsistent)
            {
                ECDsa? key = coordinator.OpenCurrentKey(() => true);
                try { _pairingIdentity = new(store, key); key = null; } finally { key?.Dispose(); }
                Pairing = new(_pairingIdentity);
            }
        }
        if (ArchivedIdentityFixture)
        {
            Listener = new(receiver, new V2ListenerOptions { Port = port }, store, Pairing);
        }
        else
        {
            var sink = new ClassroomStoreSink(store);
            try { Attachments = new AttachmentService(store, Path.Combine(dataDirectory, "attachments"), port); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is AttachmentException { Code: "unsafe_path" })
            {
                store.RecoverAttachments();
                AttachmentWarning = "附件儲存暫時無法使用；文字聊天仍可用。請檢查附件資料夾權限、磁碟空間或被鎖住的檔案，修復後重新啟動。";
                StartupWarning = string.Join("\n", new[] { warning, AttachmentWarning }.Where(value => !string.IsNullOrEmpty(value)));
            }
            if (Attachments is { } attachments)
            {
                attachments.Changed += () => DataChanged?.Invoke();
                attachments.Progress += (id, done, total) => AttachmentProgress?.Invoke(id, done, total);
                ClassroomFiles = new(store, attachments, new WindowsShareSecrets(), () => {
                    var local = LanInvitation.LocalAddresses().ToHashSet();
                    return store.GetRosterSnapshot().Where(r => r.Entry.Endpoint is { } ip && !local.Contains(ip))
                        .Select(r => new SharePeer(r.Entry.Endpoint!, attachments.Port, r.Entry.IsTeacher)).ToArray();
                }, () => {
                    string? target = store.GetRosterSnapshot().Select(r => r.Entry.Endpoint).FirstOrDefault(ip => ip is not null);
                    return target is null ? LanInvitation.LocalAddresses().FirstOrDefault(LanInvitation.PrivateIPv4) ?? "127.0.0.1" : ResolveLocalEndpointIp(target, attachments.Port);
                });
                try
                {
                    string rosterPath = Path.Combine(AppContext.BaseDirectory, "classroom-roster.csv");
                    if (File.Exists(rosterPath) && new FileInfo(rosterPath).Length <= NekoChat.Core.Roster.RosterCsv.MaxBytes)
                    {
                        var roster = NekoChat.Core.Roster.RosterCsv.Parse(File.ReadAllBytes(rosterPath));
                        var local = LanInvitation.LocalAddresses().ToHashSet();
                        ClassroomFiles.IsTeacherNode = roster.FatalError is null && roster.Errors.Count == 0 && roster.Rows.Any(r => r.IsTeacher && r.Endpoint is { } ip && local.Contains(ip));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* display role only; never grants withdrawal authority */ }
                ClassroomFiles.Changed += () => DataChanged?.Invoke();
                ClassroomFiles.Progress += (id,done,total) => ShareProgress?.Invoke(id,done,total);
            }
            var textEndpoint = new ClassroomEndpointAdapter(sink, capabilities: Attachments is null
                ? new[] { "msg.plain", "receipt.read" }
                : new[] { "msg.plain", "receipt.read", AttachmentLimits.Capability, ShareProtocol.Capability });
            IClassroomRequestEndpoint endpoint = textEndpoint;
            if (Attachments is { } activeAttachments && ClassroomFiles is { } sharedFiles)
                endpoint = new ClassroomFileEndpoint(new AttachmentEndpoint(textEndpoint, activeAttachments), sharedFiles);
            var classroomListener = new ClassroomTcpListener(endpoint, new ClassroomListenerOptions { Port = port, BindAddress = loopbackOnly ? IPAddress.Loopback : IPAddress.Any });
            sink.MessageStored += (_, _, _) => DataChanged?.Invoke();
            sink.ReceiptStored += (_, _) => DataChanged?.Invoke();
            Listener = new DirectLanListener(classroomListener, sink, Attachments);
        }
        Worker = new(store, lifetime: _lifetime.Token);
        int receiptPort = port > 0 ? port : DirectLanMessaging.DefaultPort;
        ClassroomReceipts = new ClassroomReceiptService(store, ClassroomSender, receiptPort, ownIpResolver: target => ResolveLocalEndpointIp(target, receiptPort));
        ClassroomReceipts.ReceiptsChanged += () => DataChanged?.Invoke();
        _maintenance = Task.Run(MaintainBackups);
    }
    internal Task<VisibleReadResult> ReadThroughAsync(ClassroomPrincipalKey principal, LegacyHistoryScope? legacy, long sequence, bool isLegacy, VisibleReadContext context, Func<bool> current) =>
        Worker.Run(_ => _store.MarkClassroomReadThrough(principal, legacy, sequence, isLegacy, context, current));
    internal void RegisterAttachmentDownload(AttachmentRecord row) { _store.EnqueueAttachmentDownload(row); _store.RetryOutbox("download:" + row.LocalSeq); }
    internal bool RetryAttachment(AttachmentRecord row) => _store.RequeueAttachment(row);
    internal Task<QueuedMessageReceipt> QueueMessageAsync(ClassroomPrincipalKey principal, DraftWriteSnapshot sent, int port, bool attachment) => Worker.Run(_ =>
        attachment ? _store.EnqueueAttachmentWithDraft(principal, sent.Text, sent.Epoch, sent.Revision, port).Receipt
            : _store.EnqueueClassroomMessageWithDraft(principal, sent.Text, sent.Epoch, sent.Revision, port));
    internal Task<bool> RetryStoredAsync(ClassroomPrincipalKey principal, string id, int port) => Worker.Run(_ => _store.RequeueClassroomAttempt(principal, id, port));
    internal AttachmentRecord AttachmentView(AttachmentRecord row) {
        string id = row.Direction == "incoming" ? "download:" + row.LocalSeq : row.MessageId;
        if (row.State == "complete" || !Volatile.Read(ref _outboxSnapshot).TryGetValue(id, out var item)) return row;
        return item.State is "queued" or "cancelled" or "failed" ? row with { State = item.State, Problem = item.Error } : row;
    }
    internal string? QueuedStatus(string id) {
        Volatile.Read(ref _outboxSnapshot).TryGetValue(id, out var item);
        return item?.State switch {
            "queued" => "等待傳送；連線恢復後會自動續送。",
            "sending" => "傳送中，等待對方儲存確認。",
            "cancelled" => "已取消自動續送；可重試或從本機刪除。",
            "failed" => item.Error switch { "missing_file" => "附件副本已遺失，請重新選檔。", "recipient_changed" => "對方名單或 IP 已改變，請確認對象。", "unsupported_version" => "對方版本不支援，請更新後重試。", "payload_too_large" or "legacy_payload_too_large" => "訊息超過對方上限，請修改後另送。", "legacy_delivery_unconfirmed" or "classroom-may-have-delivered" => "先前傳送可能已送達，請確認後再手動重試。", "capacity_exceeded" => "對方訊息空間已滿，請對方清理後再重試。", "wrong_recipient" or "source_not_allowed" => "對方拒絕此來源，請核對名單及 IP 位址。", "conflict" => "訊息識別資料不一致，請重新確認後另送。", "invalid_payload" => "對方無法接受訊息格式，請修改後另送。", _ => "目前無法完成傳送，請確認對方狀態後重試或從本機刪除。" },
            _ => null
        };
    }
    internal bool IsQueuedMessage(string id) => Volatile.Read(ref _outboxSnapshot).ContainsKey(id);
    internal static string ResolveLocalEndpointIp(string targetIp, int port) =>
        LocalEndpointResolver.Resolve(targetIp, port);
    internal static string RootDirectory(Settings source, bool isolated) => UiTestOptions.ResolveRootDirectory(source, isolated);
    internal static Task<ChatRuntime> OpenAsync(SettingsLoadResult source, bool isolated, int port, ChatWireConfiguration? configuration = null, ChatPairingConfiguration? pairingConfiguration = null, bool loopbackOnly = false) => Task.Run(() => {
        if (!isolated && (configuration is not null || pairingConfiguration is not null))
            throw new InvalidOperationException("Historical signed identity configuration requires an isolated fixture.");
        string root = RootDirectory(source.Value, isolated);
        string data = Path.Combine(root, "data"); ChatStore? store = null;
        try
        {
            store = ChatStore.Open(new(data) {
                RosterExcludedEndpoints = () => LanInvitation.LocalAddresses().ToArray(),
                TextValidator = text => V2Text.Check(text, configuration?.MaximumTextBytes ?? V2Text.ProductMaxUtf8Bytes) == V2TextProblem.None,
                AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? ""
            });
            var imported = source.CanImportLegacyRoster && source.Value.Contacts.Count <= NekoChat.Core.Roster.RosterCsv.MaxRows ? source.ImportLegacyRoster(store) : null;
            string? warning = !source.CanImportLegacyRoster ? "設定檔無法讀取；原檔保留，未從這份設定匯入名單。" : source.Value.Contacts.Count > NekoChat.Core.Roster.RosterCsv.MaxRows ? "舊名單超過單次載入上限，尚未載入；請聯絡班級包提供者協助。" : imported is { Skipped: > 0 } ? $"舊名單已匯入；略過 {imported.Skipped} 筆無效或重複資料。" : null;
            var runtime = new ChatRuntime(store, data, Path.Combine(root, "identity"), port, configuration, pairingConfiguration, isolated, warning, loopbackOnly); store = null; return runtime;
        }
        finally { store?.Dispose(); }
    });
    internal void Start(bool sharedNetwork = false) { Listener.Start(); if (Attachments is not null) Attachments.Port = Listener.Port;
        if (!ArchivedIdentityFixture && Outbox is null) {
            Outbox = new(_store, ClassroomSender, Attachments, ResolveLocalEndpointIp);
            Outbox.Changed += () => { try { RefreshOutboxSnapshot(); } catch (Exception error) { OutboxFailure = "待送儲存暫停：" + error.Message; } DataChanged?.Invoke(); };
            Outbox.Failed += error => { OutboxFailure = "待送儲存暫停：" + error.Message; DataChanged?.Invoke(); };
            Outbox.Recovered += () => { OutboxFailure = null; DataChanged?.Invoke(); };
        } if (!IsolatedData || sharedNetwork) ClassroomFiles?.Start(); Presence?.Start(); Receipts?.Start(); ClassroomReceipts?.Start(); }
    internal Task<V2SendResult> SendAsync(PreparedSend send, string endpoint, int port, string conversation, DraftEditBuffer buffer, DraftWriteSnapshot sent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_client is null) throw new InvalidOperationException("教室傳送服務尚未就緒。");
            long generation = Volatile.Read(ref _wireGeneration);
            var key = (send.MessageId, send.TryCount);
            if (_manualSends.ContainsKey(key)) throw new InvalidOperationException("This manual attempt is already active.");
            var operation = new ManualSendOperation(send, _lifetime.Token); _manualSends.Add(key, operation);
            var task = Task.Run(async () => {
                try
                {
                    // User cancellation aborts I/O, not the verified-ACK security/lifetime fence.
                    var outcome = await _client.SendPreparedAsync(send, endpoint, port, () => !_lifetime.IsCancellationRequested && generation == Volatile.Read(ref _wireGeneration), operation.Token).ConfigureAwait(false);
                    // Finish genuine post-send edits before publishing network completion, independent of the WPF dispatcher or exit.
                    if (outcome.Completion is Confirmed { DraftCleared: true } && _store.GetDraftState(conversation) is { } fresh)
                    {
                        var update = buffer.ApplyOwnAckClear(sent, fresh);
                        if (update.PreserveEdit is { } edited) buffer.CompleteWrite(edited, edited.Persist(_store, conversation));
                    }
                    return outcome;
                }
                finally
                {
                    try { await operation.DisposeAsync().ConfigureAwait(false); }
                    finally { lock (_gate) _manualSends.Remove(key); }
                }
            });
            _network.Add(task); return Finish(task);
        }
    }
    internal Task<ClassroomSendResult> SendClassroomAsync(ClassroomPreparedSend prepared, int port, string conversation, DraftEditBuffer buffer, DraftWriteSnapshot sent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            string ownIp = ResolveLocalEndpointIp(prepared.TargetIp, port);

            var task = _classroomSends.Start(prepared.MessageId, prepared.TryCount, async ct =>
            {
                var outcome = await ClassroomSender.SendPreparedAsync(prepared, port, ownIp, () => !_lifetime.IsCancellationRequested, ct).ConfigureAwait(false);
                if (outcome.Completion is Confirmed { DraftCleared: true } && _store.GetClassroomDraftState(prepared.Principal) is { } fresh)
                {
                    var update = buffer.ApplyOwnAckClear(sent, fresh);
                    if (update.PreserveEdit is { } edited) buffer.CompleteWrite(edited, edited.PersistClassroom(_store, prepared.Principal));
                }
                return outcome;
            }, _lifetime.Token);

            _network.Add(task);
            return Finish(task);
        }
    }
    internal bool CanCancelAttempt(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (!ArchivedIdentityFixture && Volatile.Read(ref _outboxSnapshot).TryGetValue(messageId, out var queued) && queued.State is "queued" or "sending" or "failed") return true;
            if (_classroomSends.CanCancel(messageId, tryCount)) return true;
            return _manualSends.TryGetValue((messageId, tryCount), out var operation) && operation.CanCancel;
        }
    }
    internal bool IsAttemptCancellationRequested(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (_classroomSends.IsCancellationRequested(messageId, tryCount)) return true;
            return _manualSends.TryGetValue((messageId, tryCount), out var operation) && operation.UserCancellationRequested;
        }
    }
    internal async Task<CancelResult> CancelAttemptAsync(string messageId, int tryCount)
    {
        CancellationTokenSource? cts = null;
        Task<ClassroomSendResult>? sendTask = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _classroomSends.TryBeginCancel(messageId, tryCount, out cts, out sendTask);
        }

        if (cts is not null && sendTask is not null)
        {
            ClassroomSendUiPolicy.SafeCancel(cts);
            try
            {
                var outcome = await sendTask.ConfigureAwait(false);
                return ClassroomSendUiPolicy.DecideCancelResultFromOutcome(outcome);
            }
            catch (OperationCanceledException)
            {
                return new CancelResult(true, "cancelled");
            }
            catch (Exception ex)
            {
                await Worker.Run(s => s.CompleteClassroomAttempt(messageId, new UnconfirmedOutcome(), null, tryCount)).ConfigureAwait(false);
                return ClassroomSendUiPolicy.DecideCancelResultFromException(ex);
            }
        }

        if (!ArchivedIdentityFixture && await Worker.Run(_ => _store.ListOutbox().Any(row => row.MessageId == messageId)).ConfigureAwait(false)) { await Task.Run(() => Outbox?.Cancel(messageId)).ConfigureAwait(false); return new CancelResult(true, "cancelled"); }
        if (!ArchivedIdentityFixture) return await Worker.Run(s => s.CancelClassroomAttempt(messageId, tryCount), CancellationToken.None).ConfigureAwait(false);

        Task<bool>? cancel;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            cancel = _manualSends.TryGetValue((messageId, tryCount), out var operation) ? operation.CancelAsync(messageId, tryCount) : null;
        }
        // Do not wait for transport callbacks before persisting the exact user intent off the dispatcher.
        var stored = Worker.Run(s => s.CancelAttempt(messageId, tryCount), CancellationToken.None);
        return await FinishCancellation(cancel, stored).ConfigureAwait(false);
    }
    private static async Task<CancelResult> FinishCancellation(Task<bool>? cancellation, Task<CancelResult> stored)
    {
        return await stored.ConfigureAwait(false);
    }
    private async Task<T> Finish<T>(Task<T> task)
    { try { return await task.ConfigureAwait(false); } finally { lock (_gate) _network.Remove(task); } }
    internal Task InvalidatePairingAsync(string reason)
    {
        Interlocked.Increment(ref _wireGeneration);
        lock (_gate) return _pairingDrain = Pairing?.InvalidateAsync(reason) ?? Task.CompletedTask;
    }
    internal void ResetAfterResume() { Outbox?.Wake(); _ = InvalidatePairingAsync("resume"); Presence?.ResetAfterResume(); Receipts?.ResetAfterResume(); ClassroomReceipts?.ResetAfterResume(); }
    internal async Task StopReceivingAsync()
    { if (Outbox is { } outbox) { Outbox = null; await outbox.DisposeAsync().ConfigureAwait(false); } var pairingDrain = InvalidatePairingAsync("receiver_stopped"); await _lifetime.CancelAsync().ConfigureAwait(false); await pairingDrain.ConfigureAwait(false); await Listener.DisposeAsync().ConfigureAwait(false); if (ClassroomFiles is not null) await ClassroomFiles.DisposeAsync().ConfigureAwait(false); if (Attachments is not null) await Attachments.DisposeAsync().ConfigureAwait(false); if (Presence is not null) await Presence.DisposeAsync().ConfigureAwait(false); if (Receipts is not null) await Receipts.DisposeAsync().ConfigureAwait(false); if (ClassroomReceipts is not null) await ClassroomReceipts.DisposeAsync().ConfigureAwait(false); await _maintenance.ConfigureAwait(false); }
    internal async Task DrainNetworkAsync()
    { Task[] network; lock (_gate) network = _network.ToArray(); await Task.WhenAll(network).ConfigureAwait(false); }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= Stop());
    }
    private async Task Stop()
    {
        try
        {
            try { await StopReceivingAsync().ConfigureAwait(false); } finally { await DrainNetworkAsync().ConfigureAwait(false); }
        }
        finally
        {
            try { await Worker.DisposeAsync().ConfigureAwait(false); }
            finally { try { if (Pairing is not null) await Pairing.DisposeAsync().ConfigureAwait(false); await _pairingDrain.ConfigureAwait(false); } finally { _pairingIdentity?.Dispose(); _client?.Dispose(); _writer?.Dispose(); _store.Dispose(); _lifetime.Dispose(); } }
        }
    }
    private async Task MaintainBackups()
    {
        long last = TimeProvider.System.GetTimestamp(); TimeSpan delay = TimeSpan.FromDays(1);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                long now = TimeProvider.System.GetTimestamp(); if (TimeProvider.System.GetElapsedTime(last, now) < delay) continue;
                try { _store.MaintainBackups(); delay = TimeSpan.FromDays(1); } catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException) { delay = TimeSpan.FromMinutes(1); }
                last = now;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
}
