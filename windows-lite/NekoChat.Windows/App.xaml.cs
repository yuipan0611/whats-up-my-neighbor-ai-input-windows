using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App : Application
{
    private Settings settings = new();
    private PetWindow pet = null!;
    private ChatWindow chat = null!;
    private DirectLanListener? directListener;
    private int directSendPort = DirectLanMessaging.DefaultPort;
    private Mutex? instanceGate;
    private bool checkingUpdate;
    private ChatRuntime? chatRuntime;
    private SettingsLoadResult? startupSource;
    private bool isolatedData, startingData, closingApp;
    private int startupPort = DirectLanMessaging.DefaultPort;
    private ChatWireConfiguration? startupWireConfiguration;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--restore-backup")
        {
            Environment.Exit(OfflineRestoreCommand.Run(e.Args));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--manual-send-cancel-smoke-test")
        {
            Environment.Exit(await RunManualSendCancelSmokeAsync(e.Args[1]));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--pairing-ui-smoke-test")
        {
            Environment.Exit(await RunPairingUiSmokeAsync(e.Args[1]));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--identity-vault-smoke-test")
        {
            Environment.Exit(Identity.IdentityVaultSmoke.Run(e.Args[1]));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--update-download-smoke-test")
        {
            Environment.Exit(UpdateDownloadSmoke.Run(e.Args[1]));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            Environment.Exit(SmokeTest.Run(e.Args[1]));
            return;
        }

        try
        {
            var messagingSmoke = MessagingSmokeOptions.Parse(e.Args);
            bool keyboardSmoke = e.Args.Length == 2 && e.Args[0] == "--keyboard-smoke-test";
            bool gestureSmoke = e.Args.Length == 2 && e.Args[0] == "--interaction-smoke-test";
            bool receiverSmoke = e.Args.Length == 2 && e.Args[0] == "--receiver-failure-smoke-test";
            bool instanceSmoke = e.Args.Length == 2 && e.Args[0] == "--instance-smoke-test";
            bool instanceProbe = e.Args.Length == 2 && e.Args[0] == "--instance-probe";
            if (messagingSmoke is null && !gestureSmoke && !receiverSmoke && !keyboardSmoke)
            {
                instanceGate = new Mutex(true, "Local\\NekoChat.WindowsLite.App", out bool firstInstance);
                if (instanceProbe)
                {
                    Directory.CreateDirectory(e.Args[1]);
                    File.WriteAllText(Path.Combine(e.Args[1], "duplicate-result.txt"), firstInstance ? "FAIL: second app acquired the gate" : "PASS: duplicate app stopped before settings, listener or windows");
                    Environment.Exit(firstInstance ? 1 : 0);
                    return;
                }
                if (!firstInstance)
                {
                    MessageBox.Show("NekoChat 已經在執行。請使用桌面上現有的貓咪；雙擊貓咪可開啟聊天。若要更新，請先從貓咪右鍵選「離開」。", "NekoChat 已開啟");
                    Shutdown();
                    return;
                }
            }
            var settingsSource = e.Args.Length == 0 ? Settings.LoadWithStatus() : null;
            string? smokeDirectory = messagingSmoke?.Directory ?? (e.Args.Length == 2 && (keyboardSmoke || gestureSmoke || receiverSmoke || instanceSmoke ||
                e.Args[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test") ? e.Args[1] : null);
            if (e.Args.Length > 0 && smokeDirectory is null) throw new ArgumentException("不支援這組啟動參數。");
            settings = settingsSource?.Value ?? (smokeDirectory is not null ? Settings.ForSmoke(smokeDirectory) : new Settings { AutoStartOnLogin = false, ReadOnlyFallback = true });
            if (messagingSmoke is not null) settings = settings with { TeacherIp = "127.0.0.1" };
            directSendPort = messagingSmoke?.PeerPort ?? DirectLanMessaging.DefaultPort;

            if (settings.AutoStartOnLogin)
            {
                LoginStartup.Apply(true);
            }

            pet = new PetWindow();
            chat = new ChatWindow(settings);
            Settings.SaveProblem += SettingsSaveProblem;
            pet.ReactionDisplayDevice = settings.ReactionDisplayDevice;
            pet.ReactionFailed += message => chat.SetStatus(message);
            chat.SettingsChanged += updated =>
            {
                settings = updated;
                pet.ReactionDisplayDevice = settings.ReactionDisplayDevice;
            };
            pet.SetWanderEnabled(settings.WanderEnabled);
            pet.WanderToggled += enabled =>
            {
                chat.UpdateWanderEnabled(enabled);
            };

            pet.OpenChat += targetIp => chat.ShowChat(targetIp);
            pet.SelectCoat += id =>
            {
                try
                {
                    pet.SetPreset(id);
                    chat.RememberCoat(id);
                }
                catch (Exception ex)
                {
                    chat.SetStatus(SafeError(ex));
                }
            };
            pet.AutoStartToggled += enabled =>
            {
                chat.AutoStartCheck.IsChecked = enabled;
            };
            pet.ImportCoat += () =>
            {
                var picker = new OpenFileDialog { Filter = "貓咪設定 JSON|*.json", Title = "選擇 cat.config.json" };
                if (picker.ShowDialog(chat) != true) return;
                try
                {
                    pet.SetArt(CatArtwork.FromConfig(picker.FileName));
                    chat.SetStatus("已載入自訂外觀；此次啟動有效。內建花色會自動記住。");
                }
                catch (Exception ex)
                {
                    chat.SetStatus(SafeError(ex));
                }
            };
            pet.CheckUpdates += () => _ = CheckForUpdatesAsync(true);

            chat.AutoStartChanged += enabled =>
            {
                pet.UpdateAutoStartChecked(enabled);
                LoginStartup.Apply(enabled);
            };
            chat.SendRequested += async (targetIp, text) => await SendDirectMessageAsync(targetIp, text);
            chat.ReactionRequested += async (targetIp, reaction) => await SendDirectMessageAsync(targetIp, reaction.Text, reaction, preserveDraft: true);
            pet.ExitRequested += async () => await RequestShutdownAsync();
            startupSource = settingsSource ?? new SettingsLoadResult(settings, smokeDirectory is not null ? SettingsSourceStatus.Loaded : SettingsSourceStatus.Unavailable);
            isolatedData = smokeDirectory is not null; startupPort = messagingSmoke?.ListenPort ?? DirectLanMessaging.DefaultPort;
            chat.DataRetryRequested += () => _ = StartDataAsync();
            // Historical recovery source is retained; ordinary classroom startup has no recovery command.
            if (messagingSmoke is not null) chat.IdentityRecoveryRequested += () => _ = RecoverIdentityAsync();

            if (Appearance.Presets.Contains(settings.Coat))
            {
                pet.SetPreset(settings.Coat);
            }

            pet.Show();
            chat.Show();
            if (messagingSmoke is not null)
                startupWireConfiguration = await PrepareMessagingFixtureAsync(messagingSmoke);
            await StartDataAsync();
            SystemEvents.PowerModeChanged += AppPowerModeChanged;

            if (keyboardSmoke)
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunKeyboardSmokeTestAsync(e.Args[1])));

            if (instanceSmoke)
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunInstanceSmokeTestAsync(e.Args[1])));
            if (receiverSmoke)
                _ = Dispatcher.BeginInvoke(new Action(() => RunReceiverFailureSmokeTest(e.Args[1])));

            if (messagingSmoke is not null)
            {
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunMessagingSmokeTestAsync(messagingSmoke)));
            }
            if (gestureSmoke)
            {
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunInteractionSmokeTestAsync(e.Args[1])));
            }

            if (e.Args.Length == 0)
            {
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => _ = CheckForUpdatesAsync(false)));
            }

            if (e.Args.Length == 2 && e.Args[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test")
            {
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Directory.CreateDirectory(e.Args[1]);
                    bool visible = pet.IsVisible && chat.IsVisible;
                    bool listenerRunning = directListener?.Running == true;
                    File.WriteAllText(Path.Combine(e.Args[1], "startup-result.txt"),
                        visible && listenerRunning ? "PASS: Lite edition direct LAN startup and UI rendered." : "FAIL: initial windows or listener state were incorrect.");
                    Environment.Exit(visible && listenerRunning ? 0 : 1);
                }));
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Length == 5 && e.Args[0] == "--messaging-smoke-test")
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], e.Args[2] + "-result.txt"), "FAIL: fixture startup: " + ex.GetType().Name);
            }
            else MessageBox.Show("NekoChat 無法啟動：" + ex.Message, "NekoChat");
            Shutdown(1);
        }
    }

    private async Task SendDirectMessageAsync(string targetIp, string text, ClassroomReaction? reaction = null, bool preserveDraft = false)
    {
        if (!_activeLegacySend.IsCompleted) return;
        _activeLegacySend = SendLegacyReactionCoreAsync(targetIp, text, reaction, preserveDraft);
        await _activeLegacySend;
    }
    private Task _activeLegacySend = Task.CompletedTask;
    private async Task SendLegacyReactionCoreAsync(string targetIp, string text, ClassroomReaction? reaction, bool preserveDraft)
    {
        if (reaction is null)
        { chat.SetStatus("教室傳送服務尚未就緒；未傳送，草稿保留。"); return; }
        if (directListener?.Running != true)
        {
            chat.SetStatus("接收服務未啟動，請先處理下方提示後重開 NekoChat。（草稿保留）");
            return;
        }
        if (!chat.CanSendReaction) return;
        var ownIps = LanInvitation.LocalAddresses();
        if (ownIps.Contains(targetIp))
        {
            chat.SetStatus("這是自己的 IP，請填對方的內網 IP。");
            return;
        }

        reaction ??= ClassroomReactions.MatchText(text);
        if (reaction?.TeacherOnly == true && targetIp != settings.TeacherIp) reaction = null;
        chat.SetSending(true);
        var displayName = settings.ResolveDisplayName(targetIp);
        chat.SetStatus($"正在傳送至 {displayName}…");
        try
        {
            await DirectLanMessaging.SendDirectAsync(targetIp, text, directSendPort, reaction: reaction?.Id);
            chat.ShowLegacyDeliveredStatus();
        }
        catch (Exception ex)
        {
            chat.SetStatus(SafeError(ex) + "（草稿保留）");
        }
        finally
        {
            chat.SetSending(false);
        }
    }

    internal static string SafeError(Exception ex) => ex switch
    {
        ArgumentException => ex.Message,
        InvalidOperationException => ex.Message,
        System.Net.Sockets.SocketException => "無法連線至對方電腦；請確認對方已開啟 NekoChat 且在同一個內網。",
        System.Net.Http.HttpRequestException => "對方電腦拒絕連線或回應異常。",
        _ => "操作失敗，請檢查網路連線或 IP 位址。"
    };

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdate) { if (manual) chat.SetStatus("正在檢查或下載更新，請稍候。"); return; }
        checkingUpdate = true;
        try
        {
            string currentVer = "0.1.0-beta.9";
            try
            {
                var infoVer = (System.Reflection.AssemblyInformationalVersionAttribute?)
                    Attribute.GetCustomAttribute(typeof(App).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                if (infoVer is not null && !string.IsNullOrWhiteSpace(infoVer.InformationalVersion))
                    currentVer = infoVer.InformationalVersion.Split('+')[0].Trim();
            }
            catch { }

            using var checker = new UpdateChecker();
            var result = await checker.CheckForUpdatesAsync(currentVer, "nekochat-lite-v");
            if (result.HasUpdate)
            {
                if (manual)
                {
                    if (string.IsNullOrWhiteSpace(result.DownloadUrl) || string.IsNullOrWhiteSpace(result.ChecksumUrl))
                    { MessageBox.Show(chat, "新版尚未備妥可驗證的安裝檔，請稍後重試。", "NekoChat — 更新", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                    string prompt = $"發現新版本 NekoChat Lite v{result.LatestVersion}！（目前版本：v{result.CurrentVersion}）\n\n{result.ReleaseTitle ?? ""}\n是否立即下載更新？";
                    if (MessageBox.Show(chat, prompt, "NekoChat — 檢查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
                    var download = new UpdateDownloadWindow(chat, result);
                    download.ShowDialog();
                    if (download.Error is not null) throw download.Error;
                    if (download.DownloadedPath is null) { chat.SetStatus("更新下載已取消。"); return; }
                    if (MessageBox.Show(chat, "下載與檔案驗證完成。是否開啟安裝程式並關閉 NekoChat？", "NekoChat — 更新已下載", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(download.DownloadedPath) { UseShellExecute = true })
                            ?? throw new IOException("無法啟動安裝程式，已保留下載檔供重試。");
                        await RequestShutdownAsync();
                    }
                }
                else
                {
                    pet.Notify($"發現新版本 v{result.LatestVersion}！右鍵選單「檢查更新」可下載更新。");
                }
            }
            else if (manual)
            {
                if (result.Success)
                {
                    MessageBox.Show(chat, $"目前已是最新版本（v{result.CurrentVersion}）。", "NekoChat — 檢查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(chat, "無法檢查更新：" + result.ErrorMessage + "\n\n請確認網路連線或稍後再試。", "NekoChat — 檢查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show(chat, "更新未完成：" + ex.Message, "NekoChat — 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { checkingUpdate = false; }
    }

    private void SettingsSaveProblem(string reason)
    {
        if (closingApp || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) chat.RefreshSettingsSaveStatus(); }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.SaveProblem -= SettingsSaveProblem;
        _identityRecoveryCancellation?.Cancel();
        SystemEvents.PowerModeChanged -= AppPowerModeChanged;
        if (chatRuntime is not null)
        {
            if (!closingApp)
            { closingApp = true; chat.BeginClosing(); chatRuntime.StopReceivingAsync().GetAwaiter().GetResult(); chatRuntime.DrainNetworkAsync().GetAwaiter().GetResult(); try { chat.FlushForExitAsync().GetAwaiter().GetResult(); } catch { } }
            chatRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        instanceGate?.Dispose();
        base.OnExit(e);
    }
    private void AppPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) _ = Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) { chatRuntime?.ResetAfterResume(); chat.ResetRecipientProtocolHints(); chat.DataChanged(); } })); }

    private async Task StartDataAsync()
    {
        if (startingData || closingApp || startupSource is null || chatRuntime is not null) return;
        startingData = true; ChatRuntime? candidate = null;
        try
        {
            candidate = await ChatRuntime.OpenAsync(startupSource, isolatedData, startupPort, startupWireConfiguration);
            if (closingApp) return;
            await chat.AttachRuntimeAsync(candidate, directSendPort);
            candidate.DataChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) chat.DataChanged(); }));
            var listener = candidate.Listener; directListener = listener;
            listener.LegacyMessageStored += notice => Dispatcher.BeginInvoke(new Action(() => {
                if (closingApp || !ReferenceEquals(directListener, listener)) return;
                chat.DataChanged();
                bool teacher = LanInvitation.LocalAddresses().Contains(settings.TeacherIp) || settings.TeacherIp == "127.0.0.1";
                var reaction = notice.Reaction is { } id ? ClassroomReactions.Find(id) : null;
                if (settings.ShowReactionPreviews && reaction is not null && (!reaction.TeacherOnly || teacher)) pet.NotifyReaction(reaction, notice.Scope.ContextId);
                else pet.NotifyChat("新訊息", notice.Scope.ContextId, chat.LegacyNotificationName(notice));
            }));
            listener.V2MessageStored += notice => Dispatcher.BeginInvoke(new Action(() => {
                if (closingApp || !ReferenceEquals(directListener, listener)) return;
                chat.DataChanged(); pet.NotifyChat("新訊息", "peer:" + notice.SenderId, chat.PeerNotificationName(notice.SenderId));
            }));
            listener.Diagnostic += code => { if (code == "receiver_not_ready") Dispatcher.BeginInvoke(new Action(() => { if (!closingApp && ReferenceEquals(directListener, listener)) chat.SetReceiverState(false, startupPort, listener.Error); })); };
            candidate.Start(); chatRuntime = candidate; candidate = null;
            chat.SetReceiverState(directListener.Running, directListener.Port, directListener.Error);
            chat.SetStatus(chatRuntime is { CanSend: true } ? "本機資料已就緒。" : "本機資料已就緒；教室傳送服務尚未就緒。");
        }
        catch (Exception ex)
        { chat.DetachRuntime(); chat.ShowDataFailure(ex is StoreException ? ex.Message : "無法開啟本機資料，請重新讀取。"); chat.SetReceiverState(false, startupPort, "本機資料尚未就緒"); }
        finally { if (candidate is not null) await candidate.DisposeAsync(); startingData = false; }
    }

    private async Task RequestShutdownAsync()
    {
        if (_recoveringIdentity) { _shutdownAfterIdentityRecovery = true; _identityRecoveryCancellation?.Cancel(); return; }
        if (closingApp) return; closingApp = true;
        try { chat.BeginClosing(); if (chatRuntime is not null) await chatRuntime.StopReceivingAsync(); await _activeLegacySend; await chat.PrepareForShutdownAsync(); if (chatRuntime is not null) await chatRuntime.DisposeAsync(); Shutdown(); }
        catch
        {
            try { if (chatRuntime is not null) await chatRuntime.DisposeAsync(); } catch { }
            chatRuntime = null; directListener = null; chat.DetachRuntime(); chat.AbortClosing(); closingApp = false;
            MessageBox.Show(chat, "關閉前無法完成存稿。這次不關閉；請保留目前文字，並處理資料讀寫問題後重試。", "無法完成存稿", MessageBoxButton.OK, MessageBoxImage.Warning);
            await StartDataAsync();
        }
    }
}
