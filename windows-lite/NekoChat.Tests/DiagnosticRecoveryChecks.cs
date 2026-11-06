using NekoChat.Core.Diagnostics;

namespace NekoChat.Tests;

internal static class DiagnosticRecoveryChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException("Diagnostic recovery: " + label); }
        IReadOnlyList<DiagnosticFailure> failures = [];
        var disk = DiagnosticFault.For(DiagnosticCode.DiskFull);
        var busy = DiagnosticFault.For(DiagnosticCode.QueueFull);
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.StorageWrite, disk);
        var before = failures;
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.UiRefresh, busy);
        Check(failures.Count == 2 && before.Count == 1, "M1 adding transient failure preserves unresolved DiskFull without mutating prior state");
        Check(DiagnosticRecoveryPolicy.Visible(failures)?.Fault.Code == DiagnosticCode.DiskFull, "persistent storage failure stays visible over transient failure");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.UiRefresh);
        Check(failures.Count == 1 && failures[0].Fault.Code == DiagnosticCode.DiskFull, "M1 transient recovery removes only the matching failure");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.StorageWrite);
        Check(failures.Count == 1, "read or unconfirmed completion cannot clear DiskFull");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.SendPrepare, writeConfirmed: true);
        Check(failures.Count == 0 && DiagnosticRecoveryPolicy.Visible(failures) is null, "confirmed write clears recovered storage warning");
        Check(DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.SendPrepare, true).Count == 0, "repeated success does not invent another recovery");
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.StorageWrite, disk);
        Check(failures.Count == 1, "new failure re-arms after recovery");
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.StorageWrite, disk);
        Check(failures.Count == 1, "repeated same stage and code remain one unresolved failure");
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.StorageWrite, busy);
        Check(failures.Count == 2, "different failure codes in one stage are retained separately");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.StorageWrite);
        Check(failures.Count == 1 && failures[0].Fault.Code == DiagnosticCode.DiskFull, "same-stage transient success cannot hide persistent fault");
        failures = DiagnosticRecoveryPolicy.Report(failures, DiagnosticStage.UiAction, DiagnosticFault.For(DiagnosticCode.CorruptData));
        Check(DiagnosticRecoveryPolicy.Visible(failures)?.Fault.Code == DiagnosticCode.CorruptData, "data-integrity stop outranks recoverable disk failure");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.UiAction);
        Check(failures.Count == 2, "ordinary success cannot clear data-integrity stop");
        failures = DiagnosticRecoveryPolicy.Recover(failures, DiagnosticStage.SendPrepare, true);
        Check(failures.Count == 1 && failures[0].Fault.StopDataOperations, "confirmed write leaves unrelated fatal failure unresolved");
        foreach (var code in new[] { DiagnosticCode.ReadOnly, DiagnosticCode.StorageIo })
        {
            IReadOnlyList<DiagnosticFailure> pending = [new(DiagnosticStage.StorageWrite, DiagnosticFault.For(code))];
            Check(DiagnosticRecoveryPolicy.Recover(pending, DiagnosticStage.UiRefresh).Count == 1, code + " survives unrelated refresh");
            Check(DiagnosticRecoveryPolicy.Recover(pending, DiagnosticStage.SendPrepare, true).Count == 0, code + " clears on confirmed write");
        }
        IReadOnlyList<DiagnosticFailure> warnings = [new(DiagnosticStage.UiRead, busy)];
        Check(DiagnosticRecoveryPolicy.Recover(warnings, DiagnosticStage.UiRefresh).Count == 1, "success in another UI flow leaves warning pending");
        Check(DiagnosticRecoveryPolicy.Recover(warnings, DiagnosticStage.UiRead).Count == 0, "matching UI flow clears its transient warning");
        IReadOnlyList<DiagnosticFailure> readIo = [new(DiagnosticStage.UiRefresh, DiagnosticFault.For(DiagnosticCode.StorageIo))];
        Check(DiagnosticRecoveryPolicy.Recover(readIo, DiagnosticStage.UiRefresh).Count == 0, "successful read refresh recovers its own read I/O failure without requiring a send");
        IReadOnlyList<DiagnosticFailure> ranked = [new(DiagnosticStage.UiRead, busy), new(DiagnosticStage.UiAction, DiagnosticFault.For(DiagnosticCode.Unexpected))];
        Check(DiagnosticRecoveryPolicy.Visible(ranked)?.Fault.Code == DiagnosticCode.Unexpected, "error outranks temporary warning");
        Check(DiagnosticRecoveryPolicy.Visible(ranked.Reverse().ToArray()) == DiagnosticRecoveryPolicy.Visible(ranked), "visible choice is independent of arrival order");
        Console.WriteLine($"Diagnostic recovery: {checks} checks passed.");
        return checks;
    }
}
