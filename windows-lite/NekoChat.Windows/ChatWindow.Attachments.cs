using NekoChat.Core.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core.Attachments;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    internal void ShowAttachmentProgress(long sequence, long done, long total)
    { foreach (var file in _teacherFiles.Where(x => x.Entry.Attachment.LocalSeq == sequence)) file.Progress = total == 0 ? 100 : (int)(done * 100 / total); var row = History.Rows.FirstOrDefault(r => r.Key == "cr:" + sequence); if (row is not null) row.AttachmentProgress = total == 0 ? 100 : (int)(done * 100 / total); }
    private AttachmentDraft? _attachmentDraft;
    private bool _attachmentBusy;
    private bool _attachmentDraftLoading;
    private long _attachmentLoad;
    private string? _attachmentConversation;
    private void InitializeAttachments()
    {
        CommandManager.AddPreviewCanExecuteHandler(Draft, (_, e) => {
            if (e.Command != ApplicationCommands.Paste || !Draft.IsEnabled || Draft.IsReadOnly) return;
            try { if (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage()) { e.CanExecute = true; e.Handled = true; } } catch (System.Runtime.InteropServices.COMException) { }
        });
        CommandManager.AddPreviewExecutedHandler(Draft, async (_, e) => {
            if (e.Command != ApplicationCommands.Paste) return;
            try
            {
                if (Clipboard.ContainsFileDropList())
                { e.Handled = true; await AddAttachmentPathsAsync(Clipboard.GetFileDropList().Cast<string>().ToArray()); }
                else if (Clipboard.ContainsImage())
                { e.Handled = true; var image = Clipboard.GetImage(); if (image is not null) await AddClipboardImageAsync(image); }
            }
            catch (Exception error) { e.Handled = true; SetStatus(AttachmentException.Describe(error)); }
        });
    }
    private async Task RefreshAttachmentDraftAsync()
    {
        if (_runtime is not { } runtime || !ClassroomPrincipalKey.TryParse(_selected?.ConversationId, out var principal))
        { _attachmentDraftLoading = false; _attachmentDraft = null; _attachmentConversation = null; RenderAttachmentDraft(); UpdateComposer(); return; }
        string conversation = principal.Value; long request = ++_attachmentLoad;
        _attachmentDraftLoading = true;
        if (_attachmentConversation != conversation) { _attachmentDraft = null; _attachmentConversation = conversation; RenderAttachmentDraft(); }
        UpdateComposer();
        try
        {
            var draft = runtime.Attachments is { } service ? await service.DraftAsync(principal)
                : await runtime.ReadAttachmentDraftAsync(principal);
            if (request != _attachmentLoad || !ReferenceEquals(runtime, _runtime) || _selected?.ConversationId != conversation) return;
            _attachmentDraftLoading = false; _attachmentDraft = draft; RenderAttachmentDraft(); UpdateComposer();
        }
        catch (Exception error) { if (!_closingRequested && request == _attachmentLoad && ReferenceEquals(runtime, _runtime)) { SetStatus("無法讀取附件草稿，請重新開啟此對話。文字已保留。" + AttachmentException.Describe(error)); UpdateComposer(); } }
    }
    private void RenderAttachmentDraft()
    {
        AttachmentDraftPanel.Visibility = _attachmentDraft is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateShellLayout();
        AttachmentDraftImage.Source = null;
        if (_attachmentDraft is not { } draft) return;
        AttachmentDraftText.ToolTip = draft.File.Name;
        AttachmentDraftText.Text = draft.File.Name + "\n" + AttachmentSize(draft.File.Size) + (_runtime?.Attachments is null ? " · 附件暫停；移除附件後可傳文字" : " · 待傳附件");
        if (_runtime?.Attachments is { } service && service.Vault.Exists(draft.File))
            AttachmentDraftImage.Source = AttachmentImage.Load(service.Vault.PathFor(draft.File.BlobId), draft.File.Name);
        AttachmentDraftImage.Visibility = AttachmentDraftImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }
    internal static string AttachmentSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : bytes + " B";
    private bool CanAddAttachment() => !_attachmentDraftLoading && !_attachmentBusy && !_sending && !_closingRequested && Draft.IsEnabled && !Draft.IsReadOnly && _runtime?.Attachments is not null;
    private async Task AddAttachmentPathsAsync(string[] paths)
    {
        if (!CanAddAttachment()) return;
        if (paths.Length != 1) { SetStatus("每則訊息目前只支援一個附件，請一次拖入一個檔案。"); return; }
        if (_attachmentDraft is not null) { SetStatus("請先移除目前附件，再加入另一個檔案。"); return; }
        if (_runtime?.Attachments is not { } service || !ClassroomPrincipalKey.TryParse(_selected?.ConversationId, out var principal)) return;
        _attachmentBusy = true; UpdateComposer(); SetStatus("正在複製並校驗附件…");
        try { await service.AddDraftAsync(principal, paths[0], _runtime.Lifetime); SetStatus("附件已準備，可傳送；尚未傳給對方。"); }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
        finally { _attachmentBusy = false; await RefreshAttachmentDraftAsync(); UpdateComposer(); }
    }
    private async Task AddClipboardImageAsync(BitmapSource image)
    {
        if (!CanAddAttachment()) return;
        if (_attachmentDraft is not null) { SetStatus("請先移除目前附件，再貼上另一張圖片。"); return; }
        if (!AttachmentImage.ValidDimensions(image.PixelWidth, image.PixelHeight)) { SetStatus("圖片尺寸太大，請縮小後再貼上。"); return; }
        if (_runtime?.Attachments is not { } service || !ClassroomPrincipalKey.TryParse(_selected?.ConversationId, out var principal)) return;
        _attachmentBusy = true; UpdateComposer();
        try
        {
            using var png = new BoundedPngStream();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(png); png.Position = 0;
            await service.AddDraftAsync(principal, png, "截圖-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", _runtime.Lifetime);
            SetStatus("截圖已加入待傳附件。");
        }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
        finally { _attachmentBusy = false; await RefreshAttachmentDraftAsync(); UpdateComposer(); }
    }
    private async void RemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (_attachmentDraft is not { } draft || _runtime is not { } runtime || !ClassroomPrincipalKey.TryParse(_selected?.ConversationId, out var principal)) return;
        try {
            if (runtime.Attachments is { } service) await service.RemoveDraftAsync(principal, draft.File.BlobId);
            else await runtime.RemoveAttachmentDraftMetadataAsync(principal, draft.File.BlobId);
            await RefreshAttachmentDraftAsync(); UpdateComposer(); SetStatus("待傳附件已移除。");
        }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
    }
    private async Task SendAttachmentCoreAsync(ChatRuntime runtime, InboxEntry entry, DraftEditBuffer buffer, DraftWriteSnapshot sent)
    {
        if (runtime.Attachments is not { } service || !ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal)) return;
        SetSending(true);
        try
        {
            await FlushDraftsAsync();
            // Flush may discover a stale draft; never manufacture a fresh revision to bypass that guard.
            if (buffer.Dirty || buffer.Clearing) throw new AttachmentException("cleared");
            SetStatus("正在確認對方版本並傳送附件通知…");
            var row = await service.SendAsync(principal, sent.Text, sent.Epoch, sent.Revision, _peerPort);
            var fresh = await runtime.Worker.Run(s => s.GetClassroomDraftState(principal));
            var update = buffer.ApplyOwnAckClear(sent, fresh);
            if (update.PreserveEdit is { } edited)
            {
                var result = await runtime.Worker.Run(s => s.SaveClassroomDraft(principal, edited.Text, edited.Epoch, edited.Revision));
                buffer.CompleteWrite(edited, result);
            }
            if (_selected?.ConversationId == principal.Value) SetDraftText(buffer.Text);
            SetStatus(row.StateText + "；對方收完並校驗後才會顯示附件已送達。");
        }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
        finally { SetSending(false); await RefreshAttachmentDraftAsync(); await RefreshInboxAsync(true); }
    }
    private async Task AttachmentActionAsync(AttachmentRecord row, string action)
    {
        if (_runtime?.Attachments is not { } service) return;
        try
        {
            if (action == "cancel") { _runtime.Outbox?.Cancel(row.MessageId); _runtime.Outbox?.Cancel("download:" + row.LocalSeq); await service.CancelAsync(row.LocalSeq); SetStatus("正在取消附件傳輸…"); return; }
            if (action == "download") { await _runtime.Worker.Run(_ => { _runtime.RegisterAttachmentDownload(row); return true; }); }
            else if (action == "retry") await _runtime.Worker.Run(_ => _runtime.RetryAttachment(row));
            else if (action == "save")
            {
                if (!service.Vault.Exists(row.File)) throw new AttachmentException("unavailable");
                var picker = new Microsoft.Win32.SaveFileDialog { FileName = row.File.Name, Title = "另存附件", Filter = "所有檔案|*.*", OverwritePrompt = true };
                if (picker.ShowDialog(this) == true)
                {
                    string source = service.Vault.PathFor(row.File.BlobId), destination = Path.GetFullPath(picker.FileName);
                    if (destination.StartsWith(Path.GetDirectoryName(source)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new AttachmentException("unsafe_path");
                    await Task.Run(async () => {
                        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                        try {
                            File.Copy(source, temporary, overwrite: false);
                            await using (var input = File.OpenRead(temporary))
                                if (input.Length != row.File.Size || Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(input)) != row.File.Sha256) throw new AttachmentException("hash_mismatch");
                            File.Move(temporary, destination, overwrite: true);
                        } finally { File.Delete(temporary); }
                    }); SetStatus("附件已另存；未自動開啟。");
                }
            }
        }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
        finally { await RefreshInboxAsync(true); }
    }
    private async void ClearAttachmentCacheClick(object sender, RoutedEventArgs e)
    {
        if (_runtime?.Attachments is not { } service || MessageBox.Show(this, "清除 App 保管的附件副本？聊天紀錄和已另存的檔案保留；對方可能無法再下載你先前的附件。", "清理附件空間", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await service.ClearCacheAsync(); SetStatus("本機附件副本已清理，歷史紀錄保留。"); await RefreshInboxAsync(true); }
        catch (Exception error) { _runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error); SetStatus(AttachmentException.Describe(error) + "（" + DiagnosticFault.FromException(error).DisplayCode + "）"); }
    }
    private sealed class BoundedPngStream : MemoryStream
    {
        private void Check(int count) { if (Position + count > AttachmentLimits.MaximumFileBytes) throw new AttachmentException("too_large"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void SetLength(long value) { if (value > AttachmentLimits.MaximumFileBytes) throw new AttachmentException("too_large"); base.SetLength(value); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}

internal static class AttachmentImage
{
    internal static bool ValidDimensions(int width, int height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= 32_000_000;
    internal static ImageSource? Load(string path, string name)
    {
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(name).ToLowerInvariant())) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0 || !ValidDimensions(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight)) return null;
            stream.Position = 0; var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = Math.Min(320, decoder.Frames[0].PixelWidth); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { return null; }
    }
}
