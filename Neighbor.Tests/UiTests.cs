using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;
using Shortcut = Neighbor.Core.Shortcut;
using Neighbor.Core;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    public static class UiTests
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        public static void Run(Action<bool, string> check)
        {
            bool sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
            check(sta, "UI checks execute on an STA thread");
            if (!sta) return;
            using (var icon = Theme.LoadIcon()) check(icon.Width > 1, "Embedded tray icon loads");
            using (var image = Theme.LoadCatImage()) check(image.Width > 1, "Embedded black cat image loads");
            SettingsChecks(check);
            ApiKeyChecks(check);
            using (var probe = new NonActivationProbe())
            {
                check((probe.ExtendedStyle & 0x08000080) == 0x08000080, "Floating windows request tool-window and no-activation styles");
                check(probe.MouseActivationResult() == 3, "Mouse interaction does not activate floating windows");
            }
            using (var hud = new RecordingHud())
            {
                var before = GetForegroundWindow();
                hud.ApplySettings(new AppSettings());
                hud.Show(); Application.DoEvents();
                check(GetForegroundWindow() == before, "Showing recording HUD preserves the foreground window");
                foreach (DictationState state in Enum.GetValues(typeof(DictationState))) hud.UpdateState(state, "測試狀態");
                hud.UpdateState(DictationState.Recording, "正在錄音");
                hud.UpdateLevel(-15, true); hud.UpdateLevel(Double.NaN, true); hud.UpdateLevel(-80, false);
                check(hud.AccessibleDescription.Contains("正在錄音"), "Recording HUD exposes current status to accessibility");
                hud.Hide();
            }
            using (var toast = new LearningToast())
            {
                var before = GetForegroundWindow();
                toast.ShowCandidate(new VocabularyCandidate { Original = "測式", Corrected = "測試" }, 2);
                Application.DoEvents();
                check(GetForegroundWindow() == before, "Showing learning toast preserves the foreground window");
                check(toast.AccessibleDescription.Contains("2 秒"), "Learning toast uses the controller's countdown");
                bool rejected = false; toast.Rejected += () => rejected = true;
                Descendants(toast).OfType<Button>().Single(button => button.Text == "不加入").PerformClick();
                check(rejected && !toast.Visible, "Rejecting the learning toast cancels the candidate and hides it");
            }
            VocabularyChecks(check);
        }

        public static void SaveScreenshots(string directory)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("UI screenshots require an STA thread.");
            Directory.CreateDirectory(directory);
            string folder = Path.Combine(Path.GetTempPath(), "neighbor-ui-render-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new VocabularyStore(Path.Combine(folder, "vocabulary.json"));
                var first = store.Add("ChatGPT");
                var second = store.Add("小黑貓"); second.Pinned = true; store.Update(second);
                var third = store.Add("侵權行為"); third.Enabled = false; store.Update(third);
                var candidate = store.AddCandidate(new VocabularyCandidate { Original = "測式", Corrected = "測試", Status = VocabularyStore.Waiting });
                foreach (float scale in new[] { 1F, 1.5F, 2F })
                {
                    using (var settings = new SettingsForm(new AppSettings(), new[] { new AudioDevice { Id = "fixture-mic", Name = "測試用麥克風（示範資料）" } }))
                        RenderForm(settings, directory, "settings", scale);
                    using (var vocabulary = new VocabularyForm(store))
                    {
                        vocabulary.LearningStatus = "示範資料：已貼入文字的修正追蹤可用。";
                        vocabulary.UpdatePrompt(new VocabularyPrompt { IncludedIds = new List<string> { first.Id, second.Id } });
                        RenderForm(vocabulary, directory, "vocabulary", scale);
                    }
                    using (var hud = new RecordingHud())
                    {
                        // The HUD is a per-pixel layered window, so DrawToBitmap would miss its content.
                        hud.UpdateState(DictationState.Recording, "示範資料：正在聽你說話…");
                        hud.UpdateLevel(-15, true);
                        using (var frame = hud.RenderFrame())
                            frame.Save(Path.Combine(directory, "recording-hud-scale100.png"), ImageFormat.Png);
                    }
                    using (var key = new ApiKeyForm(new TestCredentials())) RenderForm(key, directory, "api-key", scale);
                    using (var toast = new LearningToast())
                    {
                        toast.ShowCandidate(candidate, 2);
                        toast.Hide();
                        RenderForm(toast, directory, "learning-toast", scale);
                    }
                }
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                    File.WriteAllText(Path.Combine(directory, "README.txt"),
                        "這些圖片僅使用受控測試資料，未讀取使用者的金鑰、詞典、麥克風或桌面內容。\r\n" +
                        "100 / 150 / 200 代表程式化幾何與字型縮放的版面回歸檢查，不等同已完成真實螢幕 DPI 切換或多螢幕實測。\r\n" +
                        "圖片由各個測試窗體 DrawToBitmap 產生；程式要求縮放後尺寸，實際仍受 runner 視窗上限限制，需以捲軸檢視內容。\r\n" +
                        "錄音浮窗以分層視窗渲染，圖片只提供 100% 版本，且不包含桌面背景上的實際透明效果。\r\n執行環境實際桌面 DPI：" + graphics.DpiX + " × " + graphics.DpiY + "。\r\n",
                        new UTF8Encoding(false));
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        private static void RenderForm(Form form, string directory, string name, float scale)
        {
            form.AutoScaleMode = AutoScaleMode.None;
            var originalSize = form.ClientSize;
            if (scale != 1F)
            {
                form.Scale(new SizeF(scale, scale));
                form.Font = new Font(form.Font.FontFamily, form.Font.Size * scale, form.Font.Style, GraphicsUnit.Point);
            }
            var requested = new Size((int)Math.Round(originalSize.Width * scale), (int)Math.Round(originalSize.Height * scale));
            form.Show(); Application.DoEvents();
            // Restore the virtual fixture size after the production form's small-screen clamp.
            form.ClientSize = requested;
            form.Location = new Point(-30000, -30000);
            var tabs = Descendants(form).OfType<TabControl>().FirstOrDefault();
            int pages = tabs == null ? 1 : tabs.TabPages.Count;
            for (int page = 0; page < pages; page++)
            {
                if (tabs != null) tabs.SelectedIndex = page;
                form.PerformLayout(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(directory, name + "-page" + (page + 1) + "-scale" + (int)Math.Round(scale * 100) + ".png"), ImageFormat.Png);
                }
            }
            form.Hide();
        }

        private static void SettingsChecks(Action<bool, string> check)
        {
            var original = new AppSettings();
            using (var form = new SettingsForm(original, new[] { new AudioDevice { Id = "test-input", Name = "測試麥克風" } }))
            {
                check(!ReferenceEquals(form.Settings, original) && !ReferenceEquals(form.Settings.PrimaryShortcut, original.PrimaryShortcut), "Settings dialog edits a detached settings snapshot");
                var languages = Descendants(form).OfType<ComboBox>().Single(combo => combo.Items.Count == 9);
                var mixed = Descendants(form).OfType<CheckedListBox>().Single();
                check(languages.Items.Count == 9 && mixed.Items.Count == 9, "All nine supported language choices are available");
                mixed.SetItemChecked(2, true); mixed.SetItemChecked(3, true);
                check(mixed.CheckedItems.Count == 2, "Third additional language is refused");
                languages.SelectedIndex = 1;
                check(!mixed.GetItemChecked(1), "Selecting a primary language removes it from additional languages");
                form.Show(); Application.DoEvents();
                Descendants(form).OfType<Button>().Single(button => button.Text == "儲存").PerformClick();
                check(form.DialogResult == DialogResult.OK && form.Settings.PrimaryLanguage == "en" && !form.Settings.AdditionalLanguages.Contains("en"), "Saving settings returns a valid deduplicated language selection");
                check(original.PrimaryLanguage == "zh" && original.AdditionalLanguages.SequenceEqual(new[] { "en" }), "Saving settings never changes the caller's original object");
            }
            var unplugged = new AppSettings { InputDeviceId = "disconnected-device" };
            using (var form = new SettingsForm(unplugged, new AudioDevice[0]))
            {
                var input = Descendants(form).OfType<ComboBox>().Single(combo => combo.Items.Count > 0 && combo.Items[0] is AudioDevice);
                check(input.Items.Count == 2 && ((AudioDevice)input.SelectedItem).Id == "disconnected-device", "Settings preserve the selected microphone when no devices can be enumerated");
                form.Show(); Application.DoEvents();
                Descendants(form).OfType<Button>().Single(button => button.Text == "儲存").PerformClick();
                check(form.DialogResult == DialogResult.OK && form.Settings.InputDeviceId == "disconnected-device", "Settings remain saveable when microphones are unavailable");
            }
            using (var form = new SettingsForm(new AppSettings(), new[] { new AudioDevice { Id = "selected-mic", Name = "測試麥克風" } }))
            {
                var input = Descendants(form).OfType<ComboBox>().Single(combo => combo.Items.Count > 0 && combo.Items[0] is AudioDevice);
                input.SelectedIndex = 1;
                form.Show(); Application.DoEvents();
                Descendants(form).OfType<Button>().Single(button => button.Text == "儲存").PerformClick();
                check(form.Settings.InputDeviceId == "selected-mic", "Selecting a microphone saves its stable device ID");
            }
            using (var editor = new ShortcutEditor())
            {
                editor.Value = new Shortcut { Modifiers = 6, Key = (int)Keys.D };
                check(editor.IsValid && editor.Value.Modifiers == 6 && editor.Value.Key == (int)Keys.D, "Shortcut editor preserves modifier combinations and key codes");
                var detached = editor.Value; detached.Key = (int)Keys.A;
                check(editor.Value.Key == (int)Keys.D, "Shortcut editor returns a detached value");
                editor.Value = new Shortcut { Modifiers = 0, Key = (int)Keys.D };
                check(!editor.IsValid, "Shortcut editor refuses shortcuts without modifiers");
                editor.Value = new Shortcut { Modifiers = 4, Key = (int)Keys.D };
                check(!editor.IsValid, "Shortcut editor refuses Shift-only shortcuts consistently with registration");
                editor.Value = new Shortcut { Modifiers = 3, Key = (int)Keys.F12 };
                check(!editor.IsValid, "Shortcut editor refuses the system-reserved F12 key");
            }
        }

        private static void ApiKeyChecks(Action<bool, string> check)
        {
            var credentials = new TestCredentials();
            using (var form = new ApiKeyForm(credentials))
            {
                var input = Descendants(form).OfType<TextBox>().Single();
                check(input.UseSystemPasswordChar && input.Text.Length == 0, "Saved credential is never prefilled or displayed");
                input.Text = "  fixture-new-key  ";
                form.Show(); Application.DoEvents();
                Descendants(form).OfType<Button>().Single(button => button.Text == "儲存金鑰").PerformClick();
                check(form.DialogResult == DialogResult.OK && credentials.Value == "fixture-new-key" && input.Text.Length == 0,
                    "Saving a replacement key writes it to the credential store and clears the input");
            }
            using (var form = new ApiKeyForm(credentials))
            {
                form.Show(); Application.DoEvents();
                Descendants(form).OfType<Button>().Single(button => button.Text == "移除金鑰").PerformClick();
                check(credentials.Value == "" && Descendants(form).OfType<Label>().Any(label => label.Text == "尚未設定金鑰"),
                    "Removing a key deletes the credential and refreshes its visible status");
                form.Close();
            }
        }

        private static void VocabularyChecks(Action<bool, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "neighbor-ui-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new VocabularyStore(Path.Combine(folder, "vocabulary.json"));
                using (var form = new VocabularyForm(store))
                {
                    // ItemChecked is driven by native ListView notifications. Creating
                    // only the parent Form handle does not create hidden child handles.
                    form.Show(); Application.DoEvents();
                    var entry = store.Add("ChatGPT"); Application.DoEvents();
                    var list = Descendants(form).OfType<ListView>().Single(control => control.CheckBoxes);
                    check(list.IsHandleCreated && list.Visible, "Vocabulary checkbox checks use a visible native ListView");
                    check(list.Items.Count == 1 && list.Items[0].Text == "ChatGPT", "Vocabulary dialog refreshes when the shared store changes");
                    form.UpdatePrompt(new VocabularyPrompt { IncludedIds = new List<string> { entry.Id } });
                    check(list.Items[0].SubItems[1].Text == "已列入辨識提示", "Vocabulary dialog distinguishes applied prompt entries");
                    list.Items[0].Checked = false;
                    Application.DoEvents();
                    check(!store.Entries.Single().Enabled && !new VocabularyStore(Path.Combine(folder, "vocabulary.json")).Entries.Single().Enabled, "Vocabulary checkbox persists the disabled state to disk");
                    list.Items[0].Checked = true;
                    Application.DoEvents();
                    check(store.Entries.Single().Enabled && new VocabularyStore(Path.Combine(folder, "vocabulary.json")).Entries.Single().Enabled, "Vocabulary checkbox persists the re-enabled state to disk");
                    check(list.Columns.Cast<ColumnHeader>().Sum(column => column.Width) <= list.ClientSize.Width,
                        "Vocabulary status columns fit the available list width");
                    var search = Descendants(form).OfType<TextBox>().Single();
                    search.Text = "missing";
                    check(list.Items.Count == 0, "Vocabulary search filters unmatched terms");
                    search.Text = "chatgpt";
                    check(list.Items.Count == 1, "Vocabulary search handles case-insensitive matching");
                    form.LearningStatus = "此欄位不支援學習"; Application.DoEvents();
                    check(Descendants(form).OfType<Label>().Any(label => label.Text == "此欄位不支援學習"), "Learning availability updates are visible");
                }
                store.Add("小黑貓"); // Disposed form must no longer receive callbacks.
                check(store.Entries.Count == 2, "Disposed vocabulary dialog detaches from store updates");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;
                foreach (var nested in Descendants(control)) yield return nested;
            }
        }
        private sealed class TestCredentials : ICredentialStore
        {
            public string Value { get; private set; } = "test-credential-never-display";
            public string Read() { return Value; }
            public void Write(string value) { Value = value; }
            public void Delete() { Value = ""; }
        }
        private sealed class NonActivationProbe : NonActivatingForm
        {
            public int ExtendedStyle { get { return CreateParams.ExStyle; } }
            public int MouseActivationResult()
            {
                var message = Message.Create(IntPtr.Zero, 0x0021, IntPtr.Zero, IntPtr.Zero);
                WndProc(ref message); return message.Result.ToInt32();
            }
        }
    }
}
