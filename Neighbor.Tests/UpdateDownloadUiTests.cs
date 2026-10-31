using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    internal static class UpdateDownloadUiTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "neighbor-update-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string mode in new[] { "success", "cancel", "failure" })
                {
                    string cache = Path.Combine(root, mode);
                    using (var form = new UpdateDownloadForm(UpdateDownloadUiFixture.Release, () => UpdateDownloadUiFixture.Downloader(mode), cache))
                    {
                        if (mode == "cancel") form.Shown += (_, e) => form.Controls.OfType<Button>().Single().PerformClick();
                        form.ShowDialog();
                        check(mode == "success" ? form.Error == null && form.DownloadedPath != null && File.ReadAllBytes(form.DownloadedPath).SequenceEqual(UpdateDownloadUiFixture.Payload)
                            : form.DownloadedPath == null && (mode == "cancel" ? form.Error == null : form.Error != null), "Native update dialog " + mode + " completes with expected result");
                        check(Directory.GetFiles(cache, "*.part").Length == 0, "Native update dialog " + mode + " leaves no partial files");
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
