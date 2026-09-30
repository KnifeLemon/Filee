// Win32 P/Invoke declarations used by the Windows platform layer. Keep this list minimal.

using System.Runtime.InteropServices;

namespace Filee.Platform.Windows;

internal static partial class NativeMethods
{
    public const int SM_CXDRAG = 68;
    public const uint GA_ROOT = 2;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hwnd, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    // Package queries (appmodel.h, Windows 8+ / PackagePathType_EffectiveExternal: Windows 10 2004+).
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int PackagePathType_EffectiveExternal = 5;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct PACKAGE_ID
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        public char* Name;
        public char* Publisher;
        public char* ResourceId;
        public char* PublisherId;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, char** packageFullNames, ref uint bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetPackagePathByFullName2(string packageFullName, int packagePathType, ref uint pathLength, char* path);

    [LibraryImport("kernel32.dll")]
    public static unsafe partial int PackageFamilyNameFromId(PACKAGE_ID* packageId, ref uint packageFamilyNameLength, char* packageFamilyName);

    /// <summary>Returns the Win32 class name of a window ("CabinetWClass" for Explorer, ...).</summary>
    public static string ClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
}
