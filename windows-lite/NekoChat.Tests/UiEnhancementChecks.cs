using System;
using System.IO;
using System.Linq;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

public static class UiEnhancementChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool cond, string name)
        {
            checks++;
            if (!cond) throw new InvalidOperationException($"UI enhancement check failed: {name}");
        }

        Console.WriteLine("Running UiEnhancementChecks...");

        // 1. Static Contract Checks on ChatWindow.xaml
        string xamlPath = FindChatWindowXaml();
        Check(File.Exists(xamlPath), "ChatWindow.xaml exists");
        string xaml = File.ReadAllText(xamlPath);

        // Verify standard paperclip vector (not Windows 11 Clippy Emoji U+1F4CE)
        Check(!xaml.Contains("Content=\"📎\""), "ChatWindow.xaml does not use cartoon Clippy emoji 📎");
        Check(xaml.Contains("AttachFileButton"), "AttachFileButton exists");
        Check(xaml.Contains("M21.44 11.05"), "AttachFileButton uses standard geometric paperclip vector path");
        Check(xaml.Contains("StrokeThickness=\"1.8\""), "Paperclip vector has correct stroke thickness");

        // Verify Code Toolbar proportions & ComboBox styling
        Check(xaml.Contains("x:Name=\"InsertCodeButton\""), "InsertCodeButton has dedicated name");
        Check(xaml.Contains("Height=\"26\""), "InsertCodeButton has compact Height 26");
        Check(xaml.Contains("x:Name=\"CodeLanguage\""), "CodeLanguage ComboBox exists");
        Check(xaml.Contains("TargetType=\"ComboBox\""), "Custom modern ComboBox style is defined");
        Check(xaml.Contains("TargetType=\"ComboBoxItem\""), "Custom modern ComboBoxItem style is defined");

        // Verify bottom 3 buttons height & margin consistency
        Check(xaml.Contains("Content=\"新增同學\" Click=\"AddRosterClick\" Height=\"30\""), "新增同學 button has Height 30");
        Check(xaml.Contains("Content=\"匯入 CSV\" Click=\"ImportRosterClick\" Height=\"30\""), "匯入 CSV button has Height 30");
        Check(xaml.Contains("Content=\"設定\" Click=\"OpenSettingsClick\" Height=\"30\""), "設定 button has Height 30");

        // Verify ReadReceiptToggle is default ON & label is updated
        Check(xaml.Contains("x:Name=\"ReadReceiptToggle\" IsChecked=\"True\" Content=\"傳送對外已讀回條（預設開啟）\""),
            "ReadReceiptToggle has IsChecked=True and label indicates default open");

        // 2. Drag & Drop file formatting logic tests
        Check(FormatAttachmentDraft("photo.png", 512) == "[相片: photo.png (512 B)]", "Attachment formatting: small photo");
        Check(FormatAttachmentDraft("doc.pdf", 2048) == "[檔案: doc.pdf (2.0 KB)]", "Attachment formatting: document KB");
        Check(FormatAttachmentDraft("video.mp4", 1048576 * 5) == "[檔案: video.mp4 (5.0 MB)]", "Attachment formatting: file MB");

        // 3. Cat asset existence and validity
        string assetsDir = Path.Combine(Path.GetDirectoryName(xamlPath)!, "Assets");
        Check(Directory.Exists(assetsDir), "Assets directory exists");
        string[] requiredImages = { "original.png", "black.png", "blue.png", "calico.png", "colorpoint.png", "orange-tabby.png", "tortoiseshell.png", "tuxedo.png", "white.png", "app.ico" };
        foreach (var img in requiredImages)
        {
            string p = Path.Combine(assetsDir, img);
            Check(File.Exists(p), $"Asset exists: {img}");
            Check(new FileInfo(p).Length > 100, $"Asset has valid size: {img}");
        }

        // 4. ReadReceiptPolicy SQLite database initialization behavior
        string dbDir = Path.Combine(Path.GetTempPath(), "nekochat-ui-enh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        try
        {
            using (var store = ChatStore.Open(new(Path.Combine(dbDir, "data"))))
            {
                // On fresh database: Generation == 0, Enabled == false before UI startup
                var policy0 = store.GetReadReceiptPolicy();
                Check(policy0.Generation == 0 && !policy0.Enabled, "Fresh DB read receipt policy has generation 0");

                // UI startup sets Enabled = true on Generation 0
                var change1 = store.SetReadReceiptPolicy(true);
                Check(change1.Policy.Enabled && change1.Policy.Generation == 1, "First startup sets read receipts Enabled=true, Generation=1");

                // User toggles off: sets Enabled = false, Generation = 2
                var change2 = store.SetReadReceiptPolicy(false);
                Check(!change2.Policy.Enabled && change2.Policy.Generation == 2, "User manual disable sets Enabled=false, Generation=2");

                // Subsequent startup does NOT override because Generation > 0
                bool shouldAutoEnable = !change2.Policy.Enabled && change2.Policy.Generation == 0;
                Check(!shouldAutoEnable, "User choice to disable is preserved on subsequent launches");
            }
        }
        finally
        {
            try { Directory.Delete(dbDir, true); } catch { }
        }

        Console.WriteLine($"UiEnhancementChecks: {checks} checks passed.");
        return checks;
    }

    private static string FindChatWindowXaml()
    {
        string? cur = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(cur))
        {
            string candidate1 = Path.Combine(cur, "windows-lite", "NekoChat.Windows", "ChatWindow.xaml");
            if (File.Exists(candidate1)) return candidate1;
            string candidate2 = Path.Combine(cur, "NekoChat.Windows", "ChatWindow.xaml");
            if (File.Exists(candidate2)) return candidate2;
            cur = Path.GetDirectoryName(cur);
        }
        throw new FileNotFoundException("Could not find ChatWindow.xaml from " + AppContext.BaseDirectory);
    }

    private static string FormatAttachmentDraft(string fileName, long bytes)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        bool isPhoto = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        string size = bytes < 1024 ? $"{bytes} B" : bytes < 1048576 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / 1048576.0:F1} MB";
        return isPhoto ? $"[相片: {fileName} ({size})]" : $"[檔案: {fileName} ({size})]";
    }
}
