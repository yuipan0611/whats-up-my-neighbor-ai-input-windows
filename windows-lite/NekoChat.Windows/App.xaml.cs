using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core;
using NekoChat.Core.Storage;
using NekoChat.Core.Diagnostics;

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
    private UiTestOptions? _uiTestOptions;
    private ChatRuntime? chatRuntime;
    private SettingsLoadResult? startupSource;
    private bool isolatedData, startingData, closingApp;
    private int startupPort = DirectLanMessaging.DefaultPort;
    private bool classroomShareNetwork, nativeDndIsolated;
    private ChatWireConfiguration? startupWireConfiguration;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] startupArgs = e.Args;
#if UI4_LOCAL_PREVIEW
        if (startupArgs.Length == 0) startupArgs = new[] { "--ui4-native-preview", System.IO.Path.Combine(@"C:\NekoChatUI4QA20261105", System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)!.InformationalVersion.Split('+')[0]) };
#endif
#if DND_NATIVE_AUTOTEST
        if (startupArgs.Length == 0) startupArgs = new[] { "--dnd-native-smoke-test", System.IO.Path.Combine(@"C:\NekoChatDndQA20261105", "Run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]) };
#endif
#if CLASSROOM_FILES_LOCAL_TEST
        if (startupArgs.Length == 0) startupArgs = new[] { "--classroom-files-local-test", @"C:\NekoChatClassroomFilesTest20261105\Files2" };
#endif

        if (startupArgs.Length > 0 && startupArgs[0] == "--restore-backup")
        {
            Environment.Exit(OfflineRestoreCommand.Run(startupArgs));
            return;
        }

        if (startupArgs.Length == 2 && startupArgs[0] == "--diagnostics-smoke-test")
        {
            Environment.Exit(await RunDiagnosticSmokeAsync(startupArgs[1]));
            return;
        }

        if (startupArgs.Length == 2 && startupArgs[0] == "--contacts-ui-smoke-test")
        {
            Environment.Exit(await RunContactsUiSmokeAsync(startupArgs[1]));
            return;
        }

        if (startupArgs.Length == 2 && startupArgs[0] == "--unread-smoke-test")
        {
            Environment.Exit(await RunUnreadSmokeAsync(startupArgs[1]));
            return;
        }

        if (startupArgs.Length == 2 && startupArgs[0] == "--message-recovery-smoke-test")
        {
            Environment.Exit(await RunMessageRecoverySmokeAsync(startupArgs[1]));
            return;
        }
        if (startupArgs.Length == 2 && startupArgs[0] == "--manual-send-cancel-smoke-test")
        {
            Environment.Exit(await RunManualSendCancelSmokeAsync(startupArgs[1]));
            return;
        }


        if (startupArgs.Length == 2 && startupArgs[0] == "--update-download-smoke-test")
        {
            Environment.Exit(UpdateDownloadSmoke.Run(startupArgs[1]));
            return;
        }

        if (startupArgs.Length == 2 && startupArgs[0] == "--smoke-test")
        {
            Environment.Exit(SmokeTest.Run(startupArgs[1]));
            return;
        }

        try
        {
            bool dndNativeSmoke = startupArgs.Length == 2 && startupArgs[0] == "--dnd-native-smoke-test";
            nativeDndIsolated = dndNativeSmoke;
            if (dndNativeSmoke) RetirePriorDndFixtures();
            string? dndDirectory = dndNativeSmoke ? startupArgs[1] : null;
            if (dndNativeSmoke) startupArgs = new[] { "--ui-test-data", dndDirectory!, "--ui-test-port", (19000 + Random.Shared.Next(10000)).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            classroomShareNetwork = startupArgs.Length == 2 && startupArgs[0] == "--classroom-files-local-test";
            if (classroomShareNetwork) startupArgs = new[] { "--ui-test-data", startupArgs[1], "--ui-test-port", "18775" };
            bool ui4NativePreview = startupArgs.Length == 2 && startupArgs[0] == "--ui4-native-preview";
            var uiTestOptions = UiTestOptions.Parse(ui4NativePreview ? new[] { "--ui-test-data", startupArgs[1], "--ui-test-port", "18773" } : startupArgs);
            _uiTestOptions = uiTestOptions;
            var messagingSmoke = MessagingSmokeOptions.Parse(startupArgs);
            bool keyboardSmoke = startupArgs.Length == 2 && startupArgs[0] == "--keyboard-smoke-test";
            bool gestureSmoke = startupArgs.Length == 2 && startupArgs[0] == "--interaction-smoke-test";
            bool receiverSmoke = startupArgs.Length == 2 && startupArgs[0] == "--receiver-failure-smoke-test";
            bool instanceSmoke = startupArgs.Length == 2 && startupArgs[0] == "--instance-smoke-test";
            bool instanceProbe = startupArgs.Length == 2 && startupArgs[0] == "--instance-probe";
            if (messagingSmoke is null && !gestureSmoke && !receiverSmoke && !keyboardSmoke)
            {
                string mutexName = uiTestOptions?.MutexName ?? "Local\\NekoChat.WindowsLite.App";
                instanceGate = new Mutex(true, mutexName, out bool firstInstance);
                if (instanceProbe)
                {
                    Directory.CreateDirectory(startupArgs[1]);
                    File.WriteAllText(Path.Combine(startupArgs[1], "duplicate-result.txt"), firstInstance ? "FAIL: second app acquired the gate" : "PASS: duplicate app stopped before settings, listener or windows");
                    Environment.Exit(firstInstance ? 1 : 0);
                    return;
                }
                if (!firstInstance)
                {
                    if (uiTestOptions is not null)
                    {
                        Shutdown(1);
                        return;
                    }
                    MessageBox.Show("NekoChat 已經在執行。請使用桌面上現有的貓咪；雙擊貓咪可開啟聊天。若要更新，請先從貓咪右鍵選「離開」。", "NekoChat 已開啟");
                    Shutdown();
                    return;
                }
            }
            var settingsSource = (startupArgs.Length == 0 || uiTestOptions is not null) ? Settings.LoadWithStatus(uiTestOptions?.SettingsFilePath) : null;
            string? smokeDirectory = messagingSmoke?.Directory ?? (startupArgs.Length == 2 && (keyboardSmoke || gestureSmoke || receiverSmoke || instanceSmoke ||
                startupArgs[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test") ? startupArgs[1] : null);
            if (startupArgs.Length > 0 && smokeDirectory is null && uiTestOptions is null) throw new ArgumentException("不支援這組啟動參數。");
            settings = settingsSource?.Value ?? (smokeDirectory is not null ? Settings.ForSmoke(smokeDirectory) : new Settings { AutoStartOnLogin = false, ReadOnlyFallback = true });
            if (uiTestOptions is not null) settings = settings with { AutoStartOnLogin = false };
            if (dndNativeSmoke)
            {
                if (File.Exists(uiTestOptions!.DatabaseFilePath)) throw new InvalidOperationException("Native drag test requires a fresh isolated profile.");
                settings = Settings.ForUiTest(uiTestOptions.DataDirectory) with { AutoStartOnLogin = false, WanderEnabled = false, ClassRosterInitialized = true,
                    Contacts = new() { new("192.168.255.250", "拖曳測試對象") }, LastRecipientIp = "192.168.255.250", TeacherIp = "192.168.255.250" };
                settingsSource = new SettingsLoadResult(settings, SettingsSourceStatus.Loaded);
            }
            // Explicit role in the isolated messaging fixture; loopback is transport only, never a role heuristic.
            if (messagingSmoke is not null) settings = settings with { ThisDeviceIsTeacher = true, ThisDeviceTeacherExplicitChoice = true };
            directSendPort = uiTestOptions?.Port ?? messagingSmoke?.PeerPort ?? DirectLanMessaging.DefaultPort;
            startupPort = uiTestOptions?.Port ?? messagingSmoke?.ListenPort ?? DirectLanMessaging.DefaultPort;

            if (settings.AutoStartOnLogin && uiTestOptions is null)
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
            if (uiTestOptions is not null)
            {
                pet.DisableCheckUpdates();
            }
            else
            {
                pet.CheckUpdates += () => _ = CheckForUpdatesAsync(true);
            }

            chat.AutoStartChanged += enabled =>
            {
                pet.UpdateAutoStartChecked(enabled);
                if (uiTestOptions is null)
                {
                    LoginStartup.Apply(enabled);
                }
            };
            chat.SendRequested += async (targetIp, text) => await SendDirectMessageAsync(targetIp, text);
            chat.ReactionRequested += async (targetIp, reaction) => await SendDirectMessageAsync(targetIp, reaction.Text, reaction, preserveDraft: true);
            pet.ExitRequested += async () => await RequestShutdownAsync();
            startupSource = settingsSource ?? new SettingsLoadResult(settings, (smokeDirectory is not null || uiTestOptions is not null) ? SettingsSourceStatus.Loaded : SettingsSourceStatus.Unavailable);
            isolatedData = smokeDirectory is not null || uiTestOptions is not null;
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
            if (ui4NativePreview) { pet.Hide(); await chat.RunUi4NativePreviewAsync(uiTestOptions!.DataDirectory); }
            if (dndNativeSmoke) { pet.Hide(); _ = Dispatcher.BeginInvoke(new Action(async () => await RunNativeDndAsync(dndDirectory!))); }
            SystemEvents.PowerModeChanged += AppPowerModeChanged;

            if (keyboardSmoke)
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunKeyboardSmokeTestAsync(startupArgs[1])));

            if (instanceSmoke)
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunInstanceSmokeTestAsync(startupArgs[1])));
            if (receiverSmoke)
                _ = Dispatcher.BeginInvoke(new Action(() => RunReceiverFailureSmokeTest(startupArgs[1])));

            if (messagingSmoke is not null)
            {
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunMessagingSmokeTestAsync(messagingSmoke)));
            }
            if (gestureSmoke)
            {
                _ = Dispatcher.BeginInvoke(new Action(async () => await RunInteractionSmokeTestAsync(startupArgs[1])));
            }

            if (startupArgs.Length == 0 && uiTestOptions is null)
            {
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => _ = CheckForUpdatesAsync(false)));
            }

            if (startupArgs.Length == 2 && startupArgs[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test")
            {
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    try
                    {
                        Directory.CreateDirectory(startupArgs[1]);
                        Require(pet.IsVisible && chat.IsVisible && directListener?.Running == true, "Initial windows or listener state incorrect");
                        await RunAttachmentStartupChecksAsync(startupArgs[1]);
                        File.WriteAllText(Path.Combine(startupArgs[1], "startup-result.txt"), "PASS: Lite LAN startup/UI; attachment filesystem failures preserve text TCP and pending drafts; repair/restart recovers; corrupt database remains fail-closed.");
                        Environment.Exit(0);
                    }
                    catch (Exception error)
                    {
                        File.WriteAllText(Path.Combine(startupArgs[1], "startup-result.txt"), "FAIL: " + error);
                        Environment.Exit(1);
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            if (startupArgs.Length == 5 && startupArgs[0] == "--messaging-smoke-test")
            {
                Directory.CreateDirectory(startupArgs[1]);
                File.WriteAllText(Path.Combine(startupArgs[1], startupArgs[2] + "-result.txt"), "FAIL: fixture startup: " + ex.GetType().Name);
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
        if (reaction is null || !chat.IsSelectedReactionTarget(targetIp, reaction))
        { chat.SetStatus("聊天對象已變更，貼圖尚未送出。"); return; }
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
        if (_uiTestOptions is not null)
        {
            if (manual) chat.SetStatus("UI 測試模式已停用檢查更新。");
            return;
        }
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
            { closingApp = true; chat.BeginClosing(); chatRuntime.StopReceivingAsync().GetAwaiter().GetResult(); chatRuntime.DrainNetworkAsync().GetAwaiter().GetResult(); try { chat.FlushForDispatcherExitAsync().GetAwaiter().GetResult(); } catch { } }
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
            chat.SetDiagnosticsDirectory(Path.Combine(ChatRuntime.RootDirectory(settings, isolatedData), "diagnostics"));
            candidate = await ChatRuntime.OpenAsync(startupSource, isolatedData, startupPort, startupWireConfiguration, loopbackOnly: nativeDndIsolated);
            if (closingApp) return;
            await chat.AttachRuntimeAsync(candidate, directSendPort);
            candidate.ShareProgress += (id, done, total) => Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) chat.ShowShareProgress(id, done, total); }));
            candidate.AttachmentProgress += (id, done, total) => Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) chat.ShowAttachmentProgress(id, done, total); }));
            candidate.DataChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) chat.DataChanged(); }));
            var listener = candidate.Listener; directListener = listener;
            var notificationRuntime = candidate;
            listener.LegacyMessageStored += notice => _ = NotifyAfterBlockCheckAsync(notificationRuntime, listener, notice.SourceIp, () => {
                chat.DataChanged();
                bool teacher = IsReactionTeacher(notificationRuntime, settings);
                var reaction = notice.Reaction is { } id ? ClassroomReactions.Find(id) : null;
                NotifyReactionOrText(settings, pet, reaction, teacher, notice.Scope.ContextId, chat.LegacyNotificationName(notice));
            });
            listener.V2MessageStored += notice => Dispatcher.BeginInvoke(new Action(() => {
                if (closingApp || !ReferenceEquals(directListener, listener)) return;
                chat.DataChanged(); pet.NotifyChat("新訊息", "peer:" + notice.SenderId, chat.PeerNotificationName(notice.SenderId));
            }));
            listener.ClassroomMessageStored += notice => _ = NotifyAfterBlockCheckAsync(notificationRuntime, listener, notice.SourceIp, () => {
                chat.DataChanged();
                bool teacher = IsReactionTeacher(notificationRuntime, settings);
                var reaction = notice.Reaction is { } id ? ClassroomReactions.Find(id) : null;
                NotifyReactionOrText(settings, pet, reaction, teacher, notice.SourceIp, chat.ClassroomNotificationName(notice.SourceIp));
            });
            listener.Diagnostic += code => { if (code is "receiver_not_ready" or "listener_failed") Dispatcher.BeginInvoke(new Action(() => { if (!closingApp && ReferenceEquals(directListener, listener)) chat.SetReceiverState(false, startupPort, listener.Error ?? "接收服務已停止"); })); };
            candidate.Start(sharedNetwork: classroomShareNetwork); chatRuntime = candidate; candidate = null;
            chat.SetReceiverState(directListener.Running, directListener.Port, directListener.Error);
            chat.SetStatus(chatRuntime is { CanSend: true } ? "本機資料已就緒。" : "本機資料已就緒；教室傳送服務尚未就緒。");
        }
        catch (Exception ex)
        { chat.DetachRuntime(); chat.ShowDataFailure(ex); chat.SetReceiverState(false, startupPort, "本機資料尚未就緒"); }
        finally { if (candidate is not null) await candidate.DisposeAsync(); startingData = false; }
    }

    private static bool IsReactionTeacher(ChatRuntime runtime, Settings preferences) =>
        TeacherDevicePolicy.IsTeacher(preferences.ThisDeviceIsTeacher, preferences.ThisDeviceTeacherExplicitChoice, runtime.ClassroomFiles?.IsTeacherNode == true);

    private static ReactionNotificationKind NotifyReactionOrText(Settings preferences, PetWindow target, ClassroomReaction? reaction, bool teacher, string source, string name)
    {
        var kind = ReactionPreviewPolicy.Notification(preferences.ShowReactionPreviews, reaction, teacher);
        if (kind == ReactionNotificationKind.Image) target.NotifyReaction(reaction!, source);
        else if (kind == ReactionNotificationKind.Text) target.NotifyChat("新訊息", source, name);
        return kind;
    }

    private async Task NotifyAfterBlockCheckAsync(ChatRuntime runtime, DirectLanListener listener, string sourceIp, Action notify)
    {
        try
        {
            bool allowed = await runtime.Worker.Run(s => !s.IsSourceBlocked(sourceIp)).ConfigureAwait(false);
            if (!allowed) return;
            await Dispatcher.InvokeAsync(() => {
                if (!closingApp && ReferenceEquals(chatRuntime, runtime) && ReferenceEquals(directListener, listener)) notify();
            });
        }
        catch (Exception error) when (DiagnosticFault.Catchable(error))
        {
            // Unknown block state fails closed; no source address or message UUID is recorded.
            runtime.Diagnostics.Record(DiagnosticStage.ReceiveNotify, DiagnosticOutcome.Failed, error: error);
        }
    }

    private async Task RequestShutdownAsync()
    {
        if (_recoveringIdentity) { _shutdownAfterIdentityRecovery = true; _identityRecoveryCancellation?.Cancel(); return; }
        if (closingApp) return; closingApp = true;
        try { chat.BeginClosing(); if (chatRuntime is not null) await chatRuntime.StopReceivingAsync(); await _activeLegacySend; await chat.PrepareForShutdownAsync(); if (chatRuntime is not null) await chatRuntime.DisposeAsync(); Shutdown(); }
        catch (Exception ex)
        {
            chatRuntime?.Diagnostics.Record(DiagnosticStage.Shutdown, DiagnosticOutcome.Failed, error: ex);
            try { if (chatRuntime is not null) await chatRuntime.DisposeAsync(); } catch { }
            chatRuntime = null; directListener = null; chat.DetachRuntime(); chat.AbortClosing(); closingApp = false;
            string displayFault = DiagnosticFault.FromException(ex).DisplayCode;
            MessageBox.Show(chat, $"關閉前無法完成存稿。這次不關閉；請保留目前文字，並處理資料讀寫問題後重試。（{displayFault}）", "無法完成存稿", MessageBoxButton.OK, MessageBoxImage.Warning);
            await StartDataAsync();
        }
    }
}
