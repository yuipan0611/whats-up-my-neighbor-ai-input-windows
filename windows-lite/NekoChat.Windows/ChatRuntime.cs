using System.IO;
using System.Security.Cryptography;
using NekoChat.Core;
using NekoChat.Core.Identity;
using NekoChat.Core.Presence;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Windows.Identity;

namespace NekoChat.Windows;

/// <summary>Explicit configuration supplied by a settled product policy or an isolated fixture. No implicit L or identity mode.</summary>
internal sealed record ChatWireConfiguration(int MaximumTextBytes);

internal sealed class ChatRuntime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ChatStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _network = [];
    private readonly V2PacketWriter? _writer;
    private readonly V2LanClient? _client;
    private Task? _disposal;
    private long _wireGeneration;
    internal ChatStoreWorker Worker { get; }
    internal VisibleReadSession Reads { get; }
    internal DirectLanListener Listener { get; }
    internal PresenceService? Presence { get; }
    internal ReadReceiptService? Receipts { get; }
    private readonly Task _maintenance;
    internal event Action? DataChanged;
    internal int? MaximumTextBytes { get; }
    internal string IdentityStatus { get; }
    internal string? StartupWarning { get; }
    internal string DataDirectory { get; }
    internal bool CanSend => _writer is not null && Listener.Running && !_lifetime.IsCancellationRequested;
    internal CancellationToken Lifetime => _lifetime.Token;
    private ChatRuntime(ChatStore store, string dataDirectory, string identityDirectory, int port, ChatWireConfiguration? configuration, string? warning)
    {
        _store = store; DataDirectory = dataDirectory; StartupWarning = warning; Reads = new(store);
        var local = store.GetLocalIdentitySnapshot(); var vault = WindowsIdentityVault.Create(identityDirectory); var coordinator = new LocalIdentityCoordinator(store, vault);
        IdentityStatus = local.Id is null || local.PublicSpkiBase64 is null && local.CredentialRef is null ? "身分方案尚未設定" :
            coordinator.Inspect() is IdentityConsistent ? "本機身分檢查通過" : "本機身分遺失或不符，請明確建立新身分";
        V2Receiver? receiver = null;
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
        Listener = new(receiver, new V2ListenerOptions { Port = port }, store);
        Worker = new(store, lifetime: _lifetime.Token);
        _maintenance = Task.Run(MaintainBackups);
    }
    internal static Task<ChatRuntime> OpenAsync(SettingsLoadResult source, bool isolated, int port, ChatWireConfiguration? configuration = null) => Task.Run(() => {
        string root = isolated ? Path.Combine(Path.GetDirectoryName(source.Value.PersistencePath!)!, Path.GetFileNameWithoutExtension(source.Value.PersistencePath) + "-data") : Settings.DirectoryPath;
        string data = Path.Combine(root, "data"); ChatStore? store = null;
        try
        {
            store = ChatStore.Open(new(data) {
                RosterExcludedEndpoints = () => LanInvitation.LocalAddresses().ToArray(),
                TextValidator = configuration is null ? Protocol.ValidText : text => V2Text.Check(text, configuration.MaximumTextBytes) == V2TextProblem.None,
                AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? ""
            });
            var imported = source.CanImportLegacyRoster && source.Value.Contacts.Count <= NekoChat.Core.Roster.RosterCsv.MaxRows ? source.ImportLegacyRoster(store) : null;
            string? warning = !source.CanImportLegacyRoster ? "設定檔無法讀取；原檔保留，未從這份設定匯入名單。" : source.Value.Contacts.Count > NekoChat.Core.Roster.RosterCsv.MaxRows ? "舊名單超過單次匯入上限，尚未匯入；請分批以 CSV 預覽匯入。" : imported is { Skipped: > 0 } ? $"舊名單已匯入；略過 {imported.Skipped} 筆無效或重複資料。" : null;
            var runtime = new ChatRuntime(store, data, Path.Combine(root, "identity"), port, configuration, warning); store = null; return runtime;
        }
        finally { store?.Dispose(); }
    });
    internal void Start() { Listener.Start(); Presence?.Start(); Receipts?.Start(); }
    internal Task<V2SendResult> SendAsync(PreparedSend send, string endpoint, int port, string conversation, DraftEditBuffer buffer, DraftWriteSnapshot sent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_client is null) throw new InvalidOperationException("身分與訊息上限尚未設定，無法傳送私訊。");
            long generation = Volatile.Read(ref _wireGeneration);
            var task = Task.Run(async () => {
                var outcome = await _client.SendPreparedAsync(send, endpoint, port, () => !_lifetime.IsCancellationRequested && generation == Volatile.Read(ref _wireGeneration), _lifetime.Token).ConfigureAwait(false);
                // Finish genuine post-send edits before publishing network completion, independent of the WPF dispatcher or exit.
                if (outcome.Completion is Confirmed { DraftCleared: true } && _store.GetDraftState(conversation) is { } fresh)
                {
                    var update = buffer.ApplyOwnAckClear(sent, fresh);
                    if (update.PreserveEdit is { } edited) buffer.CompleteWrite(edited, edited.Persist(_store, conversation));
                }
                return outcome;
            });
            _network.Add(task); return Finish(task);
        }
    }
    private async Task<T> Finish<T>(Task<T> task)
    { try { return await task.ConfigureAwait(false); } finally { lock (_gate) _network.Remove(task); } }
    internal void ResetAfterResume() { Interlocked.Increment(ref _wireGeneration); Presence?.ResetAfterResume(); Receipts?.ResetAfterResume(); }
    internal async Task StopReceivingAsync()
    { await _lifetime.CancelAsync().ConfigureAwait(false); await Listener.DisposeAsync().ConfigureAwait(false); if (Presence is not null) await Presence.DisposeAsync().ConfigureAwait(false); if (Receipts is not null) await Receipts.DisposeAsync().ConfigureAwait(false); await _maintenance.ConfigureAwait(false); }
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
            finally { _client?.Dispose(); _writer?.Dispose(); _store.Dispose(); _lifetime.Dispose(); }
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
