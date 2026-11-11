using System.Diagnostics;

namespace NekoChat.Installer;

internal static class UpdateProcessWait
{
    internal static bool WaitForExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { return true; } // Already exited before lookup.
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
