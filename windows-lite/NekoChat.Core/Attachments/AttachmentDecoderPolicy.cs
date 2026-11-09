namespace NekoChat.Core.Attachments;

public enum AttachmentDecoderRegistration { Unknown, Registered, NotRegistered, InitializeFailed }
public enum AttachmentDecoderFailure { DecodeFailed, DecoderUnavailable }

/// Registration is queried without image bytes. A file error cannot establish codec absence.
public static class AttachmentDecoderPolicy
{
    public static AttachmentDecoderRegistration FromCreateResult(int hresult) => hresult >= 0
        ? AttachmentDecoderRegistration.Registered
        : unchecked((uint)hresult) switch {
            0x88982F50 or 0x80040154 => AttachmentDecoderRegistration.NotRegistered,
            // WINCODEC_ERR_COMPONENTINITIALIZEFAILURE, Windows SDK shared/winerror.h.
            0x88982F8B => AttachmentDecoderRegistration.InitializeFailed,
            _ => AttachmentDecoderRegistration.Unknown
        };

    public static AttachmentDecoderFailure Failure(AttachmentDecoderRegistration registration) =>
        registration is AttachmentDecoderRegistration.NotRegistered or AttachmentDecoderRegistration.InitializeFailed
            ? AttachmentDecoderFailure.DecoderUnavailable : AttachmentDecoderFailure.DecodeFailed;

    // Microsoft Windows SDK wincodec.h GUID_ContainerFormat*, not filename extensions or codec brands.
    public static Guid ContainerFormat(AttachmentImageFormat format) => format switch {
        AttachmentImageFormat.Png => new("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),
        AttachmentImageFormat.Jpeg => new("19e4a5aa-5662-4fc5-a0c0-1758028e1057"),
        AttachmentImageFormat.Gif => new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5"),
        AttachmentImageFormat.WebP => new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3"),
        AttachmentImageFormat.Tiff => new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3"),
        AttachmentImageFormat.Ico => new("a3a860c4-338f-4c17-919a-fba4b5628f21"),
        AttachmentImageFormat.Bmp => new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3"),
        AttachmentImageFormat.Heif => new("e1e62521-6787-405b-a339-500715b5763f"),
        _ => Guid.Empty
    };
}
