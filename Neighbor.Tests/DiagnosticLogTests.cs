using System;
using System.IO;
using Neighbor.Windows.Platform;

namespace Neighbor.Tests
{
    // Failures of optional features must leave a trace; they must not vanish silently.
    public static class DiagnosticLogTests
    {
        public static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "Neighbor-diag-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "diagnostics.log");
                DiagnosticLog.Record(path, "desktop-pet-create", new InvalidOperationException("synthetic failure"));
                string text = File.Exists(path) ? File.ReadAllText(path) : "";
                check(text.Contains("desktop-pet-create") && text.Contains("InvalidOperationException") && text.Contains("synthetic failure"),
                    "Diagnostic log records component, exception type and message");
                DiagnosticLog.Record(path, "desktop-pet-create", new InvalidOperationException("second"));
                check(File.ReadAllText(path).Contains("second"), "Diagnostic log appends instead of overwriting");
                bool survived = true;
                try { DiagnosticLog.Record(Path.Combine(path, "nested", "x.log"), "c", new Exception("x")); }
                catch (Exception) { survived = false; }
                check(survived, "Diagnostic log never throws when the location is unusable");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
