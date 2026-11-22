// A file's flags as macOS keeps them (st_flags), read with stat without opening the file. Cut a clip's list of marked
// frames uses it to leave alone a cloud file (iCloud Drive, Dropbox, OneDrive, Google Drive) that is online-only:
// opening one downloads it. .NET shows no such mark on macOS; Windows has its recall attributes for the same.

using System.Runtime.InteropServices;

namespace Filee.App.Services;

internal static class MacFileFlags
{
    /// <summary>SF_DATALESS (sys/stat.h): the file's data isn't on the disk; reading it fetches it.</summary>
    public const uint Dataless = 0x40000000;

    /// <summary>UF_HIDDEN (sys/stat.h): hidden in Finder, as <c>chflags hidden</c> sets it.</summary>
    public const uint Hidden = 0x00008000;

    // struct stat with 64-bit inodes, the same on Apple silicon and Intel: st_size at 96, st_flags at 116, 144 bytes.
    private const int SizeOffset = 96;
    private const int FlagsOffset = 116;
    private const int StatSize = 144;

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatArm(string path, byte[] buffer);

    // On Intel the plain "stat" keeps the old 32-bit inode layout; the 64-bit one has this name.
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatIntel(string path, byte[] buffer);

    /// <summary>The file's flags and size, or null off macOS or when it can't be read.</summary>
    internal static (uint Flags, long Size)? Read(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var buffer = new byte[StatSize + 112]; // room to spare should the struct ever grow
        try
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? StatIntel(path, buffer) : StatArm(path, buffer);
            return result == 0 ? (BitConverter.ToUInt32(buffer, FlagsOffset), BitConverter.ToInt64(buffer, SizeOffset)) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file is online-only on macOS: opening it would download it.</summary>
    public static bool IsDataless(string path) => Read(path) is { } stat && (stat.Flags & Dataless) != 0;
}
