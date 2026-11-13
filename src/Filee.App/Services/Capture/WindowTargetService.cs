using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace Filee.App.Services.Capture;

public sealed class WindowTargetService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);
    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    public WindowTargetInfo? DetectWindowAtPoint(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var myPid = (uint)Environment.ProcessId;

            // If coordinates not provided, automatically fetch live cursor point
            if (x == 0 && y == 0 && GetCursorPos(out var pt))
            {
                x = pt.X;
                y = pt.Y;
            }

            // Strategy 1: If coordinates given, enumerate top-level windows in Z-order to find the topmost
            // non-Filee, non-desktop application window containing (x, y).
            if (x != 0 || y != 0)
            {
                WindowTargetInfo? targetUnderCursor = null;
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                        return true;

                    var className = GetWindowClassName(hwnd);
                    if (IsShellOrDesktopClass(className))
                        return true;

                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == myPid)
                        return true;

                    if (GetWindowRect(hwnd, out var rect))
                    {
                        if (rect.Left <= x && x <= rect.Right && rect.Top <= y && y <= rect.Bottom)
                        {
                            var width = rect.Right - rect.Left;
                            var height = rect.Bottom - rect.Top;
                            if (width > 80 && height > 80)
                            {
                                var title = GetWindowTitle(hwnd);
                                string procName = "App";
                                try
                                {
                                    using var proc = Process.GetProcessById((int)pid);
                                    procName = proc.ProcessName;
                                    if (string.IsNullOrWhiteSpace(title))
                                        title = proc.MainWindowTitle;
                                }
                                catch { }

                                targetUnderCursor = new WindowTargetInfo(hwnd, title, procName, rect.Left, rect.Top, width, height);
                                return false; // Found topmost window under cursor
                            }
                        }
                    }
                    return true;
                }, 0);

                if (targetUnderCursor != null)
                    return targetUnderCursor;
            }

            // Strategy 2: Check current foreground window
            var fg = GetForegroundWindow();
            if (fg != 0)
            {
                GetWindowThreadProcessId(fg, out var fgPid);
                if (fgPid != myPid)
                {
                    var fgClass = GetWindowClassName(fg);
                    if (!IsShellOrDesktopClass(fgClass) && GetWindowRect(fg, out var rect))
                    {
                        var width = rect.Right - rect.Left;
                        var height = rect.Bottom - rect.Top;
                        if (width > 80 && height > 80)
                        {
                            var title = GetWindowTitle(fg);
                            string procName = "App";
                            try
                            {
                                using var proc = Process.GetProcessById((int)fgPid);
                                procName = proc.ProcessName;
                                if (string.IsNullOrWhiteSpace(title))
                                    title = proc.MainWindowTitle;
                            }
                            catch { }

                            return new WindowTargetInfo(fg, title, procName, rect.Left, rect.Top, width, height);
                        }
                    }
                }
            }

            // Strategy 3: Direct WindowFromPoint with ancestor resolution
            var hwndFromPoint = WindowFromPoint(new POINT { X = x, Y = y });
            if (hwndFromPoint != 0)
            {
                var root = GetAncestor(hwndFromPoint, GA_ROOT);
                if (root != 0)
                    hwndFromPoint = root;

                GetWindowThreadProcessId(hwndFromPoint, out var pid);
                if (pid != myPid)
                {
                    var className = GetWindowClassName(hwndFromPoint);
                    if (!IsShellOrDesktopClass(className))
                    {
                        var title = GetWindowTitle(hwndFromPoint);
                        string processName = "App";
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            processName = proc.ProcessName;
                            if (string.IsNullOrWhiteSpace(title))
                                title = proc.MainWindowTitle;
                        }
                        catch { }

                        if (GetWindowRect(hwndFromPoint, out var rect))
                        {
                            var width = rect.Right - rect.Left;
                            var height = rect.Bottom - rect.Top;
                            if (width > 50 && height > 50)
                            {
                                return new WindowTargetInfo(hwndFromPoint, title, processName, rect.Left, rect.Top, width, height);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore capture detection errors
        }

        return null;
    }

    public IReadOnlyList<WindowTargetInfo> GetOpenWindows()
    {
        var result = new List<WindowTargetInfo>();
        if (!OperatingSystem.IsWindows())
            return result;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                    return true;

                var className = GetWindowClassName(hwnd);
                if (IsShellOrDesktopClass(className))
                    return true;

                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                string procName = "App";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch
                {
                }

                if (GetWindowRect(hwnd, out var rect))
                {
                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width > 100 && height > 100)
                    {
                        result.Add(new WindowTargetInfo(hwnd, title, procName, rect.Left, rect.Top, width, height));
                    }
                }

                return true;
            }, 0);
        }
        catch
        {
            // Ignore enumeration errors
        }

        return result;
    }

    public IReadOnlyList<ScreenTargetInfo> GetScreens(Window? referenceWindow = null)
    {
        var list = new List<ScreenTargetInfo>();
        try
        {
            var screens = referenceWindow?.Screens?.All
                ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens?.All;

            if (screens != null && screens.Count > 0)
            {
                int index = 1;
                foreach (var screen in screens)
                {
                    var bounds = screen.Bounds;
                    list.Add(new ScreenTargetInfo(
                        index,
                        $"Display {index}",
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height,
                        screen.IsPrimary));
                    index++;
                }
                return list;
            }
        }
        catch
        {
        }

        // Fallback default screen
        list.Add(new ScreenTargetInfo(1, "Primary Display", 0, 0, 1920, 1080, true));
        return list;
    }

    private static readonly Dictionary<string, string> _audioDeviceMonikers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _cachedAudioDevices = [];

    public static string? GetAudioDeviceMoniker(string deviceName)
    {
        lock (_audioDeviceMonikers)
        {
            return _audioDeviceMonikers.TryGetValue(deviceName, out var moniker) ? moniker : null;
        }
    }

    public static IReadOnlyList<string> GetCachedAudioInputDevices()
    {
        lock (_cachedAudioDevices)
        {
            if (_cachedAudioDevices.Count > 0)
                return _cachedAudioDevices.ToList();
        }
        return ["Default Microphone"];
    }

    public IReadOnlyList<string> GetAudioInputDevices()
    {
        var devices = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var ffmpeg = CaptureRecordingEngine.FindFfmpeg();
                if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg))
                {
                    var psi = new ProcessStartInfo(ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"])
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();

                        var lines = stderr.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var line = lines[i].Trim();
                            var m = System.Text.RegularExpressions.Regex.Match(line, "\"([^\"]+)\"\\s+\\(audio\\)");
                            if (m.Success)
                            {
                                var name = m.Groups[1].Value.Trim();
                                string? alt = null;
                                if (i + 1 < lines.Length)
                                {
                                    var altMatch = System.Text.RegularExpressions.Regex.Match(lines[i + 1], "Alternative name\\s+\"([^\"]+)\"");
                                    if (altMatch.Success)
                                    {
                                        alt = altMatch.Groups[1].Value.Trim();
                                    }
                                }

                                if (!devices.Contains(name))
                                    devices.Add(name);

                                if (!string.IsNullOrEmpty(alt))
                                {
                                    lock (_audioDeviceMonikers)
                                    {
                                        _audioDeviceMonikers[name] = alt;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (devices.Count == 0)
            devices.Add("Default Microphone");

        lock (_cachedAudioDevices)
        {
            _cachedAudioDevices.Clear();
            _cachedAudioDevices.AddRange(devices);
        }

        return devices;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var chars = new char[256];
        var len = GetWindowText(hwnd, chars, chars.Length);
        return len > 0 ? new string(chars, 0, len) : "";
    }

    private static string GetWindowClassName(nint hwnd)
    {
        var chars = new char[128];
        var len = GetClassName(hwnd, chars, chars.Length);
        return len > 0 ? new string(chars, 0, len) : "";
    }

    private static bool IsShellOrDesktopClass(string className)
    {
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow";
    }
}
