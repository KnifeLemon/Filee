// Deleting from an elevated Setup in folders a standard user can change (Filee 1.1's %LocalAppData%\Filee, the
// settings in %AppData%). The user could put a junction or symbolic link there, or swap a folder for one while Setup
// runs, so that Setup deletes files somewhere else with admin rights. So a link is removed itself and never followed,
// and every entry is opened first (without following a link) and deleted through that handle only once its real path
// is inside the folder being deleted. Checking a path and then deleting by path would leave a gap to swap a folder in.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Filee.Setup.Installation;

[SupportedOSPlatform("windows")]
internal static partial class LinkSafeDelete
{
    public enum Outcome
    {
        Deleted,
        /// <summary>Not there (any more).</summary>
        Gone,
        /// <summary>Open in a program, read-only to us, or a folder that isn't empty.</summary>
        Failed,
        /// <summary>Its real path is outside the folder being deleted: a folder on the way was swapped for a link.</summary>
        Outside,
    }

    private const uint Delete = 0x00010000;
    private const uint ReadAttributes = 0x0080;
    private const uint WriteAttributes = 0x0100;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // also opens folders
    private const uint OpenReparsePoint = 0x00200000; // the link itself, not what it points to
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2; // the name goes when the handle closes, even if others have it open
    private const uint DispositionIgnoreReadOnly = 0x10;
    private const uint AttributeReadOnly = 0x1;
    private const uint AttributeNormal = 0x80;

    /// <summary>
    /// The real path of <paramref name="folder"/>, or null when it isn't there or is reached through a junction or
    /// link (it or a folder above it): then nothing in it is deleted.
    /// </summary>
    public static string? RealFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        using var handle = CreateFileW(full, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid || FinalPath(handle) is not { } real)
            return null;
        // The long form, as the final path has it (a temp folder can be given as C:\Users\RUNNER~1\...).
        return string.Equals(real, LongPath(full), StringComparison.OrdinalIgnoreCase) ? real : null;
    }

    /// <summary>
    /// Deletes the file, empty folder or link at <paramref name="path"/> if it really is inside
    /// <paramref name="realRoot"/> (from <see cref="RealFolder"/>). A link is deleted itself.
    /// </summary>
    public static Outcome Entry(string path, string realRoot)
    {
        using var handle = CreateFileW(path, Delete | ReadAttributes | WriteAttributes, ShareAll, 0, OpenExisting,
            BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
            return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound ? Outcome.Gone : Outcome.Failed;
        if (FinalPath(handle) is not { } real || !real.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Outcome.Outside;
        return MarkForDeletion(handle) ? Outcome.Deleted : Outcome.Failed;
    }

    private static unsafe bool MarkForDeletion(SafeFileHandle handle)
    {
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, &flags, sizeof(uint)))
            return true;
        // A volume without POSIX deletes (FAT): the older call, which refuses a read-only file until that is cleared.
        byte delete = 1;
        if (SetFileInformationByHandle(handle, FileDispositionInfo, &delete, sizeof(byte)))
            return true;
        BasicInfo basic;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, &basic, (uint)sizeof(BasicInfo))
            || (basic.Attributes & AttributeReadOnly) == 0)
            return false;
        var writable = new BasicInfo { Attributes = basic.Attributes & ~AttributeReadOnly };
        if (writable.Attributes == 0)
            writable.Attributes = AttributeNormal;
        return SetFileInformationByHandle(handle, FileBasicInfo, &writable, (uint)sizeof(BasicInfo))
            && SetFileInformationByHandle(handle, FileDispositionInfo, &delete, sizeof(byte));
    }

    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
                length = GetFinalPathNameByHandleW(handle, chars, (uint)buffer.Length, 0);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return WithoutPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length]; // too small: length is the size needed, terminator included
        }
    }

    private static unsafe string LongPath(string path)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
                length = GetLongPathNameW(path, chars, (uint)buffer.Length);
            if (length == 0)
                return path;
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[length];
        }
    }

    /// <summary><c>\\?\C:\x</c> → <c>C:\x</c>, <c>\\?\UNC\server\share</c> → <c>\\server\share</c>.</summary>
    internal static string WithoutPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, char* buffer, uint size, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathNameW(string path, char* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* info, uint size);
}
