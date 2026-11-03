namespace NekoChat.Core.Classroom.Transport;

/// <summary>A codec's decision about a complete request head. Route names are opaque to the stream reader.</summary>
public sealed record ClassroomFrameHead(string Route, int ContentLength, int MaximumBodyBytes);
public sealed record ClassroomHeadDecision(string Code, ClassroomFrameHead? Head = null);
public sealed record ClassroomRequestFrame(string Route, ReadOnlyMemory<byte> Body);
public sealed record ClassroomFrameReadResult(string Code, ClassroomRequestFrame? Frame = null)
{
    public bool Success => Code == "ok" && Frame is not null;
}

/// <summary>
/// One-request stream framing. The caller supplies the frozen classroom codec's strict header validator;
/// this class only bounds reads and allocations. It makes no source, identity, session, or storage claim.
/// The owner closes the connection after the result and supplies its accept-to-close deadline token.
/// </summary>
public static class ClassroomStreamFraming
{
    public const int MaximumHeadBytes = 8192;
    public const int MaximumMessageBodyBytes = 6 * 65536 + 4096;

    public static async Task<ClassroomFrameReadResult> ReadRequestAsync(Stream stream,
        Func<ReadOnlyMemory<byte>, ClassroomHeadDecision> validateHead, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(validateHead);
        byte[] header = new byte[MaximumHeadBytes];
        int count = 0, end = -1;
        while (end < 0 && count < header.Length)
        {
            int previous = count;
            int read = await stream.ReadAsync(header.AsMemory(count), cancellation).ConfigureAwait(false);
            if (read == 0) return new("invalid_payload");
            count += read;
            for (int i = Math.Max(0, previous - 3); i <= count - 4; i++)
                if (header[i] == 13 && header[i + 1] == 10 && header[i + 2] == 13 && header[i + 3] == 10)
                { end = i + 4; break; }
        }
        // A head without the terminating blank line within 8 KiB is rejected before the codec or any body allocation.
        if (end < 0) return new("invalid_payload");
        var decision = validateHead(header.AsMemory(0, end));
        if (decision.Code != "ok") return new(decision.Code);
        if (decision.Head is not { } head || string.IsNullOrEmpty(head.Route) || head.ContentLength <= 0 ||
            head.MaximumBodyBytes is < 1 or > MaximumMessageBodyBytes)
            throw new InvalidOperationException("The classroom head codec returned an invalid frame specification.");
        // Bound before allocating or waiting for the body. Even a defective codec cannot remove the global bound.
        if (head.ContentLength > head.MaximumBodyBytes) return new("payload_too_large");
        int buffered = count - end;
        if (buffered > head.ContentLength) return new("invalid_payload");
        byte[] body = new byte[head.ContentLength];
        header.AsMemory(end, buffered).CopyTo(body);
        while (buffered < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(buffered), cancellation).ConfigureAwait(false);
            if (read == 0) return new("invalid_payload");
            buffered += read;
        }
        cancellation.ThrowIfCancellationRequested();
        return new("ok", new(head.Route, body));
    }
}
