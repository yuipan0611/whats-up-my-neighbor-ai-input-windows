namespace NekoChat.Core.Diagnostics;

public sealed record DiagnosticFailure(DiagnosticStage Stage, DiagnosticFault Fault);

/// <summary>Pure UI failure transitions; never changes storage, delivery or retry state.</summary>
public static class DiagnosticRecoveryPolicy
{
    public static IReadOnlyList<DiagnosticFailure> Report(IReadOnlyList<DiagnosticFailure> failures,
        DiagnosticStage stage, DiagnosticFault fault) => failures
            .Where(failure => failure.Stage != stage || failure.Fault.Code != fault.Code)
            .Append(new(stage, fault)).ToArray();

    // A data stop, then a persistent storage problem, takes priority over a transient UI failure.
    // Enum keys give equally severe failures a stable order independent of event arrival.
    public static DiagnosticFailure? Visible(IReadOnlyList<DiagnosticFailure> failures) => failures
        .OrderByDescending(failure => failure.Fault.StopDataOperations)
        .ThenByDescending(failure => failure.Fault.Code is DiagnosticCode.DiskFull or DiagnosticCode.ReadOnly || NeedsWriteConfirmation(failure))
        .ThenByDescending(failure => failure.Fault.Severity)
        .ThenBy(failure => failure.Stage).ThenBy(failure => failure.Fault.Code).FirstOrDefault();

    public static IReadOnlyList<DiagnosticFailure> Recover(IReadOnlyList<DiagnosticFailure> failures,
        DiagnosticStage stage, bool writeConfirmed = false) => failures
            .Where(failure => !CanRecover(failure, stage, writeConfirmed)).ToArray();

    private static bool CanRecover(DiagnosticFailure failure, DiagnosticStage stage, bool writeConfirmed)
    {
        if (failure.Fault.StopDataOperations || NeedsWriteConfirmation(failure) && !writeConfirmed) return false;
        if (failure.Stage == stage) return true;
        return writeConfirmed && failure.Stage == DiagnosticStage.StorageWrite && failure.Fault.Code is
            DiagnosticCode.StorageBusy or DiagnosticCode.QueueFull or DiagnosticCode.DiskFull or
            DiagnosticCode.ReadOnly or DiagnosticCode.StorageIo;
    }

    private static bool NeedsWriteConfirmation(DiagnosticFailure failure) =>
        failure.Stage is DiagnosticStage.StorageWrite or DiagnosticStage.SendPrepare &&
        failure.Fault.Code is DiagnosticCode.DiskFull or DiagnosticCode.ReadOnly or DiagnosticCode.StorageIo;
}
