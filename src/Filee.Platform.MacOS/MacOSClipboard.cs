// The general pasteboard (the clipboard) for the clipboard shortcut (TriggerGesture.Clipboard): files copied in Finder,
// or a picture (a screenshot taken with Ctrl held), come in; the converted files go back, ready to paste.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Platform;

namespace Filee.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal static class MacOSClipboard
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    /// <summary>
    /// The largest picture also put on the clipboard as itself, beside the file; a larger one goes as the file only,
    /// so the clipboard doesn't hold a copy of a huge file in memory.
    /// </summary>
    private const long PictureLimit = 64L << 20;

    private static readonly nint AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    /// <summary>
    /// The existing files copied to the clipboard, else its picture in the first of
    /// <see cref="PasteboardPaths.PictureTypes"/> it has; null for neither. Copied files that are gone give null, not
    /// the icon Finder puts beside them.
    /// </summary>
    internal static ClipboardContent? Read()
    {
        GC.KeepAlive(AppKit);
        using var pool = new AutoreleasePool();
        var pasteboard = GeneralPasteboard();
        if (Available(pasteboard, [PasteboardPaths.FileUrlType, PasteboardPaths.FilenamesType]) is not null)
        {
            var files = PasteboardPaths.FirstExisting(
            [
                () => MacOSServicesProvider.FileUrls(pasteboard),
                () => MacOSServicesProvider.Strings(Send(pasteboard, sel_registerName("propertyListForType:"), NSString(PasteboardPaths.FilenamesType))),
            ], PasteboardPaths.Exists);
            return files.Count > 0 ? ClipboardContent.OfFiles(files) : null;
        }

        if (Available(pasteboard, [.. PasteboardPaths.PictureTypes.Select(t => t.Type)]) is not { } type)
            return null;
        var bytes = Bytes(Send(pasteboard, sel_registerName("dataForType:"), NSString(type)));
        var extension = PasteboardPaths.PictureTypes.First(t => t.Type == type).Extension;
        return bytes is { Length: > 0 } ? ClipboardContent.OfPicture(bytes, extension) : null;
    }

    /// <summary>
    /// Replaces the clipboard with <paramref name="paths"/>, one item per file (its file URL); a single picture also
    /// carries its bytes under its own type, for apps that paste pictures but not files.
    /// </summary>
    internal static bool Write(IReadOnlyList<string> paths)
    {
        GC.KeepAlive(AppKit);
        if (paths.Count == 0)
            return false;
        using var pool = new AutoreleasePool();
        var items = Send(objc_getClass("NSMutableArray"), sel_registerName("array"));
        var fileUrlType = NSString(PasteboardPaths.FileUrlType);
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            var item = Send(Send(objc_getClass("NSPasteboardItem"), sel_registerName("alloc")), sel_registerName("init"));
            var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), NSString(path));
            SendBool(item, sel_registerName("setString:forType:"), Send(url, sel_registerName("absoluteString")), fileUrlType);
            if (paths.Count == 1 && PasteboardPaths.PictureTypeOf(path) is { } type && new FileInfo(path).Length <= PictureLimit)
            {
                var data = Send(objc_getClass("NSData"), sel_registerName("dataWithContentsOfFile:"), NSString(path));
                if (data != 0)
                    SendBool(item, sel_registerName("setData:forType:"), data, NSString(type));
            }
            SendVoid(items, sel_registerName("addObject:"), item);
            SendVoid(item, sel_registerName("release"));
        }
        var pasteboard = GeneralPasteboard();
        Send(pasteboard, sel_registerName("clearContents"));
        return SendBool(pasteboard, sel_registerName("writeObjects:"), items);
    }

    private static nint GeneralPasteboard() => Send(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));

    /// <summary>The first of <paramref name="types"/> the pasteboard has, or null.</summary>
    private static string? Available(nint pasteboard, IReadOnlyList<string> types)
    {
        var array = Send(objc_getClass("NSMutableArray"), sel_registerName("array"));
        foreach (var type in types)
            SendVoid(array, sel_registerName("addObject:"), NSString(type));
        var found = Send(pasteboard, sel_registerName("availableTypeFromArray:"), array);
        return found == 0 ? null : Marshal.PtrToStringUTF8(Send(found, sel_registerName("UTF8String")));
    }

    /// <summary>A copy of an NSData's bytes; null for nil.</summary>
    private static byte[]? Bytes(nint data)
    {
        if (data == 0)
            return null;
        var length = (int)Math.Min(Send(data, sel_registerName("length")), int.MaxValue);
        var bytes = new byte[length];
        if (length > 0)
            Marshal.Copy(Send(data, sel_registerName("bytes")), bytes, 0, length);
        return bytes;
    }

    /// <summary>An autoreleased NSString, released with the surrounding <see cref="AutoreleasePool"/>.</summary>
    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        public AutoreleasePool() { }
        public void Dispose() => SendVoid(_pool, sel_registerName("drain"));
    }

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint first, nint second);
}
