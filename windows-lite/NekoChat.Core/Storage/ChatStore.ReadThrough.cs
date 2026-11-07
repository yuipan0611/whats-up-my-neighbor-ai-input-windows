namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    /// <summary>Advance the local reading position in the merged classroom/legacy timeline, including unloaded pages.</summary>
    public VisibleReadResult MarkClassroomReadThrough(ClassroomPrincipalKey principal, LegacyHistoryScope? legacy,
        long anchorSequence, bool anchorLegacy, VisibleReadContext context, Func<bool> stillCurrent)
    {
        RequirePrincipal(principal); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (anchorSequence <= 0) throw new ArgumentOutOfRangeException(nameof(anchorSequence));
        if (context.ActiveConversationId != principal.Value || !context.UserOpened || !context.WindowActive || context.Minimized || !context.ViewportReady)
            return new(0, 0, "visibility-guard");
        if (legacy is not null)
        {
            ValidateLegacyScope(legacy);
            if (principal.IsRoster ? legacy.RosterEntryId != principal.RosterEntryId : legacy.RosterEntryId is not null || legacy.UnknownSourceIp != principal.SourceIp)
                return new(0, 0, "scope-guard");
        }
        return Write("classroom-read-through", ctx => {
            if (!stillCurrent()) return (new VisibleReadResult(0, 0, "stale-view"), false);
            string? utc = anchorLegacy
                ? legacy is null ? null : ctx.Scalar("SELECT receivedUtc FROM LegacyMessage WHERE legacySeq=@seq AND " + LegacyScopeWhere,
                    ("@seq", anchorSequence), ("@row", legacy.RosterEntryId), ("@ip", legacy.UnknownSourceIp)) as string
                : ctx.Scalar("SELECT createdUtc FROM ClassroomMessage WHERE principal=@p AND localSeq=@seq", ("@p", principal.Value), ("@seq", anchorSequence)) as string;
            if (utc is null) return (new VisibleReadResult(0, 0, "missing-anchor"), false);
            string classroomBefore = "principal=@p AND direction='incoming' AND localReadAt IS NULL AND (createdUtc<@utc OR (createdUtc=@utc AND (@legacy=1 OR localSeq<=@seq)))";
            var values = new (string Name, object? Value)[] { ("@p", principal.Value), ("@utc", utc), ("@seq", anchorSequence), ("@legacy", anchorLegacy ? 1 : 0), ("@now", ctx.NowUtc) };
            int queued = 0;
            if (ReadPolicy(ctx).Enabled && !BlockedPrincipal(ctx,principal))
                queued = ctx.Exec("INSERT OR IGNORE INTO ClassroomReceiptOutbox(originalLocalSeq,principal,receiptKind,createdUtc) SELECT localSeq,principal,'read',@now FROM ClassroomMessage WHERE " + classroomBefore, values);
            int marked = ctx.Exec("UPDATE ClassroomMessage SET localReadAt=@now WHERE " + classroomBefore, values);
            if (legacy is not null)
                marked += ctx.Exec("UPDATE LegacyMessage SET localReadAt=@now WHERE localReadAt IS NULL AND " + LegacyScopeWhere +
                    " AND (receivedUtc<@utc OR (receivedUtc=@utc AND @legacy=1 AND legacySeq<=@seq))",
                    ("@row", legacy.RosterEntryId), ("@ip", legacy.UnknownSourceIp), ("@utc", utc), ("@seq", anchorSequence), ("@legacy", anchorLegacy ? 1 : 0), ("@now", ctx.NowUtc));
            return (new VisibleReadResult(marked, queued, marked > 0 ? "marked" : "unchanged"), marked > 0);
        }, guardRejected: () => new VisibleReadResult(0, 0, "stale-view"), transactionGuard: _ => stillCurrent());
    }
}
