using System;
using System.Threading;
using System.Windows.Forms;
using System.Security.Principal;

namespace Neighbor.Windows
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool smoke = Array.IndexOf(args, "--smoke-test") >= 0;
            string user = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, "Local\\WhatsUpMyNeighbor-" + user + (smoke ? "-smoke" : ""), out bool created))
            {
                if (!created) { if (!smoke) MessageBox.Show("What's up my neighbor 已在系統匣執行。", "What's up my neighbor"); return 0; }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += (_, e) => MessageBox.Show("操作未完成，請重新開啟 App 後再試。你的設定與詞典會保留。", "What's up my neighbor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    int code;
                    using (var app = new NeighborApplicationContext(smoke)) { Application.Run(app); code = app.Failed ? 1 : 0; }
                    return code;
                }
                catch
                {
                    if (!smoke) MessageBox.Show("App 未能啟動。請確認系統為 Windows 10 22H2／Windows 11 x64，且已啟用 .NET Framework 4.8。", "What's up my neighbor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
