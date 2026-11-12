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
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    public WindowTargetInfo? DetectWindowAtPoint(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
            if (hwnd == 0)
                return null;

            var root = GetAncestor(hwnd, GA_ROOT);
            if (root != 0)
                hwnd = root;

            var className = GetWindowClassName(hwnd);
            if (IsShellOrDesktopClass(className))
                return null;

            var title = GetWindowTitle(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);

            string processName = "Unknown";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
                if (string.IsNullOrWhiteSpace(title))
                    title = proc.MainWindowTitle;
            }
            catch
            {
                // Process may have exited or insufficient permissions
            }

            if (GetWindowRect(hwnd, out var rect))
            {
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width > 50 && height > 50)
                {
                    return new WindowTargetInfo(hwnd, title, processName, rect.Left, rect.Top, width, height);
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

    public IReadOnlyList<string> GetAudioInputDevices()
    {
        // Default device option + common fallbacks
        return ["Default Microphone"];
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
