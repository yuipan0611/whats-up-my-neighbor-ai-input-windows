using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core.Attachments;
using NekoChat.Core.Diagnostics;

namespace NekoChat.Windows;

internal enum AttachmentPreviewStatus { NotEligible, NotImage, InvalidHeader, TooLarge, DimensionsExceeded, VerificationFailed, DecoderUnavailable, DecodeFailed, Ready, Cancelled }
internal sealed record AttachmentPreviewResult(BitmapSource? Image, string Notice, AttachmentImageFormat Format,
    AttachmentPreviewStatus Status, bool HashVerified = false, bool DecodeAttempted = false, int WorkerThreadId = 0, string? DecoderError = null, WicDecoderRegistration? DecoderRegistration = null);

internal static class AttachmentImage
{
    private static readonly SemaphoreSlim DecodeGate = new(1);
    internal static bool ValidDimensions(int width, int height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= 32_000_000;
    internal static Task<AttachmentPreviewResult> LoadCompletedAsync(AttachmentRecord row, string? path, CancellationToken cancellation = default) =>
        LoadAsync(row.File, path, row.State == "complete", cancellation);
    // Drafts are local staged blobs with their own SHA-256, not partially received attachments.
    internal static Task<AttachmentPreviewResult> LoadDraftAsync(AttachmentDraft draft, string path, CancellationToken cancellation = default) =>
        LoadAsync(draft.File, path, true, cancellation);

    private static async Task<AttachmentPreviewResult> LoadAsync(AttachmentFile file, string? path, bool verifiedState, CancellationToken cancellation)
    {
        if (!verifiedState) return Result(AttachmentPreviewStatus.NotEligible, AttachmentImageFormat.Unknown);
        if (path is null) return Result(AttachmentPreviewStatus.VerificationFailed, AttachmentImageFormat.Unknown, file.Name);
        if (!AttachmentImageProbe.Eligible("complete", true, file.Size))
            return Result(file.Size > AttachmentLimits.MaximumFileBytes ? AttachmentPreviewStatus.TooLarge :
                AttachmentPreviewStatus.InvalidHeader,
                AttachmentImageFormat.Unknown, file.Name);
        bool entered = false;
        try
        {
            await DecodeGate.WaitAsync(cancellation).ConfigureAwait(false); entered = true;
            return await Task.Run(() => Decode(file, path, cancellation), cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Result(AttachmentPreviewStatus.Cancelled, AttachmentImageFormat.Unknown); }
        finally { if (entered) DecodeGate.Release(); }
    }

    private static AttachmentPreviewResult Decode(AttachmentFile file, string path, CancellationToken cancellation)
    {
        AttachmentImageFormat format = AttachmentImageFormat.Unknown; bool hashed = false, attempted = false;
        int worker = Environment.CurrentManagedThreadId; WicDecoderRegistration? registration = null;
        AttachmentPreviewResult Failure(AttachmentPreviewStatus status) => Result(status, format, file.Name, hashed, attempted, worker)
            with { DecoderRegistration = registration };
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Deny writers, permit cache deletion. Decode and rehash share this same open handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > AttachmentLimits.MaximumFileBytes) return Failure(AttachmentPreviewStatus.TooLarge);
            if (stream.Length != file.Size || !AttachmentLimits.IsHash(file.Sha256)) return Failure(AttachmentPreviewStatus.VerificationFailed);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256) return Failure(AttachmentPreviewStatus.VerificationFailed);
            hashed = true; cancellation.ThrowIfCancellationRequested(); stream.Position = 0;
            byte[] header = new byte[(int)Math.Min(stream.Length, AttachmentImageProbe.MaximumHeaderBytes)];
            stream.ReadExactly(header);
            var probe = AttachmentImageProbe.Inspect(header, stream.Length); format = probe.Format;
            if (!probe.CanDecode) return Failure(probe.Status switch {
                AttachmentImageHeaderStatus.TooLarge => AttachmentPreviewStatus.TooLarge,
                AttachmentImageHeaderStatus.DimensionsExceeded => AttachmentPreviewStatus.DimensionsExceeded,
                AttachmentImageHeaderStatus.NotImage => AttachmentPreviewStatus.NotImage,
                _ => AttachmentPreviewStatus.InvalidHeader
            });
            registration = WicDecoderCatalog.Probe(format); // Content-independent registration evidence, on this worker.
            if (registration.State != AttachmentDecoderRegistration.Registered)
                return Failure(AttachmentDecoderPolicy.Failure(registration.State) == AttachmentDecoderFailure.DecoderUnavailable
                    ? AttachmentPreviewStatus.DecoderUnavailable : AttachmentPreviewStatus.DecodeFailed);
            stream.Position = 0; cancellation.ThrowIfCancellationRequested(); attempted = true;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return Failure(AttachmentPreviewStatus.DecodeFailed);
            var first = decoder.Frames[0]; // Never enumerate/materialize further pages or animation frames.
            if (!ValidDimensions(first.PixelWidth, first.PixelHeight)) return Failure(AttachmentPreviewStatus.DimensionsExceeded);
            double scale = Math.Min(1, Math.Min(320d / first.PixelWidth, 200d / first.PixelHeight));
            BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(first, new ScaleTransform(scale, scale)) : first;
            var pixels = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
            int stride = checked(pixels.PixelWidth * 4);
            byte[] buffer = new byte[checked(stride * pixels.PixelHeight)];
            pixels.CopyPixels(buffer, stride, 0);
            // Detach pixels from the decoder/stream; only this small frozen bitmap crosses to UI.
            var detached = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            detached.Freeze(); cancellation.ThrowIfCancellationRequested();
            return new(detached, "", format, AttachmentPreviewStatus.Ready, true, true, worker, DecoderRegistration: registration);
        }
        catch (OperationCanceledException) { return Failure(AttachmentPreviewStatus.Cancelled); }
        catch (Exception error) when (DiagnosticFault.Catchable(error) || error is OutOfMemoryException)
        {
            return Failure(AttachmentPreviewStatus.DecodeFailed)
                with { DecoderError = unchecked((uint)error.HResult).ToString("X8") };
        }
    }

    private static AttachmentPreviewResult Result(AttachmentPreviewStatus status, AttachmentImageFormat format, string name = "",
        bool hash = false, bool attempted = false, int worker = 0)
    {
        const string fallback = "這台電腦無法預覽此圖片格式（可另存後用看圖程式開啟）。";
        string notice = status switch {
            AttachmentPreviewStatus.NotEligible or AttachmentPreviewStatus.Cancelled or AttachmentPreviewStatus.Ready => "",
            AttachmentPreviewStatus.NotImage => AttachmentImageProbe.ImageName(name) ? "檔案內容不是可辨識的圖片，無法預覽（可另存後用看圖程式開啟）。" : "",
            AttachmentPreviewStatus.TooLarge => "圖片超過 25 MiB，無法建立縮圖。",
            AttachmentPreviewStatus.DimensionsExceeded => "圖片尺寸超過預覽上限（可另存後用看圖程式開啟）。",
            AttachmentPreviewStatus.VerificationFailed => "無法驗證檔案，未產生預覽；請重新下載。",
            AttachmentPreviewStatus.DecoderUnavailable when format == AttachmentImageFormat.Heif => "需要 Windows 的 HEIF 影像延伸模組，這台電腦目前無法預覽（可另存後用看圖程式開啟）。",
            _ => fallback
        };
        return new(null, notice, format, status, hash, attempted, worker);
    }
}

/// Owns one visible card's asynchronous preview; discarded/rebuilt cards cannot publish stale images.
internal sealed class AttachmentPreviewView : StackPanel
{
    private readonly AttachmentRecord row; private readonly string? path;
    private CancellationTokenSource? pending;
    internal readonly Image Thumbnail = new() { MaxWidth = 300, MaxHeight = 200, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    internal readonly TextBlock Hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(89, 107, 128)), Margin = new(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    internal AttachmentPreviewResult? Result { get; private set; }

    internal AttachmentPreviewView(AttachmentRecord row, string? path)
    {
        this.row = row; this.path = path;
        Children.Add(Thumbnail); Children.Add(Hint);
        Loaded += async (_, _) => {
            pending?.Cancel(); pending?.Dispose(); var current = new CancellationTokenSource(); pending = current;
            var result = await AttachmentImage.LoadCompletedAsync(row, path, current.Token);
            if (!ReferenceEquals(current, pending) || current.IsCancellationRequested || !IsLoaded) return;
            Result = result; Thumbnail.Source = result.Image;
            Thumbnail.Visibility = result.Image is null ? Visibility.Collapsed : Visibility.Visible;
            Hint.Text = result.Notice; Hint.Visibility = string.IsNullOrEmpty(result.Notice) ? Visibility.Collapsed : Visibility.Visible;
        };
        Unloaded += (_, _) => pending?.Cancel();
    }
}
