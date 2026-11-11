using System.Diagnostics;
using NekoChat.Installer;

namespace NekoChat.Tests;

internal static class UpdateProcessWaitChecks
{
    internal static int Run()
    {
        if (!UpdateProcessWait.WaitForExit(0, TimeSpan.Zero)) throw new Exception("Optional wait PID failed");
        if (!UpdateProcessWait.WaitForExit(int.MaxValue, TimeSpan.Zero)) throw new Exception("Exited PID failed");
        using var current = Process.GetCurrentProcess();
        if (UpdateProcessWait.WaitForExit(current.Id, TimeSpan.FromMilliseconds(10)))
            throw new Exception("Live app must abort installation on timeout");
        if (current.HasExited) throw new Exception("Timed-out app must remain running");
        Console.WriteLine("Update process handoff: 4 optional/exited/live-PID and no-kill checks passed.");
        return 4;
    }
}
