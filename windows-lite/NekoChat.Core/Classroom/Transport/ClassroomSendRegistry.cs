using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NekoChat.Core.Classroom.Transport;

public sealed class ClassroomSendAlreadyRunningException : InvalidOperationException
{
    public string MessageId { get; }
    public int TryCount { get; }

    public ClassroomSendAlreadyRunningException(string messageId, int tryCount)
        : base($"Classroom send already in progress for message {messageId} try {tryCount}.")
    {
        MessageId = messageId;
        TryCount = tryCount;
    }
}

public sealed class ClassroomSendRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, int Try), (CancellationTokenSource Cts, Task<ClassroomSendResult> Task)> _sends = [];

    public (CancellationTokenSource Cts, Task<ClassroomSendResult> Task)? TryGet(string messageId, int tryCount)
    {
        lock (_gate)
        {
            return _sends.TryGetValue((messageId, tryCount), out var pair) ? pair : null;
        }
    }

    public bool IsCancellationRequested(string messageId, int tryCount)
    {
        lock (_gate)
        {
            return _sends.TryGetValue((messageId, tryCount), out var pair) && pair.Cts.IsCancellationRequested;
        }
    }

    public bool CanCancel(string messageId, int tryCount)
    {
        lock (_gate)
        {
            return _sends.TryGetValue((messageId, tryCount), out var pair) && !pair.Cts.IsCancellationRequested;
        }
    }

    public void CheckNotRunning(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (_sends.ContainsKey((messageId, tryCount)))
            {
                throw new ClassroomSendAlreadyRunningException(messageId, tryCount);
            }
        }
    }

    public void Register(string messageId, int tryCount, CancellationTokenSource cts, Task<ClassroomSendResult> task)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            var key = (messageId, tryCount);
            if (_sends.ContainsKey(key))
            {
                throw new ClassroomSendAlreadyRunningException(messageId, tryCount);
            }

            _sends.Add(key, (cts, task));
        }
    }

    public bool TryRemove(string messageId, int tryCount)
    {
        lock (_gate)
        {
            return _sends.Remove((messageId, tryCount));
        }
    }

    public bool TryBeginCancel(string messageId, int tryCount, out CancellationTokenSource? cts, out Task<ClassroomSendResult>? sendTask)
    {
        lock (_gate)
        {
            if (_sends.TryGetValue((messageId, tryCount), out var pair))
            {
                cts = pair.Cts;
                sendTask = pair.Task;
                return true;
            }
            cts = null;
            sendTask = null;
            return false;
        }
    }

    /// <summary>
    /// PM 定修法 B：原子登記佔位 -> 啟動 work -> 完成時先移除登記再 Dispose CTS，最後回報結果給 TCS。
    /// 徹底解決 Task.Run 瞬間結束時 finally 早於 Register 執行的競態缺陷。
    /// </summary>
    public Task<ClassroomSendResult> Start(
        string messageId,
        int tryCount,
        Func<CancellationToken, Task<ClassroomSendResult>> work,
        CancellationToken linkedToken)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(work);

        var key = (messageId, tryCount);
        CancellationTokenSource cts;
        var tcs = new TaskCompletionSource<ClassroomSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_sends.ContainsKey(key))
            {
                throw new ClassroomSendAlreadyRunningException(messageId, tryCount);
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            _sends.Add(key, (cts, tcs.Task));
        }

        // 在 lock 外啟動 work
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(cts.Token).ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                lock (_gate)
                {
                    _sends.Remove(key);
                }
                cts.Dispose();
            }
        }, CancellationToken.None);

        return tcs.Task;
    }
}
