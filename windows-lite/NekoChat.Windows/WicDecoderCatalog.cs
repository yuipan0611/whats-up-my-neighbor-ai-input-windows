using System.Runtime.InteropServices;
using NekoChat.Core.Attachments;
using NekoChat.Core.Diagnostics;

namespace NekoChat.Windows;

internal sealed record WicDecoderRegistration(AttachmentDecoderRegistration State, Guid ContainerFormat,
    Guid? DecoderClsid = null, string? QueryError = null);

/// Queries WIC by container without opening any image; no OS-edition or exception-message guesses.
internal static class WicDecoderCatalog
{
    // https://learn.microsoft.com/windows/win32/api/wincodec/nf-wincodec-iwicimagingfactory-createdecoder
    internal static WicDecoderRegistration Probe(AttachmentImageFormat format)
    {
        Guid container = AttachmentDecoderPolicy.ContainerFormat(format);
        if (container == Guid.Empty) return new(AttachmentDecoderRegistration.Unknown, container);
        object? factoryObject = null, infoObject = null; IWicDecoder? decoder = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new("cacaf262-9370-4615-a13b-9f5539da4c0a"), throwOnError: true)!;
            factoryObject = Activator.CreateInstance(type) ?? throw new InvalidOperationException("WIC factory unavailable");
            int result = ((IWicFactory)factoryObject).CreateDecoder(ref container, IntPtr.Zero, out decoder);
            var state = AttachmentDecoderPolicy.FromCreateResult(result);
            if (state != AttachmentDecoderRegistration.Registered)
                return new(state, container, QueryError: unchecked((uint)result).ToString("X8"));
            if (decoder is null) throw new InvalidOperationException("WIC returned no decoder");
            Marshal.ThrowExceptionForHR(decoder.GetContainerFormat(out var actual));
            if (actual != container) throw new InvalidOperationException("WIC container mismatch");
            Marshal.ThrowExceptionForHR(decoder.GetDecoderInfo(out infoObject));
            var info = (IWicComponentInfo)infoObject;
            Marshal.ThrowExceptionForHR(info.GetComponentType(out uint componentType));
            if (componentType != 1) throw new InvalidOperationException("WIC component is not a decoder");
            Marshal.ThrowExceptionForHR(info.GetCLSID(out var clsid));
            return new(AttachmentDecoderRegistration.Registered, container, clsid);
        }
        catch (Exception error) when (DiagnosticFault.Catchable(error))
        {
            // Factory/inspection failures are unknown, never proof that a format is unregistered.
            return new(AttachmentDecoderRegistration.Unknown, container, QueryError: unchecked((uint)error.HResult).ToString("X8"));
        }
        finally
        {
            if (infoObject is not null && !ReferenceEquals(infoObject, decoder)) Marshal.FinalReleaseComObject(infoObject);
            if (decoder is not null && !ReferenceEquals(decoder, factoryObject)) Marshal.FinalReleaseComObject(decoder);
            if (factoryObject is not null) Marshal.FinalReleaseComObject(factoryObject);
        }
    }

    // Exact used vtable prefixes from Microsoft Windows SDK wincodec.h. Later methods are not called.
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, uint options, out IWicDecoder decoder);
        [PreserveSig] int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, uint options, out IWicDecoder decoder);
        [PreserveSig] int CreateDecoderFromFileHandle(UIntPtr handle, IntPtr vendor, uint options, out IWicDecoder decoder);
        [PreserveSig] int CreateComponentInfo(ref Guid clsid, out IWicComponentInfo info);
        [PreserveSig] int CreateDecoder(ref Guid container, IntPtr vendor, out IWicDecoder? decoder);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicDecoder
    {
        [PreserveSig] int QueryCapability(IntPtr stream, out uint capability);
        [PreserveSig] int Initialize(IntPtr stream, uint options);
        [PreserveSig] int GetContainerFormat(out Guid container);
        [PreserveSig] int GetDecoderInfo([MarshalAs(UnmanagedType.IUnknown)] out object info);
    }

    [ComImport, Guid("23bc3f0a-698b-4357-886b-f24d50671334"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicComponentInfo
    {
        [PreserveSig] int GetComponentType(out uint type);
        [PreserveSig] int GetCLSID(out Guid clsid);
    }
}
