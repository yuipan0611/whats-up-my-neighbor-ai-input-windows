using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class App : Application
{
    private Settings settings = new();
    private PetWindow pet = null!;
    private ChatWindow chat = null!;
    private DirectLanListener? directListener;
    private int directSendPort = DirectLanMessaging.DefaultPort;
    private Mutex? instanceGate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            settings = e.Args.Length == 0 ? Settings.Load() : new Settings { AutoStartOnLogin = false };
            directSendPort = messagingSmoke?.PeerPort ?? DirectLanMessaging.DefaultPort;

            if (settings.AutoStartOnLogin)
            {
                LoginStartup.Apply(true);
            }

            pet = new PetWindow();
            chat = new ChatWindow(settings);
            pet.SetWanderEnabled(settings.WanderEnabled);
            pet.WanderToggled += enabled =>
            {
                settings = Settings.Load() with { WanderEnabled = enabled };
                settings.Save();
                chat.UpdateWanderEnabled(enabled);
            };

            pet.OpenChat += targetIp => chat.ShowChat(targetIp);
            pet.SelectCoat += id =>
            {
                try
                {
                    pet.SetPreset(id);
                    settings = settings with { Coat = id };
                    settings.Save();
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

            directListener = new DirectLanListener(messagingSmoke?.ListenPort ?? DirectLanMessaging.DefaultPort);
            directListener.MessageReceived += (senderIp, text) => Dispatcher.Invoke(() =>
            {
                var displayName = settings.ResolveDisplayName(senderIp);
                chat.AddHistory($"{displayName}：{text}");
                pet.NotifyChat(text, senderIp, displayName);
            });
            directListener.Start();
            chat.SetReceiverState(directListener.Running, directListener.Port, directListener.Error);

            if (Appearance.Presets.Contains(settings.Coat))
            {
                pet.SetPreset(settings.Coat);
            }

            pet.Show();
            chat.Show();

            if (keyboardSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunKeyboardSmokeTestAsync(e.Args[1])));

            if (instanceSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunInstanceSmokeTestAsync(e.Args[1])));
            if (receiverSmoke)
                Dispatcher.BeginInvoke(new Action(() => RunReceiverFailureSmokeTest(e.Args[1])));

            if (messagingSmoke is not null)
            {
                Dispatcher.BeginInvoke(new Action(async () => await RunMessagingSmokeTestAsync(messagingSmoke)));
            }
            if (gestureSmoke)
            {
                Dispatcher.BeginInvoke(new Action(async () => await RunInteractionSmokeTestAsync(e.Args[1])));
            }

            if (e.Args.Length == 0)
            {
                Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => _ = CheckForUpdatesAsync(false)));
            }

            if (e.Args.Length == 2 && e.Args[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test")
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Directory.CreateDirectory(e.Args[1]);
                    bool visible = pet.IsVisible && chat.IsVisible;
                    bool listenerRunning = directListener.Running;
                    File.WriteAllText(Path.Combine(e.Args[1], "startup-result.txt"),
                        visible && listenerRunning ? "PASS: Lite edition direct LAN startup and UI rendered." : "FAIL: initial windows or listener state were incorrect.");
                    Environment.Exit(visible && listenerRunning ? 0 : 1);
                }));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("NekoChat 無法啟動：" + ex.Message, "NekoChat");
            Shutdown(1);
        }
    }

    private async Task SendDirectMessageAsync(string targetIp, string text)
    {
        if (directListener?.Running != true)
        {
            chat.SetStatus("接收服務未啟動，請先處理下方提示後重開 NekoChat。（草稿保留）");
            return;
        }
        if (!chat.SendButton.IsEnabled) return;
        var ownIps = LanInvitation.LocalAddresses();
        if (ownIps.Contains(targetIp))
        {
            chat.SetStatus("這是自己的 IP，請填對方的內網 IP。");
            return;
        }

        chat.SendButton.IsEnabled = false;
        var displayName = settings.ResolveDisplayName(targetIp);
        chat.SetStatus($"正在傳送至 {displayName}…");
        try
        {
            await DirectLanMessaging.SendDirectAsync(targetIp, text, directSendPort);
            chat.AddHistory($"我 → {displayName}：{text}");
            if (chat.Draft.Text == text) chat.Draft.Clear();
            chat.SetStatus($"已送達 {displayName}！");
        }
        catch (Exception ex)
        {
            chat.SetStatus(SafeError(ex) + "（草稿保留）");
        }
        finally
        {
            chat.SendButton.IsEnabled = true;
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
        try
        {
            string currentVer = "0.1.0-beta.6";
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
                string url = !string.IsNullOrWhiteSpace(result.HtmlUrl)
                    ? result.HtmlUrl
                    : "https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases";
                string prompt = $"發現新版本 NekoChat Lite v{result.LatestVersion}！（目前版本：v{result.CurrentVersion}）\n\n{result.ReleaseTitle ?? ""}\n是否開啟瀏覽器前往下載頁面？";
                if (manual)
                {
                    if (MessageBox.Show(chat, prompt, "NekoChat — 檢查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { chat.SetStatus("無法開啟瀏覽器，請手動前往：" + url); }
                    }
                }
                else
                {
                    pet.Notify($"發現新版本 v{result.LatestVersion}！右鍵選單「檢查更新」可前往下載。");
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
            if (manual) MessageBox.Show(chat, "檢查更新時發生錯誤：" + ex.Message, "NekoChat — 檢查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = directListener?.DisposeAsync();
        instanceGate?.Dispose();
        base.OnExit(e);
    }
}
