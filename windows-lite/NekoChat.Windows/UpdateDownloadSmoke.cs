using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NekoChat.Windows;

internal static class UpdateDownloadSmoke
{
    internal static int Run(string directory)
    {
        string root = Path.GetFullPath(directory);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (string mode in new[] { "success", "cancel", "failure" })
            {
                string cache = Path.Combine(root, mode);
                var window = new UpdateDownloadWindow(null, UpdateDownloadUiFixture.Release, () => UpdateDownloadUiFixture.Downloader(mode), cache);
                if (mode == "cancel") window.Loaded += (_, _) => ((StackPanel)window.Content).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ShowDialog();
                if (mode == "success")
                {
                    if (window.Error is not null || window.DownloadedPath is null || !File.ReadAllBytes(window.DownloadedPath).SequenceEqual(UpdateDownloadUiFixture.Payload))
                        throw new IOException("Verified native download did not complete.");
                }
                else if (window.DownloadedPath is not null || Directory.GetFiles(cache, "*.exe").Length != 0 ||
                         (mode == "cancel" ? window.Error is not null : window.Error is null))
                    throw new IOException("Native cancellation/error handling failed.");
                if (Directory.GetFiles(cache, "*.part").Length != 0) throw new IOException("Native dialog left partial files.");
            }
            File.WriteAllText(Path.Combine(root, "result.txt"), "PASS: native WPF download progress, verified file completion, cancel button, checksum failure and partial cleanup; synthetic transport only; no browser, installer execution, user settings or real network.");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "result.txt"), "FAIL: " + ex); return 1; }
    }
}
