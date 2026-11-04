using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    // Explicit QA entry only. No network send, daily profile, shell or firewall operation.
    internal async Task RunUi4NativePreviewAsync(string profileDirectory)
    {
        string evidence = Path.Combine(profileDirectory, "Evidence"); Directory.CreateDirectory(evidence);
        try
        {
            if (_runtime is not { IsolatedData: true, ArchivedIdentityFixture: false, Attachments: { } service } runtime)
                throw new InvalidOperationException("隔離資料尚未就緒，未執行原生預覽。");
            await runtime.Worker.Run(s => s.SetReadReceiptPolicy(false));
            var teacher = _snapshot?.Entries.FirstOrDefault(x => x.IsTeacher && !x.Removed && x.Endpoint is not null);
            if (teacher?.ConversationId is null || !ClassroomPrincipalKey.TryParse(teacher.ConversationId, out var principal))
                throw new InvalidOperationException("測試包缺少老師名單，未製造測試資料。");
            string marker = Path.Combine(profileDirectory, "ui4-preview-seeded.txt");
            if (!File.Exists(marker))
            {
                foreach (var sample in new[] { ("課堂範例.py", "for i in range(3):\n    print('你好', i)\n"), ("課堂筆記.txt", "這是隔離預覽資料，不是老師真正分享的教材。\n") })
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(sample.Item2);
                    var wire = new AttachmentFile(Guid.NewGuid().ToString("N"), sample.Item1, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                    var row = await runtime.Worker.Run(s => ((ChatStore)s).ReceiveAttachmentOffer(teacher.Endpoint!, service.Port,
                        Guid.NewGuid().ToString("D"), wire, "隔離預覽教材", AttachmentLimits.Token(), () => true).Record!);
                    await using (var part = service.Vault.Reserve(row.File.BlobId, bytes.Length)) { await part.WriteAsync(bytes); }
                    await service.Vault.CommitAsync(row.File, default);
                    await runtime.Worker.Run(s => ((ChatStore)s).SetAttachmentState(row, "complete"));
                }
                await runtime.Worker.Run(s => {
                    var draft = s.GetClassroomDraftState(principal);
                    var saved = s.SaveClassroomDraft(principal, "這一段可以再講一次嗎？", draft.Epoch, draft.Revision + 1);
                    if (saved.Saved && s.BeginClassroomSend(principal, "這一段可以再講一次嗎？", draft.Epoch, draft.Revision + 1) is AttemptStarted attempt)
                        s.CompleteClassroomAttempt(attempt.MessageId, new FailedOutcome("native_preview_fixture"), null, attempt.TryCount);
                    return true;
                });
                File.WriteAllText(marker, "Isolated local fixture only; no messages sent.");
            }
            await RefreshInboxAsync(true);
            teacher = _snapshot!.Entries.Single(x => x.Id == teacher.Id);
            WindowState = WindowState.Normal;
            double normalWidth = Math.Max(MinWidth, Math.Min(1040, SystemParameters.WorkArea.Width - 24));
            Width = normalWidth; Height = Math.Max(MinHeight, Math.Min(720, SystemParameters.WorkArea.Height - 24));
            Show(); Activate(); await SelectEntryAsync(teacher, false);
            await CaptureUi4VisualAsync(Path.Combine(evidence, "01-teacher-chat.png"));
            _showTeacherFiles = true; _showInbox = false; _userOpened = false;
            UpdateHeader(); UpdateComposer(); UpdateShellLayout(); ViewChanged(); await LoadTeacherFilesAsync(false);
            await CaptureUi4VisualAsync(Path.Combine(evidence, "02-teacher-files.png"));
            Width = 520; await CaptureUi4VisualAsync(Path.Combine(evidence, "03-narrow-teacher-files.png"));
            Width = normalWidth; UpdateShellLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            File.WriteAllText(Path.Combine(evidence, "native-result.json"), JsonSerializer.Serialize(new {
                result = "WPF rendering captured", captureKind = "Actual Windows WPF RenderTargetBitmap, not desktop or input acceptance",
                sourceVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ChatWindow).Assembly)?.InformationalVersion,
                dpiX = dpi.PixelsPerInchX, dpiY = dpi.PixelsPerInchY,
                width = ActualWidth, height = ActualHeight, teacherFileRows = _teacherFiles.Count,
                profile = "isolated QA", networkMessagesSentByFixture = 0,
                unexecuted = new[] { "Native keyboard/IME", "Real clipboard and drag/drop", "Other DPI settings", "Two Windows delivery" }
            }, new JsonSerializerOptions { WriteIndented = true }));
            SetStatus("隔離預覽已開啟；示範資料不會自動傳給老師。原生渲染圖已保存。");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "native-result.json"), JsonSerializer.Serialize(new { result = "failed", error = ex.Message }));
            SetStatus("原生預覽未完成：" + ex.Message);
        }
        try
        {
            string target = Path.Combine(@"\\Mac\NekoChat-回傳\UI4-native-20261105", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(target);
            foreach (string name in new[] { "01-teacher-chat.png", "02-teacher-files.png", "03-narrow-teacher-files.png", "native-result.json" })
                if (File.Exists(Path.Combine(evidence, name))) File.Copy(Path.Combine(evidence, name), Path.Combine(target, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("原生預覽證據保留在隔離資料夾；共用資料夾暫時無法寫入。"); }
    }
    private async Task CaptureUi4VisualAsync(string path)
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
}
