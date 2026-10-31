using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core;

namespace NekoChat.Windows;

internal sealed class UpdateDownloadWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status = new() { Text = "正在取得更新檔案…", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 18, Margin = new Thickness(0, 16, 0, 16), Maximum = 100 };
    private readonly Button cancel = new() { Content = "取消下載", Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Right };
    private bool finished;
    internal string? DownloadedPath { get; private set; }
    internal Exception? Error { get; private set; }

    internal UpdateDownloadWindow(Window? owner, UpdateCheckResult release, Func<UpdateDownloader>? createDownloader = null, string? cacheDirectory = null)
    {
        Title = "下載 NekoChat 更新"; Width = 420; Height = 185;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (owner?.IsLoaded == true) Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(cancel); Content = panel;
        void Cancel() { if (finished) return; cancellation.Cancel(); cancel.IsEnabled = false; status.Text = "正在取消下載…"; }
        cancel.Click += (_, _) => Cancel();
        Closing += (_, e) => { if (!finished) { e.Cancel = true; Cancel(); } };
        Loaded += async (_, _) =>
        {
            try
            {
                using var downloader = createDownloader?.Invoke() ?? new UpdateDownloader();
                var updates = new Progress<UpdateDownloadProgress>(p =>
                {
                    if (finished || cancellation.IsCancellationRequested) return;
                    progress.IsIndeterminate = !p.TotalBytes.HasValue;
                    if (p.TotalBytes is > 0) progress.Value = 100.0 * p.BytesReceived / p.TotalBytes.Value;
                    status.Text = p.TotalBytes.HasValue
                        ? $"正在下載更新… {progress.Value:0}%（{p.BytesReceived / 1048576.0:0.0} MB）"
                        : $"正在下載更新… {p.BytesReceived / 1048576.0:0.0} MB";
                });
                var file = await downloader.DownloadAsync(release.DownloadUrl!, release.ChecksumUrl!,
                    cacheDirectory ?? Path.Combine(Settings.DirectoryPath, "Updates"), updates, cancellation.Token);
                if (!cancellation.IsCancellationRequested) DownloadedPath = file;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Error = ex; }
            finally { finished = true; Close(); cancellation.Dispose(); }
        };
    }
}
