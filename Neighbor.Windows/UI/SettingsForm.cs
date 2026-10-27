using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Shortcut = Neighbor.Core.Shortcut;
using Neighbor.Core;

namespace Neighbor.Windows.UI
{
    public sealed class SettingsForm : Form
    {
        public static readonly string[] LanguageCodes = { "zh", "en", "ja", "ko", "th", "vi", "es", "fr", "de" };
        private static readonly string[] LanguageNames = { "繁體中文", "英文", "日文", "韓文", "泰文", "越南文", "西班牙文", "法文", "德文" };
        private readonly ComboBox primary = Combo();
        private readonly CheckedListBox additional = new CheckedListBox { CheckOnClick = true, Height = 190, Width = 470, BorderStyle = BorderStyle.FixedSingle };
        private readonly ComboBox mode = Combo(), alternateMode = Combo(), silence = Combo(), devices = Combo(), position = Combo();
        private readonly CheckBox numbers = new CheckBox { Text = "將中文數字轉成阿拉伯數字", AutoSize = true };
        private readonly CheckBox retry = new CheckBox { Text = "語言疑似誤判時，自動重試一次", AutoSize = true };
        private readonly CheckBox startup = new CheckBox { Text = "登入 Windows 時啟動", AutoSize = true };
        private readonly CheckBox soundEffects = new CheckBox { Text = "播放錄音與辨識提示音效（上課／安靜環境建議關閉）", AutoSize = true };
        private readonly CheckBox alternateEnabled = new CheckBox { Text = "啟用第二組快捷鍵", AutoSize = true };
        private readonly ShortcutEditor firstShortcut = new ShortcutEditor(), secondShortcut = new ShortcutEditor();
        private readonly Label validation = Theme.Label("", true);
        public AppSettings Settings { get; private set; }

        public SettingsForm(AppSettings settings, IReadOnlyList<AudioDevice> audioDevices)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Settings = settings.Clone();
            Theme.Style(this, "小黑貓 · 設定", new Size(610, 624));
            MaximizeBox = false;
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8) };
            var recordingPage = Page(tabs, "錄音與外觀");
            var languagePage = Page(tabs, "辨識與文字");
            var shortcutsPage = Page(tabs, "快捷鍵");
            controls(recordingPage, Theme.Label("輸入麥克風"), devices,
                Theme.Label("使用預設裝置時，會在每次開始錄音時重新選擇。", true),
                Theme.Label("靜音自動停止"), silence,
                Theme.Label("每段最長五分鐘；也可隨時按快捷鍵或貓咪右眼完成。", true),
                Theme.Label("小黑貓位置"), position, startup, soundEffects);
            devices.Items.Add(new AudioDevice { Id = null, Name = "Windows 預設麥克風" });
            foreach (var device in audioDevices ?? new AudioDevice[0]) devices.Items.Add(device);
            devices.SelectedIndex = 0;
            for (int i = 1; i < devices.Items.Count; i++)
                if (((AudioDevice)devices.Items[i]).Id == settings.InputDeviceId) devices.SelectedIndex = i;
            if (!String.IsNullOrEmpty(settings.InputDeviceId) && devices.SelectedIndex == 0)
            {
                devices.Items.Add(new AudioDevice { Id = settings.InputDeviceId, Name = "先前選擇的麥克風（目前未連接）" });
                devices.SelectedIndex = devices.Items.Count - 1;
            }
            silence.Items.AddRange(new object[] { "關閉", "3 秒", "5 秒", "10 秒" });
            silence.SelectedIndex = Array.IndexOf(new[] { 0, 3, 5, 10 }, settings.SilenceSeconds);
            if (silence.SelectedIndex < 0) silence.SelectedIndex = 0;
            position.Items.AddRange(new object[] { "畫面下方", "畫面上方" });
            position.SelectedIndex = settings.HudAtTop ? 1 : 0;
            startup.Checked = settings.LaunchAtLogin;
            soundEffects.Checked = settings.SoundEffectsEnabled;

            primary.Items.AddRange(LanguageNames);
            primary.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, settings.PrimaryLanguage));
            additional.Items.AddRange(LanguageNames);
            foreach (var code in settings.AdditionalLanguages ?? new List<string>())
            {
                var index = Array.IndexOf(LanguageCodes, code);
                if (index >= 0 && index != primary.SelectedIndex && additional.CheckedItems.Count < 2) additional.SetItemChecked(index, true);
            }
            additional.ItemCheck += (sender, args) =>
            {
                if (args.NewValue != CheckState.Checked) return;
                if (args.Index == primary.SelectedIndex) { args.NewValue = CheckState.Unchecked; validation.Text = "主要語言已包含，不需要重複勾選。"; }
                else if (additional.CheckedItems.Count >= 2) { args.NewValue = CheckState.Unchecked; validation.Text = "最多選擇兩種混用語言。"; }
                else validation.Text = "";
            };
            primary.SelectedIndexChanged += (sender, args) => { if (primary.SelectedIndex >= 0) additional.SetItemChecked(primary.SelectedIndex, false); };
            mode.Items.AddRange(new object[] { "純逐字 · 保留原本說法", "標準 · 整理語助詞與重複", "簡潔 · 精簡口語贅詞" });
            mode.SelectedIndex = (int)settings.Mode;
            numbers.Checked = settings.ConvertChineseNumerals;
            retry.Checked = settings.AutomaticLanguageRetry;
            controls(languagePage, Theme.Label("主要語言"), primary, Theme.Label("允許混用（最多兩種）"), additional,
                Theme.Label("文字模式"), mode, numbers, retry, validation);

            firstShortcut.Value = settings.PrimaryShortcut ?? new Shortcut();
            secondShortcut.Value = settings.AlternateShortcut ?? new Shortcut { Modifiers = 3, Key = (int)Keys.D };
            alternateEnabled.Checked = settings.AlternateShortcut != null;
            alternateMode.Items.AddRange(new object[] { "純逐字", "標準", "簡潔" });
            alternateMode.SelectedIndex = (int)settings.AlternateMode;
            secondShortcut.Enabled = alternateMode.Enabled = alternateEnabled.Checked;
            alternateEnabled.CheckedChanged += (sender, args) => { secondShortcut.Enabled = alternateMode.Enabled = alternateEnabled.Checked; };
            controls(shortcutsPage, Theme.Label("主要快捷鍵"), firstShortcut,
                Theme.Label("按一次開始，再按一次完成。先勾選輔助鍵，再點選欄位按下主鍵。", true),
                alternateEnabled, secondShortcut, Theme.Label("第二組快捷鍵使用的文字模式"), alternateMode,
                Theme.Label("快捷鍵若已被其他程式使用，儲存時會提示並保留原設定。", true));

            var save = Theme.Button("儲存", Save, true);
            var cancel = Theme.Button("取消", (sender, args) => { DialogResult = DialogResult.Cancel; Close(); });
            AcceptButton = save; CancelButton = cancel;
            Controls.Add(tabs);
            Controls.Add(Theme.ButtonRow(save, cancel));
        }

        private void Save(object sender, EventArgs args)
        {
            if (!firstShortcut.IsValid || (alternateEnabled.Checked && !secondShortcut.IsValid))
            { Theme.Error(this, "請選擇 Ctrl、Alt 或 Win，並搭配一般按鍵；F12 保留給系統。"); return; }
            var first = firstShortcut.Value;
            var second = alternateEnabled.Checked ? secondShortcut.Value : null;
            if (second != null && first.Modifiers == second.Modifiers && first.Key == second.Key)
            { Theme.Error(this, "兩組快捷鍵不能相同。"); return; }
            var result = Settings.Clone();
            result.PrimaryLanguage = LanguageCodes[primary.SelectedIndex];
            result.AdditionalLanguages = additional.CheckedIndices.Cast<int>().Select(index => LanguageCodes[index]).Where(code => code != result.PrimaryLanguage).Take(2).ToList();
            result.Mode = (OutputMode)mode.SelectedIndex;
            result.AlternateMode = (OutputMode)alternateMode.SelectedIndex;
            result.ConvertChineseNumerals = numbers.Checked;
            result.AutomaticLanguageRetry = retry.Checked;
            result.SilenceSeconds = new[] { 0, 3, 5, 10 }[silence.SelectedIndex];
            result.InputDeviceId = ((AudioDevice)devices.SelectedItem).Id;
            result.HudAtTop = position.SelectedIndex == 1;
            result.LaunchAtLogin = startup.Checked;
            result.SoundEffectsEnabled = soundEffects.Checked;
            result.PrimaryShortcut = first; result.AlternateShortcut = second;
            try { SettingsStore.Validate(result); }
            catch (ArgumentException exception) { Theme.Error(this, exception.Message); return; }
            Settings = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470, Margin = new Padding(0, 0, 0, 12) }; }
        private static FlowLayoutPanel Page(TabControl tabs, string name)
        {
            var page = new TabPage(name) { BackColor = Theme.Background };
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22, 12, 22, 12) };
            page.Controls.Add(content); tabs.TabPages.Add(page); return content;
        }
        private static void controls(FlowLayoutPanel parent, params Control[] children) { parent.Controls.AddRange(children); }
    }

    public sealed class ShortcutEditor : FlowLayoutPanel
    {
        private readonly CheckBox control = new CheckBox { Text = "Ctrl", AutoSize = true };
        private readonly CheckBox alt = new CheckBox { Text = "Alt", AutoSize = true };
        private readonly CheckBox shift = new CheckBox { Text = "Shift", AutoSize = true };
        private readonly CheckBox windows = new CheckBox { Text = "Win", AutoSize = true };
        private readonly TextBox key = new TextBox { ReadOnly = true, Width = 120, TextAlign = HorizontalAlignment.Center, ShortcutsEnabled = false, AccessibleName = "點選後按下快捷鍵的主鍵" };
        private int keyCode = (int)Keys.Space;
        public ShortcutEditor()
        {
            AutoSize = true; Width = 470; WrapContents = false; Margin = new Padding(0, 4, 0, 14);
            Controls.AddRange(new Control[] { control, alt, shift, windows, key });
            key.PreviewKeyDown += (sender, args) => { if (args.KeyCode != Keys.Tab) args.IsInputKey = true; };
            key.KeyDown += (sender, args) =>
            {
                args.SuppressKeyPress = true; args.Handled = true;
                if (IsModifier(args.KeyCode) || args.KeyCode == Keys.Tab) return;
                keyCode = (int)args.KeyCode;
                key.Text = KeyName(args.KeyCode);
            };
            Value = new Shortcut();
        }
        public Shortcut Value
        {
            get { return new Shortcut { Modifiers = (alt.Checked ? 1 : 0) | (control.Checked ? 2 : 0) | (shift.Checked ? 4 : 0) | (windows.Checked ? 8 : 0), Key = keyCode }; }
            set
            {
                var shortcut = value ?? new Shortcut();
                alt.Checked = (shortcut.Modifiers & 1) != 0; control.Checked = (shortcut.Modifiers & 2) != 0;
                shift.Checked = (shortcut.Modifiers & 4) != 0; windows.Checked = (shortcut.Modifiers & 8) != 0;
                keyCode = shortcut.Key; key.Text = KeyName((Keys)keyCode);
            }
        }
        public bool IsValid { get { return (Value.Modifiers & 11) != 0 && keyCode > 0 && keyCode < 255 && keyCode != (int)Keys.F12 && !IsModifier((Keys)keyCode); } }
        private static bool IsModifier(Keys value) { return value == Keys.ControlKey || value == Keys.ShiftKey || value == Keys.Menu || value == Keys.LWin || value == Keys.RWin; }
        private static string KeyName(Keys value) { return value == Keys.Space ? "空白鍵" : value.ToString(); }
    }
}
