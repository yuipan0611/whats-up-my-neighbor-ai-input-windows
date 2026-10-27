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
            settings = Settings.Load();

            if (settings.AutoStartOnLogin)
            {
                LoginStartup.Apply(true);
            }

            pet = new PetWindow();
            chat = new ChatWindow(settings);

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

            directListener = new DirectLanListener();
            directListener.MessageReceived += (senderIp, text) => Dispatcher.Invoke(() =>
            {
                var displayName = settings.ResolveDisplayName(senderIp);
                chat.AddHistory($"{displayName}：{text}");
                pet.NotifyChat(text, senderIp, displayName);
            });
            directListener.Start();

            if (Appearance.Presets.Contains(settings.Coat))
            {
                pet.SetPreset(settings.Coat);
            }

            pet.Show();
            chat.Show();

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
            await DirectLanMessaging.SendDirectAsync(targetIp, text);
            chat.AddHistory($"我 → {displayName}：{text}");
            chat.Draft.Clear();
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
            string currentVer = "0.1.0-beta.3";
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
        base.OnExit(e);
    }
}
