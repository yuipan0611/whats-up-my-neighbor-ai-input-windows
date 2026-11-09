using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core.Attachments;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.TestFixtures;

namespace NekoChat.Windows;

public partial class App
{
    private async Task<object[]> RunAttachmentPreviewUiChecksAsync(string output, Action<bool, string> check,
        Action<string, FrameworkElement> shot, Func<Func<bool>, string, Task> wait, CancellationToken cancellation)
    {
        string profile = Path.Combine(output, "attachment-preview-profile-" + Guid.NewGuid().ToString("N"));
        UiTestOptions.AssertSafeDataDirectory(profile);
        var settings = Settings.ForUiTest(profile) with { ClassRosterInitialized = true, ThisDeviceIsTeacher = false, ThisDeviceTeacherExplicitChoice = true };
        check(settings.Save(), "AP isolated settings saved");
        await using var runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
        var service = runtime.Attachments ?? throw new InvalidOperationException("AP fixture vault unavailable");
        const string source = "10.251.235.7";
        await runtime.Worker.Run(store => store.SaveManualRosterEntry(null, new("合成圖片對象", null, false, source),
            RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())));
        var view = new ChatWindow(settings);
        view.InstallContactsUiSmokeHooks(profile, new() { LocalAddresses = ["10.251.235.8"] }, runtime);
        var observations = new List<object>(); int uiThread = Environment.CurrentManagedThreadId;
        try
        {
            await view.AttachRuntimeAsync(runtime, 0); view.Show(); view.Activate();
            await view.SmokeOpenEntryAsync(entry => entry.Endpoint == source);
            // Query every format independently of decoding so later formats are recorded even if one fails.
            var inventory = await Task.Run(() => AttachmentPreviewFixtures.Names.ToDictionary(extension => extension,
                extension => {
                    byte[] bytes = AttachmentPreviewFixtures.Read(extension);
                    return WicDecoderCatalog.Probe(AttachmentImageProbe.Inspect(bytes, bytes.Length).Format);
                }), cancellation);
            await File.WriteAllTextAsync(Path.Combine(output, "attachment-decoder-inventory.json"),
                System.Text.Json.JsonSerializer.Serialize(inventory.Select(item => new { format = item.Key,
                    registration = item.Value.State.ToString(), item.Value.ContainerFormat, item.Value.DecoderClsid, item.Value.QueryError }),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellation);
            async Task<(AttachmentRecord Row, AttachmentPreviewView Preview)> Card(byte[] bytes, string name, string state, bool tamper = false, bool missing = false)
            {
                await using var input = new MemoryStream(bytes, writable: false);
                var file = await service.Vault.StageAsync(input, name, bytes.Length, cancellation); // Actual staged hash, not a fabricated hash.
                var offered = await runtime.Worker.Run(store => {
                    var actual = (ChatStore)store;
                    return actual.ReceiveAttachmentOffer(source, 8768, Guid.NewGuid().ToString("D"), file, "", new string('b', 64), () => true).Record
                        ?? throw new InvalidOperationException("AP synthetic offer was rejected");
                });
                // ReceiveAttachmentOffer intentionally replaces the wire blob id with a local id.
                // Follow DownloadAsync's reserve/commit path before seeding the transfer state.
                check(offered.File.BlobId != file.BlobId, "AP receiver owns a distinct local blob id: " + name);
                await using (var target = service.Vault.Reserve(offered.File.BlobId, offered.File.Size))
                    await target.WriteAsync(bytes, cancellation);
                await service.Vault.CommitAsync(offered.File, cancellation);
                service.Vault.Delete(file.BlobId);
                if (tamper)
                {
                    byte[] changed = (byte[])bytes.Clone(); changed[^1] ^= 1;
                    await File.WriteAllBytesAsync(service.Vault.PathFor(offered.File.BlobId), changed, cancellation);
                }
                if (missing) service.Vault.Delete(offered.File.BlobId);
                var row = await runtime.Worker.Run(store => {
                    var actual = (ChatStore)store;
                    if (!actual.SetAttachmentState(offered, state)) throw new InvalidOperationException("AP synthetic transfer state was rejected");
                    return actual.GetAttachment(offered.LocalSeq)!;
                });
                await view.SmokeRefreshContactsAsync();
                var message = view.History.Rows.Single(message => message.Sequence == row.LocalSeq && message.Attachment is not null);
                check(message.Attachment!.State == state && (missing ? message.AttachmentPath is null :
                    message.AttachmentPath == service.Vault.PathFor(row.File.BlobId)), "AP projected state/path match the receiver local blob: " + name);
                view.History.ScrollIntoView(message); view.UpdateLayout();
                AttachmentPreviewView? preview = null;
                await wait(() => {
                    view.UpdateLayout();
                    var cards = SmokeVisuals<MessageCard>(view.History).Where(card => ReferenceEquals(card.Message, message)).ToArray();
                    if (cards.Length != 1) return false;
                    preview = SmokeVisuals<AttachmentPreviewView>(cards.Single()).SingleOrDefault();
                    return preview?.Result is { Status: not AttachmentPreviewStatus.Cancelled };
                }, "AP actual asynchronous card preview did not settle: " + name);
                return (row, preview!);
            }
            foreach (string extension in AttachmentPreviewFixtures.Names)
            {
                // All names deliberately use .data: content, not extension, must select the codec.
                var item = await Card(AttachmentPreviewFixtures.Read(extension), "synthetic-" + extension + ".data", "complete");
                var result = item.Preview.Result!;
                var registration = inventory[extension];
                observations.Add(new { format = extension, status = result.Status.ToString(),
                    observation = result.Status == AttachmentPreviewStatus.Ready ? "實際縮圖成功" :
                        result.Status == AttachmentPreviewStatus.DecoderUnavailable ? "無解碼器時顯示提示" : "預覽失敗，測試須失敗",
                    decoderRegistered = registration.State == AttachmentDecoderRegistration.Unknown ? (bool?)null :
                        registration.State == AttachmentDecoderRegistration.Registered,
                    registrationState = registration.State.ToString(), registration.ContainerFormat, registration.DecoderClsid,
                    registration.QueryError, productRegistrationState = result.DecoderRegistration?.State.ToString(),
                    productDecoderClsid = result.DecoderRegistration?.DecoderClsid,
                    productRegistrationQueryError = result.DecoderRegistration?.QueryError,
                    result.HashVerified, result.DecodeAttempted, result.WorkerThreadId, result.DecoderError, result.Notice });
                await File.WriteAllTextAsync(Path.Combine(output, "attachment-preview-observations.json"),
                    System.Text.Json.JsonSerializer.Serialize(observations, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellation);
                check(registration.State != AttachmentDecoderRegistration.Unknown &&
                    result.DecoderRegistration is { } productRegistration && productRegistration.State == registration.State &&
                    productRegistration.ContainerFormat == registration.ContainerFormat,
                    "AP-04 independent WIC container registration query is definitive and agrees with product: " + extension +
                    " / " + registration.State + " / " + registration.QueryError);
                if (registration.State == AttachmentDecoderRegistration.NotRegistered)
                {
                    check(result.Status == AttachmentPreviewStatus.DecoderUnavailable && !result.DecodeAttempted &&
                        item.Preview.Hint.IsVisible && !item.Preview.Thumbnail.IsVisible &&
                        (extension != "heic" || item.Preview.Hint.Text.Contains("需要 Windows 的 HEIF 影像延伸模組", StringComparison.Ordinal)),
                        "AP-04 無解碼器時顯示提示: " + extension);
                }
                else
                {
                    check(result.Status == AttachmentPreviewStatus.Ready && result.DecodeAttempted && registration.DecoderClsid is not null &&
                        result.Image is { IsFrozen: true } &&
                        item.Preview.Thumbnail.IsVisible && result.Image.PixelWidth <= 320 && result.Image.PixelHeight <= 200,
                        "AP-01 actual WIC thumbnail rendered: " + extension + " / " + result.Status);
                    var image = new FormatConvertedBitmap(result.Image!, PixelFormats.Bgra32, null, 0);
                    byte[] pixel = new byte[4];
                    image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
                    check(pixel[2] > 150 && pixel[0] < 120, "AP-01 first frame/page is red, never blue animation/page2: " + extension);
                }
                check(result.HashVerified && result.WorkerThreadId != 0 && result.WorkerThreadId != uiThread,
                    "AP-03 hash/content/registration checked off UI; pixel decode only with a registered codec: " + extension);
                shot("attachment-preview-" + extension, view);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "attachment-preview-observations.json"),
                System.Text.Json.JsonSerializer.Serialize(observations, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellation);
            var incomplete = await Card(AttachmentPreviewFixtures.Read("gif"), "not-complete.gif", "receiving");
            check(incomplete.Preview.Result is { Status: AttachmentPreviewStatus.NotEligible, DecodeAttempted: false, HashVerified: false } &&
                !incomplete.Preview.Thumbnail.IsVisible, "AP-05 incomplete attachment with an existing valid blob never opens decoder");
            var missing = await Card(AttachmentPreviewFixtures.Read("png"), "missing.png", "complete", missing: true);
            check(missing.Preview.Result is { Status: AttachmentPreviewStatus.VerificationFailed, DecodeAttempted: false, HashVerified: false } &&
                missing.Preview.Hint.IsVisible && missing.Preview.Hint.Text.Contains("無法驗證檔案", StringComparison.Ordinal),
                "AP-05 completed attachment without its local blob shows verification hint instead of silent NotEligible");
            shot("attachment-preview-missing", view);
            var fake = await Card("%PDF-1.7 not an image"u8.ToArray(), "fake.jpg", "complete");
            check(fake.Preview.Result is { Status: AttachmentPreviewStatus.NotImage, DecodeAttempted: false } && fake.Preview.Hint.IsVisible,
                "AP-02 fake image extension rejected before WIC with visible explanation");
            var broken = AttachmentPreviewFixtures.Read("png")[..20];
            var truncated = await Card(broken, "truncated.png", "complete");
            check(truncated.Preview.Result is { Status: AttachmentPreviewStatus.InvalidHeader, DecodeAttempted: false } && truncated.Preview.Hint.IsVisible,
                "AP-02 truncated image header rejected without crashing");
            var tampered = await Card(AttachmentPreviewFixtures.Read("png"), "tampered.png", "complete", tamper: true);
            check(tampered.Preview.Result is { Status: AttachmentPreviewStatus.VerificationFailed, HashVerified: false, DecodeAttempted: false } &&
                tampered.Preview.Hint.IsVisible, "AP-05 changed completed blob fails SHA-256 before decoder");
            var payload = await Card(AttachmentPreviewFixtures.Read("png")[..40], "bad-payload.png", "complete");
            check(payload.Preview.Result is { DecodeAttempted: true, Status: not AttachmentPreviewStatus.Ready } &&
                payload.Preview.Hint.IsVisible, "AP-04 corrupt image decoder failure is visible and app continues");
            shot("attachment-preview-invalid", view);
            string draftPath = Path.Combine(profile, "draft-animation.gif");
            await File.WriteAllBytesAsync(draftPath, AttachmentPreviewFixtures.Read("gif"), cancellation);
            await view.SmokeStageContactAttachmentAsync(draftPath);
            await wait(() => view.AttachmentDraftImage.Source is not null, "AP-05 actual staged GIF draft did not preview");
            check(view.AttachmentDraftImage.IsVisible && string.IsNullOrEmpty(view.AttachmentDraftPreviewHint.Text),
                "AP-05 sender draft shares the same successful content/size/hash/background rules");
            shot("attachment-preview-draft-gif", view);
        }
        catch
        {
            shot("attachment-preview-failure", view); // Capture this actual window before finally hides it.
            throw;
        }
        finally { await view.PrepareForShutdownAsync(); view.DetachRuntime(); view.Hide(); }
        return observations.ToArray();
    }
}
