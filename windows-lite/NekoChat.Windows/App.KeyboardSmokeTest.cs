using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunKeyboardSmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = new ChatWindow(new Settings { AutoStartOnLogin = false });
        int sends = 0;
        string? sent = null;
        probe.SendRequested += (_, text) => { sends++; sent = text; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int exitCode = 0;
        try
        {
            chat.Hide();
            probe.Show();
            probe.Activate();
            probe.SetReceiverState(true, 8768, null);
            probe.RecipientIp.Text = "127.0.0.1";
            probe.Draft.Focus();
            Keyboard.Focus(probe.Draft);
            await Task.Delay(100, timeout.Token);
            Require(probe.Draft.IsKeyboardFocused, "Draft did not gain keyboard focus");

            probe.Draft.Text = "按 Enter 送出的中文 🐈";
            await PressReturnAsync();
            Require(sends == 1 && sent == probe.Draft.Text && !probe.Draft.Text.Contains('\n'),
                "Native Enter did not send exactly once or inserted a newline");

            probe.Draft.Text = "第一行";
            probe.Draft.CaretIndex = probe.Draft.Text.Length;
            KeybdEvent(0x10, 0, 0, UIntPtr.Zero);
            await PressReturnAsync();
            KeybdEvent(0x10, 0, 2, UIntPtr.Zero);
            await Task.Delay(100, timeout.Token);
            Require(sends == 1 && probe.Draft.Text.Contains('\n'), "Shift+Enter sent instead of inserting a newline");

            probe.Draft.Text = "傳送中保留";
            probe.SendButton.IsEnabled = false;
            await PressReturnAsync();
            Require(sends == 1 && probe.Draft.Text == "傳送中保留", "Disabled sender accepted Enter");
            probe.SendButton.IsEnabled = true;

            // Routed events exercise the IME guard, not an installed native IME.
            var composition = new TextComposition(InputManager.Current, probe.Draft, "注音組字");
            probe.Draft.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            var composingEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(probe),
                Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            probe.Draft.RaiseEvent(composingEnter);
            Require(sends == 1, "Composition confirmation sent a message");
            probe.Draft.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            probe.Draft.Text = "選字完成後再 Enter";
            await PressReturnAsync();
            Require(sends == 2 && sent == "選字完成後再 Enter", "Enter did not resume after composition");

            Capture(probe, Path.Combine(directory, "keyboard-chat.png"));
            File.WriteAllText(Path.Combine(directory, "result.txt"),
                "PASS: native Windows Enter sends once without newline; native Shift+Enter inserts a newline without sending; " +
                "disabled sender retains draft; routed IME composition confirmation does not send; Enter resumes after composition. " +
                "Installed Chinese IME and cross-machine delivery remain manual checks.");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + ex);
        }
        finally
        {
            KeybdEvent(0x0D, 0, 2, UIntPtr.Zero);
            KeybdEvent(0x10, 0, 2, UIntPtr.Zero);
        }
        Environment.Exit(exitCode);

        async Task PressReturnAsync()
        {
            KeybdEvent(0x0D, 0, 0, UIntPtr.Zero);
            KeybdEvent(0x0D, 0, 2, UIntPtr.Zero);
            await Task.Delay(120, timeout.Token);
        }
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte key, byte scan, uint flags, UIntPtr extra);
}
