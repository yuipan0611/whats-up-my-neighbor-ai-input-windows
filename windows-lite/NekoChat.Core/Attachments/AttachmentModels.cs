using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Attachments;

public static class AttachmentLimits
{
    public const long MaximumFileBytes = 25L * 1024 * 1024;
    public const long AutomaticBytes = 5L * 1024 * 1024;
    public const long StorageBytes = 512L * 1024 * 1024;
    public const int ChunkBytes = 256 * 1024;
    public const string Capability = "attachments.v1";
    public static bool Automatic(bool onRoster, long size) => onRoster && size <= AutomaticBytes;
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 180 &&
        name is not "." and not ".." && !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) &&
        !name.EndsWith('.') && !name.EndsWith(' ') && Encoding.UTF8.GetByteCount(name) <= 512;
    public static bool IsId(string? id) => Guid.TryParseExact(id, "N", out var parsed) && parsed.ToString("N") == id;
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

public sealed record AttachmentFile(string BlobId, string Name, long Size, string Sha256);
public sealed record AttachmentDraft(AttachmentFile File, long Epoch);
public sealed record AttachmentRecord(long LocalSeq, string Principal, string Direction, string MessageId, AttachmentFile File,
    string Caption, string PeerIp, int PeerPort, string Token, long Epoch, string State, string? Problem, string? CompletedUtc = null)
{
    public override string ToString() => $"Attachment({MessageId},{Direction},{State})";
    public string StateText => State switch {
        "sending" => "傳送附件通知中", "offered" => Direction == "outgoing" ? "通知已送出 · 等待收件確認" : "等待下載",
        "receiving" => "下載中", "complete" => Direction == "outgoing" ? "附件已送達" : "已接收",
        "cancelled" => "已取消；可手動重試", "unconfirmed" => "結果未確認；可手動重試",
        "unavailable" => CompletedUtc is not null ? (Direction == "outgoing" ? "附件已送達 · 本機副本不可用" : "已接收 · 本機副本不可用") : "檔案不可用", _ => Problem switch { "storage_full" => "附件空間已滿，請清理後重試", "hash_mismatch" => "內容校驗失敗，請重新下載", "unsupported_version" => "對方版本不支援附件；草稿已保留", "unavailable" => "對方離線或附件不可用；可手動重試", _ => "失敗；可手動重試" } };
}

public sealed class AttachmentException(string code, Exception? inner = null) : IOException(code, inner)
{
    public string Code { get; } = code;
    public static string Describe(Exception error) => error is AttachmentException attachment ? attachment.Code switch {
        "too_large" => "單一附件不能超過 25 MiB。", "catalog_full" => "課堂檔案目錄已達上限，無法新增分享。", "not_owner" => "只有分享者能撤下這份檔案。", "withdrawn" => "這份分享已撤下。", "storage_full" => "附件空間已滿，請清理本機附件後重試。",
        "unsupported_version" => "對方版本尚不支援附件，請先更新；草稿已保留。",
        "invalid_name" => "檔名含不支援的字元，請重新命名。", "hash_mismatch" => "檔案校驗失敗，請重新下載。",
        "source_changed" => "檔案在準備時改變，請重新選取。", "unavailable" => "對方離線或附件已不可用，請稍後重試。",
        "cleared" => "對話或名單已改變，當次附件操作已中止。", _ => "附件操作失敗，請手動重試。"
    } : error is OperationCanceledException ? "已取消附件操作；不會自動重傳。" : "附件無法讀取或儲存，請手動重試。";
}
