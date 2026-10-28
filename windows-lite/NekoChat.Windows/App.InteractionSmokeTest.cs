using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunInteractionSmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        GetCursorPos(out var originalCursor);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        int exitCode = 0;
        try
        {
            int presses = 0, openings = 0;
            pet.Cat.MouseLeftButtonDown += (_, _) => presses++;
            pet.OpenChat += _ => openings++;
            chat.Hide();
            pet.Left = SystemParameters.WorkArea.Left + 30;
            pet.Top = SystemParameters.WorkArea.Top + 30;
            await Task.Delay(250, timeout.Token);

            ClickCat();
            await UntilAsync(() => presses == 1, timeout.Token);
            await Task.Delay(150, timeout.Token);
            Require(openings == 0 && !chat.IsVisible, "Single left click opened chat");

            await Task.Delay((int)GetDoubleClickTime() + 100, timeout.Token);
            ClickCat();
            await Task.Delay(70, timeout.Token);
            ClickCat();
            await UntilAsync(() => presses == 3 && openings > 0, timeout.Token);
            Require(openings == 1 && chat.IsVisible, "Double left click did not open chat exactly once");
            Capture(chat, Path.Combine(directory, "double-click-chat.png"));

            chat.Hide();
            await Task.Delay((int)GetDoubleClickTime() + 100, timeout.Token);
            // The second press of a double click becomes a drag, and must not open chat.
            ClickCat();
            await Task.Delay(70, timeout.Token);
            var start = CatScreenPoint();
            SetCursorPos((int)start.X, (int)start.Y);
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            await UntilAsync(() => presses == 5, timeout.Token);
            double beforeLeft = pet.Left;
            SetCursorPos((int)start.X + 50, (int)start.Y + 30);
            await UntilAsync(() => Math.Abs(pet.Left - beforeLeft) > 10, timeout.Token);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(150, timeout.Token);
            Require(openings == 1 && !chat.IsVisible, "Releasing a drag opened chat");
            // Verify timer-driven window movement, not merely changing walk sprites.
            pet.SetWanderEnabled(true);
            pet.Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - pet.Width) / 2);
            var beforeWander = pet.Left;
            await UntilAsync(() => Math.Abs(pet.Left - beforeWander) >= 8, timeout.Token);
            pet.SetWanderEnabled(false);
            var pausedLeft = pet.Left;
            await Task.Delay(250, timeout.Token);
            Require(Math.Abs(pet.Left - pausedLeft) < .01, "Paused wandering still moved the pet");
            Capture(pet, Path.Combine(directory, "automatic-wandering.png"));
            File.WriteAllText(Path.Combine(directory, "result.txt"),
                "PASS: native Windows mouse input; single left click stays closed; double left click opens chat exactly once; double-click drag moves cat and release stays closed. timer-driven wandering changes actual window position; disabling wandering stops movement. Bubble reply is covered by messaging smoke.");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + ex);
        }
        finally
        {
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
        Environment.Exit(exitCode);

        Point CatScreenPoint() => pet.Cat.PointToScreen(new Point(pet.Cat.ActualWidth / 2, pet.Cat.ActualHeight / 2));
        void ClickCat()
        {
            var point = CatScreenPoint();
            SetCursorPos((int)point.X, (int)point.Y);
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
