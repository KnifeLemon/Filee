// OS integration points. Windows implementation: Filee.Platform.Windows. macOS: planned.
// Code that needs the OS goes through this interface so the rest of the app stays portable.

namespace Filee.Core.Platform;

/// <summary>Operating-system specific services.</summary>
public interface IPlatformServices
{
    /// <summary>True if the window under the given screen point (physical pixels) belongs to the file manager or desktop.</summary>
    bool IsFileManagerAt(int x, int y);

    /// <summary>Process name (without extension) owning the window under the point, or <c>null</c>.</summary>
    string? ProcessNameAt(int x, int y);

    /// <summary>Files currently selected in the foreground file manager window (empty if none).</summary>
    IReadOnlyList<string> GetFileManagerSelection();

    /// <summary>System drag threshold in physical pixels.</summary>
    int SystemDragThreshold { get; }

    /// <summary>True if the user asked the OS to reduce animations.</summary>
    bool PrefersReducedMotion { get; }

    /// <summary>Adds or removes "start with the system".</summary>
    void SetStartWithSystem(bool enabled, string executablePath);

    /// <summary>Adds or removes the file manager context-menu entry ("Convert with Filee") and Send To shortcut.</summary>
    void SetContextMenu(bool enabled, string executablePath, string label);

    /// <summary>Opens a folder in the file manager and selects the file, if given.</summary>
    void RevealInFileManager(string path);
}

/// <summary>Fallback used on platforms without an implementation yet. Everything is a no-op.</summary>
public sealed class NullPlatformServices : IPlatformServices
{
    public bool IsFileManagerAt(int x, int y) => true;
    public string? ProcessNameAt(int x, int y) => null;
    public IReadOnlyList<string> GetFileManagerSelection() => [];
    public int SystemDragThreshold => 4;
    public bool PrefersReducedMotion => false;
    public void SetStartWithSystem(bool enabled, string executablePath) { }
    public void SetContextMenu(bool enabled, string executablePath, string label) { }

    public void RevealInFileManager(string path)
    {
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (target is null)
            return;
        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(opener, $"\"{target}\"") { UseShellExecute = false });
    }
}
