using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunInstanceSmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            Require(directListener?.Running == true, "Primary receiver did not start");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--instance-probe");
            start.ArgumentList.Add(Path.GetFullPath(directory));
            using var child = Process.Start(start) ?? throw new IOException("Duplicate app did not start");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(true); throw; }
            Require(child.ExitCode == 0 && File.ReadAllText(Path.Combine(directory, "duplicate-result.txt")).StartsWith("PASS:"),
                "Duplicate launch was not blocked");
            Require(directListener!.Running && chat.IsVisible && pet.IsVisible && chat.SendButton.IsEnabled,
                "Duplicate launch disrupted the first app");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: real second app process blocked before settings/listener/UI; original receiver, chat and cat remain active.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + ex);
            Environment.Exit(1);
        }
    }

    private void RunReceiverFailureSmokeTest(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            Require(directListener?.Running == false && !string.IsNullOrWhiteSpace(directListener.Error),
                "Test must occupy production receiver port before starting app");
            Require(!chat.SendButton.IsEnabled && chat.ReceiverStatus.Text.Contains("無法接收訊息") &&
                chat.ReceiverStatus.Text.Contains("8768"), "Receiver failure is not visible or sending remains enabled");
            chat.RecipientIp.Text = "127.0.0.1";
            chat.Draft.Text = "接收失敗不能假裝送出";
            chat.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chat.Draft.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chat), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            chat.SetStatus("操作其他設定後，接收錯誤仍須保留");
            Require(chat.Draft.Text == "接收失敗不能假裝送出" && chat.History.Text.Length == 0 &&
                chat.ReceiverStatus.Text.Contains("無法接收訊息"), "Blocked send or persistent error lost state");
            Capture(chat, Path.Combine(directory, "receiver-unavailable.png"));
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: occupied TCP 8768 produces persistent visible receiver error; sending disabled, no history and draft retained.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + ex);
            Environment.Exit(1);
        }
    }
}
