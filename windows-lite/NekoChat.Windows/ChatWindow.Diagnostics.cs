using System.IO;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core.Diagnostics;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private string? _diagnosticsDirectory;
    private bool _exportingDiagnostics;
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
            var journal = _runtime?.Diagnostics ?? (temporary = new DiagnosticJournal(_diagnosticsDirectory, typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"));
            var result = await journal.ExportAsync(destination);
            DiagnosticsStatus.Text = result.Success ? "診斷已匯出。請自行選擇是否分享給開發者。" : "診斷匯出未完成，請重試。（NCD-" + result.Code + "）";
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
