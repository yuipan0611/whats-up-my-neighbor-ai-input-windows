using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;
using Shortcut = Neighbor.Core.Shortcut;
using Neighbor.Core;
using Neighbor.Windows.Platform;
using Neighbor.Windows.UI;

namespace Neighbor.Windows
{
    public sealed class NeighborApplicationContext : ApplicationContext
    {
        public const string ProductName = "What's up my neighbor";
        private readonly bool smoke;
        private readonly Control dispatcher = new Control();
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Timer timer = new Timer { Interval = 100 };
        private readonly Timer hideHud = new Timer { Interval = 3000 };
        private readonly SettingsStore settingsStore;
        private AppSettings settings;
        private readonly VocabularyStore vocabulary;
        private readonly TemporaryAudioStore audioStore;
        private readonly WindowsCredentialStore credentials;
        private readonly WasapiRecorder recorder;
        private readonly WindowsTextDelivery delivery;
        private readonly HotKeyService hotkeys;
        private readonly GroqTranscriber transcriber;
        private readonly DictationCoordinator coordinator;
        private readonly LearningEngine learning;
        private readonly RecordingHud hud;
        private readonly LearningToast toast;
        private readonly DesktopPetController desktopPet;
        private VocabularyForm vocabularyForm;
        private VocabularyPrompt latestPrompt;
        private readonly ToolStripMenuItem statusItem = new ToolStripMenuItem("準備好了") { Enabled = false };
        private readonly ToolStripMenuItem recordItem = new ToolStripMenuItem("開始錄音");
        private readonly ToolStripMenuItem retryItem = new ToolStripMenuItem("重新辨識保留錄音");
        private readonly ToolStripMenuItem cancelItem = new ToolStripMenuItem("取消／捨棄本次錄音");
        private string learningStatus = "貼入後在同一欄位修正一個詞，可自動學習。";
        private string shownCandidateId;
        private string shownPendingId;
        private bool exiting, learningBusy;
        private int smokeTicks;
        /// <summary>Smoke mode only: true when an optional component failed, so the process exits non-zero.</summary>
        // Smoke mode only: true when the optional companion failed, so the process exits non-zero.
        public bool Failed => smoke && desktopPet.Failed;

        public NeighborApplicationContext(bool smoke = false)
        {
            this.smoke = smoke;
            var unused = dispatcher.Handle;
            string directory = smoke ? Path.Combine(Path.GetTempPath(), "Neighbor-smoke-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsUpMyNeighbor");
            Directory.CreateDirectory(directory);
            settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings = settingsStore.Load();
            vocabulary = new VocabularyStore(Path.Combine(directory, "vocabulary.json"));
            audioStore = new TemporaryAudioStore(Path.Combine(directory, "Recordings"));
            audioStore.Cleanup();
            credentials = new WindowsCredentialStore();
            recorder = new WasapiRecorder();
            delivery = new WindowsTextDelivery();
            hotkeys = new HotKeyService();
            transcriber = new GroqTranscriber(credentials);
            coordinator = new DictationCoordinator(recorder, transcriber, delivery, new WindowsTextNormalizer(), audioStore, vocabulary);
            learning = new LearningEngine(new WindowsWordSegmenter());
            hud = new RecordingHud(); hud.ApplySettings(settings);
            toast = new LearningToast();
            desktopPet = new DesktopPetController(() => new DesktopPetForm(),
                (stage, error) => DiagnosticLog.Record(DiagnosticLog.DefaultPath, stage, error),
                message => Post(() => Notify(message)));
            desktopPet.Create();
            tray = new NotifyIcon { Icon = Theme.LoadIcon(), Text = ProductName, ContextMenuStrip = menu, Visible = true };
            BuildMenu();
            hotkeys.Pressed += id => Run(() => ToggleAsync(id == 2));
            hud.StartRequested += () => Run(() => ToggleAsync(false));
            hud.FinishRequested += () => Run(coordinator.StopAsync);
            hud.CancelRequested += () => Run(CancelAsync);
            hud.RetryRequested += () => Run(coordinator.RetryAsync);
            toast.Rejected += RejectLearning;
            coordinator.StatusChanged += (_, e) => UpdateStatus(e);
            coordinator.LevelChanged += (_, e) => hud.UpdateLevel(e.Decibels, e.IsKnown);
            coordinator.PromptChanged += value => { latestPrompt = value; vocabularyForm?.UpdatePrompt(value); };
            coordinator.Delivered += result => {
                SystemSounds.Asterisk.Play();
                learningStatus = string.IsNullOrWhiteSpace(result.Message) ? "未啟用自動學習；可手動新增詞條。" : result.Message;
                RefreshLearningStatus();
                if (!result.PasteSent || !result.PasteVerified) Notify(result.Message);
            };
            delivery.CorrectionObserved += (_, e) => Post(() => { try { ObserveCorrection(e); } catch { CancelLearning("未加入"); Notify("詞典紀錄未能更新，可在詞典中手動處理。"); } });
            hideHud.Tick += (_, e) => { hideHud.Stop(); if (coordinator.State == DictationState.Idle) hud.Hide(); };
            timer.Tick += (_, e) => Run(TickAsync);
            timer.Start();
            if (!smoke)
            {
                if (!hotkeys.TryApply(settings.PrimaryShortcut, settings.AlternateShortcut, out string error)) Post(() => Notify(error + " 可從系統匣開啟設定或開始錄音。"));
                Post(FirstRun);
                desktopPet.SetEnabled(settings.DesktopPetEnabled);
            }
        }
        private void BuildMenu()
        {
            menu.Items.Add(new ToolStripMenuItem(ProductName) { Enabled = false });
            menu.Items.Add(statusItem); menu.Items.Add(new ToolStripSeparator());
            recordItem.Click += (_, e) => Run(() => ToggleAsync(false));
            retryItem.Click += (_, e) => Run(coordinator.RetryAsync);
            cancelItem.Click += (_, e) => Run(CancelAsync);
            retryItem.Enabled = cancelItem.Enabled = false;
            menu.Items.Add(recordItem); menu.Items.Add(retryItem); menu.Items.Add(cancelItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("文字、語言與錄音設定…", null, (_, e) => OpenSettings());
            var petItem = new ToolStripMenuItem("桌面小黑貓散步") { Checked = settings.DesktopPetEnabled };
            petItem.Click += (_, e) => {
                var updated = settings.Clone(); updated.DesktopPetEnabled = !settings.DesktopPetEnabled;
                try { settingsStore.Save(updated); settings = updated; petItem.Checked = settings.DesktopPetEnabled; desktopPet.SetEnabled(settings.DesktopPetEnabled); }
                catch { Notify("無法儲存桌面小黑貓設定。"); }
            };
            menu.Items.Add(petItem);
            menu.Items.Add("詞典與學習狀態…", null, (_, e) => OpenVocabulary());
            menu.Items.Add("設定／更換 Groq 金鑰…", null, (_, e) => OpenKey());
            menu.Items.Add("Windows 麥克風隱私設定", null, (_, e) => { try { System.Diagnostics.Process.Start("ms-settings:privacy-microphone"); } catch { Notify("請開啟 Windows 設定 → 隱私權與安全性 → 麥克風。"); } });
            menu.Items.Add("使用說明", null, (_, e) => OpenHelp());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("結束", null, (_, e) => Run(ExitAsync));
            tray.DoubleClick += (_, e) => OpenSettings();
        }
        private void FirstRun()
        {
            if (settingsStore.LoadError != null) Notify(settingsStore.LoadError);
            else if (!settings.IntroductionShown)
            {
                settings.IntroductionShown = true;
                try { settingsStore.Save(settings); } catch { }
                MessageBox.Show("歡迎使用 What's up my neighbor！\n\n1. 在 App 中安全儲存自己的 Groq API Key。\n2. 點一下要輸入的文字欄位。\n3. 按 Ctrl + Alt + Space 開始錄音，再按一次完成。\n\n小黑貓左眼取消、右眼完成。App 只貼上，不會自動送出訊息。\n\n錄音會送至 Groq 辨識，需要網路。", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenKey();
            }
            if (vocabulary.LoadError != null) Notify(vocabulary.LoadError);
        }
        private async Task ToggleAsync(bool alternate)
        {
            if (coordinator.State == DictationState.Idle || coordinator.State == DictationState.RetainedAudio)
            {
                if (string.IsNullOrWhiteSpace(credentials.Read())) { OpenKey(); return; }
                CancelLearning("已取消（開始新的錄音）");
                learning.Reset();
            }
            await coordinator.ToggleAsync(settings, alternate);
        }
        private async Task CancelAsync() { CancelLearning("已取消"); await coordinator.CancelAsync(); }
        private void UpdateStatus(StatusEventArgs e)
        {
            desktopPet.SetBusy(e.State != DictationState.Idle);
            statusItem.Text = e.Message;
            tray.Text = ProductName + (e.State == DictationState.Recording ? " · 錄音中" : "");
            recordItem.Text = e.State == DictationState.Recording ? "完成錄音並辨識" : "開始錄音";
            recordItem.Enabled = e.State == DictationState.Idle || e.State == DictationState.Recording || e.State == DictationState.RetainedAudio;
            retryItem.Enabled = e.State == DictationState.RetainedAudio;
            cancelItem.Enabled = e.State != DictationState.Idle;
            hideHud.Stop(); hud.UpdateState(e.State, e.Message); hud.Show();
            if (e.State == DictationState.Recording) SystemSounds.Asterisk.Play();
            if (e.State == DictationState.Transcribing) SystemSounds.Beep.Play();
            if (e.State == DictationState.Idle) hideHud.Start();
            if (e.State == DictationState.RetainedAudio) Notify(e.Message);
        }
        private async Task TickAsync()
        {
            if (exiting) return;
            if (smoke)
            {
                if (++smokeTicks == 1) { hud.UpdateState(DictationState.Recording, "測試錄音介面"); hud.Show(); hud.UpdateLevel(-20, true); desktopPet.SetEnabled(true); desktopPet.SetBusy(false); }
                if (smokeTicks == 8) desktopPet.SetEnabled(false);
                if (smokeTicks >= 10) await ExitAsync();
                return;
            }
            await coordinator.TickAsync();
            if (learningBusy) return;
            learningBusy = true;
            try
            {
                if (shownCandidateId != null && !ShownCandidateIsWaiting())
                { CancelLearning("這筆候選已由你處理。"); delivery.StopObserving(); return; }
                var candidate = learning.Poll(DateTime.UtcNow);
                if (candidate != null)
                {
                    var persisted = vocabulary.AddCandidate(candidate);
                    if (persisted == null) { learning.Cancel(); return; }
                    shownCandidateId = persisted.Id; shownPendingId = candidate.Id;
                    toast.ShowCandidate(candidate, 2);
                    learningStatus = "修正詞等待加入；可按「不加入」取消。"; RefreshLearningStatus();
                }
                if (shownCandidateId != null && learning.Pending != null && learning.CountdownUntilUtc.HasValue)
                    toast.ShowCandidate(learning.Pending, Math.Max(1, (int)Math.Ceiling((learning.CountdownUntilUtc.Value - DateTime.UtcNow).TotalSeconds)));
                if (!learning.IsReadyToCommit(DateTime.UtcNow)) return;
                string pendingId = learning.Pending?.Id;
                bool valid = await delivery.RevalidateCorrectionAsync();
                if (!valid || pendingId != learning.Pending?.Id || !ShownCandidateIsWaiting()) { CancelLearning("已取消（輸入範圍已變更）"); return; }
                var completed = learning.Complete();
                if (completed == null) return;
                if (shownCandidateId != null) vocabulary.SetCandidateStatus(shownCandidateId, VocabularyStore.Added);
                else throw new InvalidOperationException("找不到待加入的詞條，請在詞典中手動確認。");
                toast.Hide(); shownCandidateId = shownPendingId = null;
                learningStatus = "已加入詞典；下次辨識可查看是否納入提示。"; RefreshLearningStatus();
                delivery.StopObserving();
            }
            catch (Exception e) { CancelLearning("未加入"); Notify(e is InvalidOperationException || e is ArgumentException ? e.Message : "詞典未能儲存，請查看詞典與學習狀態。"); }
            finally { learningBusy = false; }
        }
        private void ObserveCorrection(CorrectionEventArgs e)
        {
            if (exiting || coordinator.State != DictationState.Idle) return;
            if (!e.IsValid) { CancelLearning("已取消（觀察失效）"); learningStatus = e.Reason ?? "無法確認修正範圍，可手動新增。"; RefreshLearningStatus(); return; }
            var candidate = learning.Observe(e.Original, e.Corrected, DateTime.UtcNow);
            if (shownPendingId != null && candidate?.Id != shownPendingId)
            {
                if (ShownCandidateIsWaiting()) vocabulary.SetCandidateStatus(shownCandidateId, VocabularyStore.Cancelled);
                toast.Hide(); shownCandidateId = shownPendingId = null;
            }
        }
        private void RejectLearning()
        {
            try { if (ShownCandidateIsWaiting()) vocabulary.SetCandidateStatus(shownCandidateId, VocabularyStore.Rejected); }
            catch { Notify("拒絕紀錄未能儲存，這次仍不會加入詞典。"); }
            shownCandidateId = shownPendingId = null; learning.Cancel(); toast.Hide();
            learningStatus = "本次修正未加入，可在詞典中手動處理。"; RefreshLearningStatus();
        }
        private bool ShownCandidateIsWaiting()
        { return shownCandidateId != null && vocabulary.Candidates.Any(c => c.Id == shownCandidateId && c.Status == VocabularyStore.Waiting); }
        private void CancelLearning(string status)
        {
            if (ShownCandidateIsWaiting()) { try { vocabulary.SetCandidateStatus(shownCandidateId, VocabularyStore.Cancelled); } catch { } }
            shownCandidateId = shownPendingId = null; learning.Cancel(); toast.Hide();
            learningStatus = status; RefreshLearningStatus();
        }
        private void RefreshLearningStatus() { if (vocabularyForm != null && !vocabularyForm.IsDisposed) vocabularyForm.LearningStatus = learningStatus; }
        private void OpenSettings()
        {
            try
            {
                System.Collections.Generic.IReadOnlyList<AudioDevice> availableDevices;
                try { availableDevices = recorder.GetDevices(); }
                catch
                {
                    availableDevices = new AudioDevice[0];
                    Notify("目前無法讀取麥克風清單，仍可調整其他設定。請確認 Windows 音訊服務與麥克風連線後重新開啟設定。");
                }
                using (var form = new SettingsForm(settings, availableDevices))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    var updated = form.Settings; SettingsStore.Validate(updated);
                    // The settings dialog has no companion control; keep the current value even if it was toggled while the dialog was open.
                    updated.DesktopPetEnabled = settings.DesktopPetEnabled;
                    if (!hotkeys.TryApply(updated.PrimaryShortcut, updated.AlternateShortcut, out string error)) { Notify(error); return; }
                    try { LoginStartup.Apply(updated.LaunchAtLogin); settingsStore.Save(updated); }
                    catch { hotkeys.TryApply(settings.PrimaryShortcut, settings.AlternateShortcut, out _); LoginStartup.Apply(settings.LaunchAtLogin); throw; }
                    settings = updated; hud.ApplySettings(settings);
                }
            }
            catch (Exception e) { Notify(e is InvalidOperationException || e is ArgumentException ? e.Message : "設定未能儲存，請稍後再試。"); }
        }
        private void OpenKey() { using (var form = new ApiKeyForm(credentials)) form.ShowDialog(); }
        private void OpenVocabulary()
        {
            if (vocabularyForm == null || vocabularyForm.IsDisposed)
            {
                vocabularyForm = new VocabularyForm(vocabulary);
                vocabularyForm.LearningStatus = learningStatus;
                if (latestPrompt != null) vocabularyForm.UpdatePrompt(latestPrompt);
            }
            vocabularyForm.Show(); vocabularyForm.Activate();
        }
        private void OpenHelp()
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用說明.txt");
            try { System.Diagnostics.Process.Start(file); } catch { Notify("快捷鍵 Ctrl + Alt + Space：開始／完成錄音。從系統匣可調整設定與金鑰。"); }
        }
        private void Notify(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || exiting) return;
            tray.ShowBalloonTip(5000, ProductName, message.Length > 250 ? message.Substring(0, 250) : message, ToolTipIcon.Info);
        }
        private void Post(Action action) { if (!exiting && !dispatcher.IsDisposed) dispatcher.BeginInvoke(action); }
        private async void Run(Func<Task> operation)
        {
            try { await operation(); } catch { Notify("操作未完成，請稍後再試。已有文字可從剪貼簿手動貼上。"); }
        }
        private async Task ExitAsync()
        {
            if (exiting) return; exiting = true; timer.Stop(); hideHud.Stop();
            CancelLearning("已取消（App 結束）");
            await coordinator.CancelAsync();
            audioStore.Cleanup();
            hud.Hide(); toast.Hide(); tray.Visible = false;
            desktopPet.Hide();
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); hideHud.Dispose(); hotkeys.Dispose(); recorder.Dispose(); delivery.Dispose(); transcriber.Dispose();
                hud.Dispose(); toast.Dispose(); vocabularyForm?.Dispose();
                desktopPet.Dispose();
                var icon = tray.Icon; tray.Dispose(); icon?.Dispose(); menu.Dispose(); dispatcher.Dispose();
                if (smoke) { try { Directory.Delete(Path.GetDirectoryName(audioStore.DirectoryPath), true); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
