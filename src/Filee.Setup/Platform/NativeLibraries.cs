// Where Setup's native code comes from. Setup and the uninstaller run with administrator rights for an install for all
// users, so no library they load may come from a folder the signed-in user can change.
//
// A single-file .NET program normally unpacks its native libraries at start into %TEMP%\.net\<name>\<id> and, on the
// next start, takes whatever files it finds there as its own: any program of the user's could put its own
// libSkiaSharp.dll there first. So the publish carries Skia and HarfBuzz as resources of Filee.Setup.dll instead (see
// Filee.Setup.csproj) and Setup writes them out itself, into a folder made new for this run: with administrator rights
// in %SystemRoot%\Temp with Program.AdminOnlyFolder's rights, where no standard user can change, rename or replace
// anything between writing and loading; without them in the user's %TEMP%, where there are no rights to gain. They are
// loaded right away, by full path. ANGLE (av_libglesv2.dll) isn't published at all: Setup draws in software.
//
// Libraries named without a folder are the other way in: .NET first tries the folder of Setup.exe (for a download,
// Downloads), and Windows then searches the PATH too. Setup only needs Windows' own libraries besides the two above, so
// those come from System32 only. (.NET's own start-up code, before Main, still tries Setup.exe's folder for a few of
// Windows' libraries: that folder must not be one a standard user can change when Setup runs elevated.)

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Filee.Setup.Platform;

[SupportedOSPlatform("windows")]
internal static class NativeLibraries
{
    /// <summary>The name of an unpacked libraries folder, followed by a new GUID.</summary>
    internal const string FolderPrefix = "Filee-Native-";

    /// <summary>The libraries the publish carries, by the name SkiaSharp's and HarfBuzzSharp's P/Invokes use.</summary>
    internal static readonly IReadOnlyList<string> Carried = ["libSkiaSharp", "libHarfBuzzSharp"];

    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x100;
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

    private static readonly Lock Gate = new();
    private static unsafe delegate* unmanaged<char*, nint, uint, nint> _loadLibraryEx;
    private static readonly Dictionary<string, nint> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This run's folder, once <see cref="Unpack"/> has written the libraries.</summary>
    public static string? Folder { get; private set; }

    /// <summary>True in the published Setup, which carries the libraries; false in a build (they sit next to it).</summary>
    public static readonly bool AreCarried = typeof(NativeLibraries).Assembly.GetManifestResourceInfo(ResourceName(Carried[0])) is not null;

    private static string ResourceName(string library) => $"native.{library}.dll";

    /// <summary>
    /// First thing at start, before any P/Invoke of Setup's: Windows looks only in System32 for libraries loaded
    /// without a folder, and .NET asks <see cref="Resolve"/> for every P/Invoke of every assembly (CoreLib's never ask).
    /// </summary>
    public static unsafe void Guard()
    {
        // Through kernel32 by its full path, not P/Invokes: those would themselves be looked up next to Setup.exe
        // first, and the resolver can't use them for the same reason.
        var kernel32 = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        _loadLibraryEx = (delegate* unmanaged<char*, nint, uint, nint>)NativeLibrary.GetExport(kernel32, "LoadLibraryExW");
        var setDefaultDllDirectories = (delegate* unmanaged<uint, int>)NativeLibrary.GetExport(kernel32, "SetDefaultDllDirectories");
        if (setDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) == 0)
            throw new Win32Exception(Marshal.GetLastSystemError());
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Register(e.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            Register(assembly);
    }

    private static unsafe nint LoadLibraryEx(string name, uint flags)
    {
        fixed (char* chars = name)
            return _loadLibraryEx(chars, 0, flags);
    }

    private static void Register(Assembly assembly)
    {
        if (assembly == typeof(object).Assembly)
            return;
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Registered already (the load event and the first list can both see an assembly).
        }
    }

    /// <summary>
    /// A carried library: the one <see cref="Unpack"/> loaded. Another library named without a folder: System32's. A
    /// path is loaded as it is (0: .NET's own search). In the published Setup, nothing else is looked for anywhere.
    /// </summary>
    internal static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var library = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (Carried.Contains(library, StringComparer.OrdinalIgnoreCase))
        {
            lock (Gate)
            {
                if (Loaded.TryGetValue(library, out var handle))
                    return handle;
            }
            return AreCarried ? throw new DllNotFoundException($"{library} is used before Setup has unpacked it.") : 0;
        }
        if (name.Contains('\\') || name.Contains('/'))
            return 0;
        var system = LoadLibraryEx(name, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (system != 0 || !AreCarried)
            return system;
        throw new DllNotFoundException($"{name} is not in System32, and Setup loads no other library by name.");
    }

    /// <summary>
    /// Writes the carried libraries into a new folder and loads them from there; before Avalonia starts. Does nothing in
    /// a build (no resources) or when done already. Removes what earlier runs left in the same place first.
    /// </summary>
    public static void Unpack(SetupPaths paths, bool elevated, SetupLog log)
    {
        if (Folder is not null || !AreCarried)
            return;
        var parent = elevated ? paths.SystemTemp : paths.Temp;
        Directory.CreateDirectory(parent);
        RemoveOld(parent, elevated, log);
        var folder = Path.Combine(parent, $"{FolderPrefix}{Guid.NewGuid():N}");
        NativeMethods.CreateNewFolder(folder, elevated ? Program.AdminOnlyFolder : null);
        Folder = folder;
        foreach (var library in Carried)
        {
            var file = Path.Combine(folder, library + ".dll");
            using (var source = typeof(NativeLibraries).Assembly.GetManifestResourceStream(ResourceName(library))
                                ?? throw new InvalidDataException($"{library} is missing from Setup."))
            using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            // Its own imports (fontsub.dll and the like) from System32 too, or from this folder.
            var handle = LoadLibraryEx(file, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (handle == 0)
            {
                var error = Marshal.GetLastSystemError();
                throw new Win32Exception(error, $"Could not load {file}: {new Win32Exception(error).Message}");
            }
            lock (Gate)
                Loaded[library] = handle;
        }
        log.Info($"Native libraries in {folder}");
    }

    /// <summary>
    /// Deletes the folders earlier runs left in <paramref name="parent"/>; one still in use (another Setup runs from
    /// it) stays. With administrator rights only folders an administrator made: in %SystemRoot%\Temp a standard user
    /// may make folders of this name too, and swap what is in them for links to elsewhere.
    /// </summary>
    internal static void RemoveOld(string parent, bool elevated, SetupLog log)
    {
        // A folder this new may be another Setup's that is still writing its libraries.
        var before = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(parent, FolderPrefix + "*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var folder in folders)
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.CreationTimeUtc > before)
                    continue;
                if (elevated && !MadeByAdministrators(info))
                    continue;
                // Only the files Setup writes, then the folder if that left it empty: nothing is deleted recursively.
                foreach (var library in Carried)
                    File.Delete(Path.Combine(folder, library + ".dll"));
                Directory.Delete(folder);
                log.Info($"Removed {folder}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or not ours to delete: a later run tries again.
            }
        }
    }

    /// <summary>Owned by Administrators or SYSTEM, which a standard user can't make the owner of anything.</summary>
    private static bool MadeByAdministrators(DirectoryInfo folder) =>
        folder.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

    /// <summary>Deletes this run's folder once the process has ended (a later run removes it otherwise).</summary>
    public static void RemoveAfterExit(SetupLog log)
    {
        if (Folder is not { } folder)
            return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /c ping -n 4 127.0.0.1 >nul & rd /s /q \"{folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Warn($"Could not clean up {folder}: {ex.Message}");
        }
    }
}
