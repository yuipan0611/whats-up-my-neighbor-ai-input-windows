using System.IO;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core.Diagnostics;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private string? _diagnosticsDirectory;
    private (DiagnosticStage Stage, DiagnosticFault Fault)? _visibleDiagnosticFailure;
    private bool _exportingDiagnostics;
    private void ClearRecoveredDiagnostic(DiagnosticStage stage, bool writeConfirmed = false)
    {
        if (_visibleDiagnosticFailure is not { } failure || failure.Fault.StopDataOperations) return;
        bool sameFlow = failure.Stage == stage;
        bool recoveredWrite = writeConfirmed && failure.Stage == DiagnosticStage.StorageWrite &&
            failure.Fault.Code is DiagnosticCode.StorageBusy or DiagnosticCode.QueueFull or DiagnosticCode.DiskFull or DiagnosticCode.ReadOnly or DiagnosticCode.StorageIo;
        if (!sameFlow && !recoveredWrite) return;
        _visibleDiagnosticFailure = null; StorageStatus.Text = _runtime?.StartupWarning ?? "";
        ReloadData.Visibility = Visibility.Collapsed;
        _runtime?.Diagnostics.Record(stage, DiagnosticOutcome.Recovered);
    }
    internal void SetDiagnosticsDirectory(string directory)
    {
        _diagnosticsDirectory = directory; ExportDiagnosticsButton.IsEnabled = !_closingRequested && !_exportingDiagnostics;
        if (_runtime?.Diagnostics is { WriteFailures: > 0, PersistentAvailable: false }) DiagnosticsStatus.Text = "本機記錄空間無法使用，仍可匯出最近的診斷事件。";
    }
    private async void ExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_exportingDiagnostics || _closingRequested) return;
        var picker = new SaveFileDialog { FileName = "NekoChat-診斷-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", Filter = "診斷壓縮檔|*.zip", DefaultExt = ".zip", OverwritePrompt = true };
        if (picker.ShowDialog(this) == true) await ExportDiagnosticsAsync(picker.FileName);
    }
    internal async Task<DiagnosticExportResult> ExportDiagnosticsAsync(string destination)
    {
        if (_exportingDiagnostics || _diagnosticsDirectory is null) return new(false, DiagnosticCode.QueueFull);
        _exportingDiagnostics = true; ExportDiagnosticsButton.IsEnabled = false; DiagnosticsStatus.Text = "正在整理診斷記錄…";
        DiagnosticJournal? temporary = null;
        try
        {
            var journal = _runtime?.Diagnostics ?? (temporary = new DiagnosticJournal(_diagnosticsDirectory, ChatRuntime.DiagnosticVersion));
            var result = await journal.ExportAsync(destination);
            journal.Record(DiagnosticStage.Journal, result.Success ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Failed, result.Code);
            DiagnosticsStatus.Text = result.Success ? journal.PersistentAvailable ? "診斷已匯出。請自行選擇是否分享給開發者。" : "近期診斷已匯出；本機記錄空間無法使用，較早事件可能未保留。" : "診斷匯出未完成，請重試。（NCD-" + result.Code + "）";
            return result;
        }
        catch (Exception error) when (DiagnosticFault.Catchable(error))
        {
            var fault = DiagnosticFault.FromException(error);
            DiagnosticsStatus.Text = "診斷匯出未完成，請重試。（" + fault.DisplayCode + "）";
            return new(false, fault.Code);
        }
        finally
        {
            if (temporary is not null) await temporary.DisposeAsync();
            _exportingDiagnostics = false; ExportDiagnosticsButton.IsEnabled = !_closingRequested;
        }
    }
}
