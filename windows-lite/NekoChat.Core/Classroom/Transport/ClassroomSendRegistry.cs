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
}
