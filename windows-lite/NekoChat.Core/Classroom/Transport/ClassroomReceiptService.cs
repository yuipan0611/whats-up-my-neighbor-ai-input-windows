using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Classroom.Transport;

/// <summary>
/// CM-13: Outbound classroom read receipt outbox processor with bounded exponential retry.
/// - Sent only when read receipts policy is enabled (D-10 opt-in).
/// - Contains metadata only (messageId, target).
/// - Backoff: starts at 30s, doubles up to 10m.
/// - Bound: max 8 attempts per event per app session.
/// - Concurrency: max 1 receipt in flight per target IP.
/// - Complete: only verified receipt.ack with status ok/duplicate/discarded completes and removes from outbox.
/// </summary>
public sealed class ClassroomReceiptService : IAsyncDisposable
{
    public const int MaxAttemptsPerEvent = 8;
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);

    private readonly IChatStore _store;
    private readonly ClassroomSender _sender;
    private readonly int _port;
    private readonly string? _ownIp;
    private readonly Func<string, string?>? _ownIpResolver;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object _gate = new();

    private sealed class EventState
    {
        public int Attempts { get; set; }
        public long NextAttemptTimestamp { get; set; }
    }

    private readonly Dictionary<string, EventState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlightTargets = new(StringComparer.Ordinal);

    private Task _loop = Task.CompletedTask;
    private Task _timer = Task.CompletedTask;
    private Task? _disposal;
    private bool _started;
    private long _generation;

    public long TotalAttempts;
    public long TotalCompleted;
    public event Action? ReceiptsChanged;
    public event Action<string>? Diagnostic;

    public ClassroomReceiptService(
        IChatStore store,
        ClassroomSender sender,
        int port = DirectLanMessaging.DefaultPort,
        string? ownIp = null,
        TimeProvider? clock = null,
        Func<string, string?>? ownIpResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _ownIp = ownIp;
        _clock = clock ?? TimeProvider.System;
        _ownIpResolver = ownIpResolver;
    }

    public static TimeSpan CalculateBackoff(int attemptCount)
    {
        if (attemptCount <= 1) return InitialBackoff;
        double seconds = 30 * Math.Pow(2, attemptCount - 1);
        if (seconds > MaximumBackoff.TotalSeconds) seconds = MaximumBackoff.TotalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_started) return;
            _started = true;
            _loop = Task.Run(LoopAsync);
            _timer = Task.Run(TimerAsync);
        }
        Pulse();
    }

    public void Pulse() => _wake.Writer.TryWrite(true);

    public void ResetAfterResume()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _states.Clear();
            _inFlightTargets.Clear();
        }
        Pulse();
    }

    private async Task TimerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _clock);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Pulse();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (bool _ in _wake.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessOutboxBatchAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is StoreException or System.IO.IOException or ObjectDisposedException)
                {
                    Report("receipt_storage_unavailable");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            Report("receipt_service_unavailable");
        }
    }

    public async Task ProcessOutboxBatchAsync()
    {
        if (_lifetime.IsCancellationRequested) return;

        // D-10: Outbound read receipts require policy enabled
        if (!_store.GetReadReceiptPolicy().Enabled)
        {
            lock (_gate)
            {
                _states.Clear();
            }
            return;
        }

        var pending = _store.ListPendingClassroomReceipts(50);
        if (pending.Count == 0)
        {
            lock (_gate)
            {
                _states.Clear();
            }
            return;
        }

        if (pending.Count < 50)
        {
            var activeKeys = new HashSet<string>(pending.Select(r => $"{r.Principal.Value}:{r.MessageId}"), StringComparer.Ordinal);
            lock (_gate)
            {
                var deadKeys = _states.Keys.Where(k => !activeKeys.Contains(k)).ToList();
                foreach (var k in deadKeys) _states.Remove(k);
            }
        }

        long now = _clock.GetTimestamp();

        foreach (var receipt in pending)
        {
            if (_lifetime.IsCancellationRequested) break;

            string key = $"{receipt.Principal.Value}:{receipt.MessageId}";
            EventState state;

            lock (_gate)
            {
                if (!_states.TryGetValue(key, out state!))
                {
                    state = new EventState { Attempts = 0, NextAttemptTimestamp = 0 };
                    _states[key] = state;
                }

                // Bound check: max 8 attempts per app session
                if (state.Attempts >= MaxAttemptsPerEvent)
                {
                    continue;
                }

                // Backoff check
                if (now < state.NextAttemptTimestamp)
                {
                    continue;
                }

                // Concurrency: max 1 receipt in flight per target IP
                if (_inFlightTargets.Contains(receipt.TargetIp))
                {
                    continue;
                }

                _inFlightTargets.Add(receipt.TargetIp);
            }

            bool completed = false;
            try
            {
                state.Attempts++;
                Interlocked.Increment(ref TotalAttempts);

                string? targetOwnIp = _ownIpResolver?.Invoke(receipt.TargetIp) ?? _ownIp;
                if (string.IsNullOrWhiteSpace(targetOwnIp))
                {
                    Report("local_endpoint_unknown");
                    continue;
                }
                var result = await _sender.SendReceiptAsync(
                    receipt,
                    _port,
                    targetOwnIp,
                    () => !_lifetime.IsCancellationRequested && _store.GetReadReceiptPolicy().Enabled,
                    _lifetime.Token).ConfigureAwait(false);

                if (result.Completed)
                {
                    completed = true;
                    Interlocked.Increment(ref TotalCompleted);
                    try { ReceiptsChanged?.Invoke(); } catch { }
                }
                else if (result.Status == "capability_missing")
                {
                    // Receiver does not support read receipts: don't retry 8 times
                    state.Attempts = MaxAttemptsPerEvent;
                }
            }
            catch (Exception)
            {
                Report("receipt_send_failed");
            }
            finally
            {
                lock (_gate)
                {
                    _inFlightTargets.Remove(receipt.TargetIp);
                    if (completed)
                    {
                        _states.Remove(key);
                    }
                    else if (state.Attempts < MaxAttemptsPerEvent)
                    {
                        TimeSpan backoff = CalculateBackoff(state.Attempts);
                        long ticks = (long)(backoff.TotalSeconds * _clock.TimestampFrequency);
                        state.NextAttemptTimestamp = _clock.GetTimestamp() + ticks;
                    }
                }
            }
        }
    }

    private void Report(string code)
    {
        try { Diagnostic?.Invoke(code); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposal ??= StopAsync());
        }
    }

    private async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _wake.Writer.TryComplete();
        await Task.WhenAll(_loop, _timer).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
