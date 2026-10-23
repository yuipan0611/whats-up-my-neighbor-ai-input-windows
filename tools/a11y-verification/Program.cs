using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Neighbor.Core;
using Neighbor.Windows.UI;

// Synthetic-data host for an interactive accessibility check. It shows a dummy input box and the HUD,
// records the actions the HUD raises, and obeys one-line commands from a file. No audio, no API.
internal static class Host
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // Test fixture only: the dummy input box must be foreground to prove that eye actions keep focus.
    // The HUD itself never calls SetForegroundWindow.
    private static void ForceForegroundForFixture(Form input, TextBox box)
    {
        IntPtr current = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(current, IntPtr.Zero);
        uint ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try { SetForegroundWindow(input.Handle); input.Activate(); box.Focus(); }
        finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
    }

    private static string dir;

    private static void Log(string line)
    {
        File.AppendAllText(Path.Combine(dir, "events.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        dir = args[0];
        Application.EnableVisualStyles();
        var input = new Form { Text = "synthetic input host", StartPosition = FormStartPosition.Manual, Location = new Point(120, 120), Size = new Size(420, 220) };
        var box = new TextBox { Dock = DockStyle.Fill, Text = "synthetic text" };
        input.Controls.Add(box);
        var hud = new RecordingHud();
        hud.StartRequested += () => Log("EVENT Start");
        hud.FinishRequested += () => Log("EVENT Finish");
        hud.CancelRequested += () => Log("EVENT Cancel");
        hud.RetryRequested += () => Log("EVENT Retry");
        hud.UpdateState(DictationState.Idle, "synthetic idle");
        input.Show();
        input.Activate();
        box.Focus();
        hud.Show();
        Log("SETUP foregroundIsInput=" + (GetForegroundWindow() == input.Handle) + " boxFocused=" + box.Focused);
        File.WriteAllText(Path.Combine(dir, "hud.hwnd"), hud.Handle.ToInt64().ToString());
        File.WriteAllText(Path.Combine(dir, "input.hwnd"), input.Handle.ToInt64().ToString());
        Log("READY");

        var timer = new Timer { Interval = 100 };
        timer.Tick += (s, e) =>
        {
            string path = Path.Combine(dir, "cmd.txt");
            if (!File.Exists(path)) return;
            string command = File.ReadAllText(path).Trim();
            File.Delete(path);
            Log("CMD " + command);
            var parts = command.Split(' ');
            switch (parts[0])
            {
                case "state":
                    DictationState parsed;
                    if (Enum.TryParse(parts[1], out parsed)) hud.UpdateState(parsed, "synthetic " + parts[1]);
                    else Log("ERROR unknown state " + parts[1]);
                    break;
                case "hide": hud.Hide(); break;
                case "show": hud.Show(); break;
                case "move": hud.Location = new Point(int.Parse(parts[1]), int.Parse(parts[2])); break;
                case "activate": ForceForegroundForFixture(input, box); Log("ACTIVATE inputIsForeground=" + (GetForegroundWindow() == input.Handle) + " boxFocused=" + box.Focused); break;
                case "focus": Log("FOCUS foreground=" + GetForegroundWindow().ToInt64() + " inputIsForeground=" + (GetForegroundWindow() == input.Handle) + " boxFocused=" + box.Focused); break;
                case "close":
                    hud.Dispose();
                    Log("CLOSED");
                    timer.Stop();
                    input.Close();
                    Application.ExitThread();
                    break;
            }
        };
        timer.Start();
        Application.Run(input);
    }
}
