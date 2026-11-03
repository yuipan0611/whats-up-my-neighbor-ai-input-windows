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
    private readonly Dictionary<(string Id, int Try), (CancellationTokenSource Cts, Task<ClassroomSendResult> Task)> _classroomSends = [];
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
    internal DirectLanListener Listener { get; }
    internal PresenceService? Presence { get; }
    internal ReadReceiptService? Receipts { get; }
    internal ClassroomReceiptService? ClassroomReceipts { get; }
    internal IdentityRecoveryFlow? IdentityRecovery { get; }
    internal bool ArchivedIdentityFixture { get; }
    private readonly Task _maintenance;
    internal event Action? DataChanged;
    internal int? MaximumTextBytes { get; }
    internal string IdentityStatus { get; }
    internal string? StartupWarning { get; }
    internal string DataDirectory { get; }
    internal ClassroomSender ClassroomSender { get; }
    internal bool CanSend => ArchivedIdentityFixture ? (_writer is not null && Listener.Running && !_lifetime.IsCancellationRequested) : (Listener.Running && !_lifetime.IsCancellationRequested);
    internal CancellationToken Lifetime => _lifetime.Token;
    internal bool IsStopping => _lifetime.IsCancellationRequested;
    internal long WireGeneration => Volatile.Read(ref _wireGeneration);
    private ChatRuntime(ChatStore store, string dataDirectory, string identityDirectory, int port, ChatWireConfiguration? configuration, ChatPairingConfiguration? pairingConfiguration, bool isolated, string? warning)
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
            var endpoint = new ClassroomEndpointAdapter(sink);
            var classroomListener = new ClassroomTcpListener(endpoint, new ClassroomListenerOptions { Port = port });
            sink.MessageStored += (_, _, _) => DataChanged?.Invoke();
            sink.ReceiptStored += (_, _) => DataChanged?.Invoke();
            Listener = new DirectLanListener(classroomListener, sink);
        }
        Worker = new(store, lifetime: _lifetime.Token);
        ClassroomReceipts = new ClassroomReceiptService(store, ClassroomSender, port, ownIpResolver: target => ResolveLocalEndpointIp(target, port));
        ClassroomReceipts.ReceiptsChanged += () => DataChanged?.Invoke();
        _maintenance = Task.Run(MaintainBackups);
    }
    internal static string ResolveLocalEndpointIp(string targetIp, int port) =>
        LocalEndpointResolver.Resolve(targetIp, port);
    internal static string RootDirectory(Settings source, bool isolated) => UiTestOptions.ResolveRootDirectory(source, isolated);
    internal static Task<ChatRuntime> OpenAsync(SettingsLoadResult source, bool isolated, int port, ChatWireConfiguration? configuration = null, ChatPairingConfiguration? pairingConfiguration = null) => Task.Run(() => {
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
            string? warning = !source.CanImportLegacyRoster ? "設定檔無法讀取；原檔保留，未從這份設定匯入名單。" : source.Value.Contacts.Count > NekoChat.Core.Roster.RosterCsv.MaxRows ? "舊名單超過單次匯入上限，尚未匯入；請分批以 CSV 預覽匯入。" : imported is { Skipped: > 0 } ? $"舊名單已匯入；略過 {imported.Skipped} 筆無效或重複資料。" : null;
            var runtime = new ChatRuntime(store, data, Path.Combine(root, "identity"), port, configuration, pairingConfiguration, isolated, warning); store = null; return runtime;
        }
        finally { store?.Dispose(); }
    });
    internal void Start() { Listener.Start(); Presence?.Start(); Receipts?.Start(); ClassroomReceipts?.Start(); }
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
            var key = (prepared.MessageId, prepared.TryCount);
            if (_classroomSends.ContainsKey(key))
            {
                throw new InvalidOperationException($"Classroom send already in progress for message {prepared.MessageId} try {prepared.TryCount}.");
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var task = Task.Run(async () =>
            {
                try
                {
                    var outcome = await ClassroomSender.SendPreparedAsync(prepared, port, ownIp, () => !_lifetime.IsCancellationRequested, cts.Token).ConfigureAwait(false);
                    if (outcome.Completion is Confirmed { DraftCleared: true } && _store.GetClassroomDraftState(prepared.Principal) is { } fresh)
                    {
                        var update = buffer.ApplyOwnAckClear(sent, fresh);
                        if (update.PreserveEdit is { } edited) buffer.CompleteWrite(edited, edited.Persist(_store, conversation));
                    }
                    return outcome;
                }
                finally
                {
                    lock (_gate) _classroomSends.Remove(key);
                    cts.Dispose();
                }
            });
            if (!_classroomSends.TryAdd(key, (cts, task)))
            {
                cts.Dispose();
                throw new InvalidOperationException($"Classroom send already in progress for message {prepared.MessageId} try {prepared.TryCount}.");
            }
            _network.Add(task);
            return Finish(task);
        }
    }
    internal bool CanCancelAttempt(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (_classroomSends.TryGetValue((messageId, tryCount), out var pair)) return !pair.Cts.IsCancellationRequested;
            return _manualSends.TryGetValue((messageId, tryCount), out var operation) && operation.CanCancel;
        }
    }
    internal bool IsAttemptCancellationRequested(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (_classroomSends.TryGetValue((messageId, tryCount), out var pair)) return pair.Cts.IsCancellationRequested;
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
            if (_classroomSends.TryGetValue((messageId, tryCount), out var pair))
            {
                cts = pair.Cts;
                sendTask = pair.Task;
            }
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
        if (cancellation is not null) await Task.WhenAll(cancellation, stored).ConfigureAwait(false);
        return await stored.ConfigureAwait(false);
    }
    private async Task<T> Finish<T>(Task<T> task)
    { try { return await task.ConfigureAwait(false); } finally { lock (_gate) _network.Remove(task); } }
    internal Task InvalidatePairingAsync(string reason)
    {
        Interlocked.Increment(ref _wireGeneration);
        lock (_gate) return _pairingDrain = Pairing?.InvalidateAsync(reason) ?? Task.CompletedTask;
    }
    internal void ResetAfterResume() { _ = InvalidatePairingAsync("resume"); Presence?.ResetAfterResume(); Receipts?.ResetAfterResume(); ClassroomReceipts?.ResetAfterResume(); }
    internal async Task StopReceivingAsync()
    { var pairingDrain = InvalidatePairingAsync("receiver_stopped"); await _lifetime.CancelAsync().ConfigureAwait(false); await pairingDrain.ConfigureAwait(false); await Listener.DisposeAsync().ConfigureAwait(false); if (Presence is not null) await Presence.DisposeAsync().ConfigureAwait(false); if (Receipts is not null) await Receipts.DisposeAsync().ConfigureAwait(false); if (ClassroomReceipts is not null) await ClassroomReceipts.DisposeAsync().ConfigureAwait(false); await _maintenance.ConfigureAwait(false); }
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
