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
    /// PM 定修法 B：原子登記佔位 -> 啟動 work -> 完成時先移除登記與 Dispose CTS，最後回報結果給 TCS。
    /// 徹底解決完成時通知 awaiter 與 finally 清理之間的競態缺陷（Ben 1 & Tom REG 壓力測試）。
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
            ClassroomSendResult? sendResult = null;
            Exception? caughtException = null;
            OperationCanceledException? caughtCanceled = null;

            try
            {
                sendResult = await work(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                caughtCanceled = oce;
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
            finally
            {
                lock (_gate)
                {
                    _sends.Remove(key);
                }
                cts.Dispose();
            }

            if (caughtCanceled != null)
            {
                tcs.TrySetCanceled(caughtCanceled.CancellationToken);
            }
            else if (caughtException != null)
            {
                tcs.TrySetException(caughtException);
            }
            else
            {
                tcs.TrySetResult(sendResult!);
            }
        }, CancellationToken.None);

        return tcs.Task;
    }
}
