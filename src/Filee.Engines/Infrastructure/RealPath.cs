// The path a file or folder really has on Windows, with every junction, symbolic link and mount point on the way
// followed (#527). The Windows Installer service runs as SYSTEM and refuses a path through a link a normal user
// made ("untrusted mount point", error 448), so msiexec gets the real path instead.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Filee.Engines.Infrastructure;

internal static class RealPath
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // read, write, delete
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // also opens folders

    /// <summary>
    /// The final path of an existing file or folder (<c>GetFinalPathNameByHandle</c>), as a plain drive or UNC
    /// path; <paramref name="path"/> itself when it can't be resolved.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string Of(string path)
    {
        using var handle = CreateFileW(path, 0, FileShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
            return path;
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                return path;
            if (length < buffer.Length)
                return WithoutPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length]; // too small: length is the size needed, terminator included
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);
}
