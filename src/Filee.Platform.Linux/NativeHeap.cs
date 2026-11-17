// glibc's malloc, which Skia, FreeType, HarfBuzz, the runtime and the engines' libraries all share (issue #957). It keeps
// what they free for the next allocation and only gives back the end of its main heap, and it starts a new arena for
// threads that meet in malloc, up to 8 per CPU. Windows has MemoryTrimmer's working set trim for this; here the memory
// has to be asked for.
//
// Looked up by name while running: musl has no malloc_trim and no libc.so.6, and mallinfo2 needs glibc 2.33. What's missing
// is simply not done. "libc" alone won't do: on a system with development packages it finds libc.so, a linker script.

using System.Runtime.InteropServices;

namespace Filee.Platform.Linux;

/// <summary>What malloc holds: handed out to native code, and freed but kept for the next allocation.</summary>
public readonly record struct NativeHeapUsage(long InUseBytes, long FreeBytes);

public static class NativeHeap
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MallocTrim(nuint pad);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MallInfo2 MallInfo2Call();

    /// <summary>struct mallinfo2: only the fields used are named, but all of them take their place.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MallInfo2
    {
        public nuint Arena, Ordblks, Smblks, Hblks, Hblkhd, Usmblks, Fsmblks, Uordblks, Fordblks, Keepcost;
    }

    private static readonly Lazy<nint> Libc = new(() =>
        OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libc.so.6", out var handle) ? handle : 0);

    private static T? Bind<T>(string name) where T : Delegate =>
        Libc.Value != 0 && NativeLibrary.TryGetExport(Libc.Value, name, out var export)
            ? Marshal.GetDelegateForFunctionPointer<T>(export)
            : null;

    /// <summary>Returns malloc's free memory to the system, in every arena. False when there was none, or no glibc.</summary>
    public static bool Trim() => Bind<MallocTrim>("malloc_trim")?.Invoke(0) == 1;

    /// <summary>What malloc holds right now; null when this C library can't say.</summary>
    public static NativeHeapUsage? Usage()
    {
        if (Bind<MallInfo2Call>("mallinfo2") is not { } call)
            return null;
        var info = call();
        return new((long)info.Uordblks + (long)info.Hblkhd, (long)info.Fordblks);
    }
}
