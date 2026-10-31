using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Neighbor.Core;

namespace Neighbor.Windows.UI
{
    internal sealed class UpdateDownloadForm : Form
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Label status = new Label { Text = "正在取得更新檔案…", AutoSize = false, Left = 20, Top = 18, Width = 380, Height = 40 };
        private readonly ProgressBar progress = new ProgressBar { Left = 20, Top = 66, Width = 380, Height = 18, Style = ProgressBarStyle.Marquee };
        private readonly Button cancel = new Button { Text = "取消下載", Left = 295, Top = 100, Width = 105, Height = 30 };
        private bool finished;
        internal string DownloadedPath { get; private set; }
        internal Exception Error { get; private set; }
        internal UpdateDownloadForm(UpdateCheckResult release, Func<UpdateDownloader> createDownloader = null, string cacheDirectory = null)
        {
            Text = "下載 What's up my neighbor 更新"; ClientSize = new System.Drawing.Size(420, 150);
            StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; Controls.AddRange(new Control[] { status, progress, cancel });
            Action stop = () => { if (finished) return; cancellation.Cancel(); cancel.Enabled = false; status.Text = "正在取消下載…"; };
            cancel.Click += (_, e) => stop();
            FormClosing += (_, e) => { if (!finished) { e.Cancel = true; stop(); } };
            Shown += async (_, e) =>
            {
                try
                {
                    using (var downloader = createDownloader?.Invoke() ?? new UpdateDownloader())
                    {
                        var updates = new Progress<UpdateDownloadProgress>(p =>
                        {
                            if (finished || cancellation.IsCancellationRequested || IsDisposed) return;
                            progress.Style = p.TotalBytes.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                            if (p.TotalBytes > 0) progress.Value = Math.Min(100, (int)(100.0 * p.BytesReceived / p.TotalBytes.Value));
                            status.Text = p.TotalBytes.HasValue
                                ? string.Format("正在下載更新… {0}%（{1:0.0} MB）", progress.Value, p.BytesReceived / 1048576.0)
                                : string.Format("正在下載更新… {0:0.0} MB", p.BytesReceived / 1048576.0);
                        });
                        var file = await downloader.DownloadAsync(release.DownloadUrl, release.ChecksumUrl,
                            cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsUpMyNeighbor", "Updates"), updates, cancellation.Token);
                        if (!cancellation.IsCancellationRequested) DownloadedPath = file;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error = ex; }
                finally { finished = true; Close(); cancellation.Dispose(); }
            };
        }
    }
}
