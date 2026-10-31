using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NekoChat.Core;

namespace NekoChat.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--configure-firewall")return LanFirewall.RunHelper(args[1]);
        if(args.Length==2 && args[0]=="--smoke-install")return Smoke(args[1]);
        if(args.Length!=0)return 2;
        using var gate=new Mutex(true,"Local\\NekoChat.WindowsLite.Installer",out bool acquired);
        if(!acquired){MessageBox.Show("另一個 NekoChat 安裝程式正在執行。","NekoChat");return 1;}
        var app=new Application();
        var window=new Window{Title="安裝 NekoChat Windows Lite",Width=480,Height=440,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,
            Background=new SolidColorBrush(Color.FromRgb(250,249,247))};
        var panel=new StackPanel{Margin=new Thickness(28)};
        panel.Children.Add(new TextBlock{Text="讓桌面貓咪陪你教室聊天、內網通訊",FontSize=22,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="安裝至目前 Windows 使用者，建立開始功能表捷徑。所需元件已包含。安裝時會要求一次管理員授權，讓同網段同學可以傳訊到這台電腦。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,16)});
        var button=new Button{Content="安裝並啟動",Padding=new Thickness(16,10,16,10),HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(button);
        var status=new TextBlock{Text="教室純文字版，不需要麥克風或 API Key；原有設定與花色會保留。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,0)};panel.Children.Add(status);
        var skip=new Button{Content="略過網路設定並啟動（可能無法收訊）",Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,0),Padding=new Thickness(10),HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(skip);
        InstallationUpdate? pending=null;
        bool installing=false;
        void Launch()
        {
            var transaction=pending??throw new InvalidOperationException("尚未完成安裝。");
            var result=transaction.AcceptLaunch(folder=>
            {
                using var process=Process.Start(new ProcessStartInfo(Path.Combine(folder,"NekoChat.Windows.exe")){WorkingDirectory=folder,UseShellExecute=true})
                    ??throw new IOException("無法啟動新版，將還原原版。");
            });
            var notices=result.Warnings.ToList();
            try{CreateShortcut(transaction.Destination);}catch(Exception ex)when(ex is IOException or System.Runtime.InteropServices.COMException)
            {notices.Add("新版已啟動，但開始功能表捷徑未完成。"+ex.Message);}
            if(result.PreservedFiles>0)notices.Add("自訂或無法確認歸屬的檔案已保留於："+PreservedFilesRoot());
            transaction.Dispose();pending=null;installing=false;
            if(notices.Count>0)MessageBox.Show(string.Join("\n\n",notices),"NekoChat 安裝完成",MessageBoxButton.OK,MessageBoxImage.Information);
            window.Close();
        }
        skip.Click+=(_,_)=>{if(!installing && pending is not null){try{Launch();}catch(Exception ex){status.Text=ex.Message;if(pending is not null && !pending.IsPending){pending.Dispose();pending=null;}button.Content="重試安裝";}}};
        window.Closing+=(_,e)=>
        {
            if(installing){e.Cancel=true;return;}
            try{pending?.Dispose();}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {status.Text="還原未完成，請保留安裝位置旁的暫存與舊版資料夾。"+ex.Message;e.Cancel=true;pending=null;}
        };
        button.Click+=async(_,_)=> {
            installing=true;button.IsEnabled=false;skip.IsEnabled=false;status.Text="正在安裝…";
            try {
                if(pending is null)pending=await Task.Run(Install);
                status.Text="正在設定同網段收訊權限，請確認 Windows 管理員授權提示…";
                await ConfigureFirewallAsync(Path.Combine(pending.Destination,"NekoChat.Windows.exe"));
                Launch();
            }catch(Exception ex){
                if(pending is not null && !pending.IsPending){pending.Dispose();pending=null;}
                status.Text=ex is Win32Exception {NativeErrorCode:1223}
                    ?"程式已安裝，但你尚未同意網路設定。可重試管理員授權；略過後可能無法收到同學訊息。"
                    :ex is InvalidDataException or IOException or InvalidOperationException?ex.Message
                    :"安裝或網路設定未完成。請確認已離開舊版程式，且學校允許安裝與內網通訊。";
                button.Content=pending is null?"重試安裝":"重試網路設定";
                button.IsEnabled=true;skip.IsEnabled=true;skip.Visibility=pending is null?Visibility.Collapsed:Visibility.Visible;
            }
            finally{installing=false;}
        };
        window.Content=panel;app.Run(window);return 0;
    }
    private static async Task ConfigureFirewallAsync(string programPath)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            LanFirewall.Configure(programPath);
            return;
        }
        var start=new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--configure-firewall");
        start.ArgumentList.Add(programPath);
        using var helper=Process.Start(start)??throw new IOException("無法開啟網路設定。");
        await helper.WaitForExitAsync();
        if(helper.ExitCode!=0)
            throw new InvalidOperationException("程式已安裝，但網路設定未完成。請重試並授予管理員權限；若學校政策禁止，請聯絡管理者。");
    }

    private static Stream VerifiedPayload()
    {
        var assembly=typeof(Program).Assembly;
        var payload=assembly.GetManifestResourceStream("payload.zip")??throw new InvalidDataException("此安裝檔缺少程式內容，請重新下載完整安裝檔。");
        try {
            using var digestStream=assembly.GetManifestResourceStream("payload.sha256")??throw new InvalidDataException("安裝檔缺少完整性檢查資料。");
            using var reader=new StreamReader(digestStream);
            var expected=reader.ReadToEnd().Trim();var actual=Convert.ToHexString(SHA256.HashData(payload));
            if(expected.Length!=64 || !actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安裝檔不完整，請重新下載。");
            payload.Position=0;return payload;
        }catch{payload.Dispose();throw;}
    }
    private static InstallationUpdate Install()
    {
        if(new[]{"NekoChat.Windows","NekoChat-Lite"}.SelectMany(Process.GetProcessesByName).Any(p=>{using(p)return p.SessionId==Process.GetCurrentProcess().SessionId;}))
            throw new IOException("請先從貓咪右鍵選單離開舊版 NekoChat，再按安裝。");
        var parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs");
        return InstallAt(parent);
    }
    private static string PreservedFilesRoot()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NekoChat.WindowsLite","UpgradePreservedFiles");
    private static InstallationUpdate InstallAt(string parent,string? preservedRoot=null)
    {
        using var payload=VerifiedPayload();
        return InstallationUpdate.Begin(payload,parent,preservedRoot??PreservedFilesRoot());
    }
    private static void CreateShortcut(string installed,string? shortcutPath=null)
    {
        var shellType=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("無法建立開始功能表捷徑。程式已安裝，可從安裝資料夾開啟。");
        dynamic shell=Activator.CreateInstance(shellType)!;
        dynamic link=shell.CreateShortcut(shortcutPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"NekoChat Windows Lite.lnk"));
        link.TargetPath=Path.Combine(installed,"NekoChat.Windows.exe");link.WorkingDirectory=installed;link.Save();
    }
    private static int Smoke(string directory)
    {
        var root=Path.GetFullPath(directory);
        var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!root.StartsWith(temporary,StringComparison.OrdinalIgnoreCase))return 2;
        Directory.CreateDirectory(root);
        try {
            var parent=Path.Combine(root,"Programs");
            var recovery=Path.Combine(root,"user-data","UpgradePreservedFiles");
            void StartSmoke(string folder,string name)
            {
                var start=new ProcessStartInfo(Path.Combine(folder,"NekoChat.Windows.exe")){WorkingDirectory=folder,UseShellExecute=false};
                start.ArgumentList.Add("--startup-smoke-test");start.ArgumentList.Add(Path.Combine(root,name));
                using var child=Process.Start(start)??throw new IOException("Installed App could not launch");
                if(!child.WaitForExit(45000)){child.Kill(entireProcessTree:true);throw new IOException("Installed App startup timed out");}
                if(child.ExitCode!=0 || !File.Exists(Path.Combine(root,name,"startup-result.txt")))throw new IOException("Installed App startup failed");
            }
            string installed;
            using(var fresh=InstallAt(parent,recovery))
            {fresh.AcceptLaunch(folder=>StartSmoke(folder,"fresh-startup"));installed=fresh.Destination;}
            File.WriteAllText(Path.Combine(installed,"user-artwork-sentinel.txt"),"preserve-test-only-artwork");
            var settings=Path.Combine(root,"settings-sentinel.txt");File.WriteAllText(settings,"preserve-test-only-settings");
            using(var upgrade=InstallAt(parent,recovery))
            {
                var result=upgrade.AcceptLaunch(folder=>StartSmoke(folder,"startup"));
                if(result.Warnings.Count!=0 || result.PreservedFiles!=1 || Directory.GetDirectories(parent,".nekochat-previous-*").Length!=0)
                    throw new IOException("Upgrade did not clean completed backups");
                installed=upgrade.Destination;
            }
            var artwork=Directory.GetFiles(recovery,"user-artwork-sentinel.txt",SearchOption.AllDirectories).Single();
            if(File.ReadAllText(artwork)!="preserve-test-only-artwork" || File.ReadAllText(settings)!="preserve-test-only-settings")
                throw new IOException("Upgrade did not preserve custom artwork/settings");
            var shortcut=Path.Combine(root,"NekoChat Test.lnk");CreateShortcut(installed,shortcut);
            if(!File.Exists(shortcut))throw new IOException("Shortcut creation failed");
            File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: single-file setup checksum, fresh install, transactional upgrade, successful-launch old-file cleanup, preserved custom artwork/settings, Start menu shortcut mechanism and actual installed App startup; no external Python/.NET installation or config edits.");return 0;
        }catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.txt"),"FAIL: "+ex.GetType().Name+" "+ex.Message);return 1;}
    }
}
