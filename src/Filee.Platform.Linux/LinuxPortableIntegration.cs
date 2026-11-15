// Removes only Linux launchers and Thunar actions generated for this portable executable.

using System.Xml;
using System.Xml.Linq;

namespace Filee.Platform.Linux;

/// <summary>Owned-entry cleanup, separate from the normal integration switches which also replace old copies.</summary>
public sealed class LinuxPortableIntegration(string configHome, string dataHome)
{
    /// <summary>Uses the same XDG directories as LinuxPlatformServices.</summary>
    public static LinuxPortableIntegration Current => new(Home("XDG_CONFIG_HOME", ".config"), Home("XDG_DATA_HOME", ".local/share"));

    /// <summary>Whether any supported entry targets this exact executable.</summary>
    public bool HasOwnedEntries(string executable) => OwnedFiles(executable).Any() || OwnedActions(executable).Any();

    /// <summary>Rechecks ownership before removing each launcher, preserving unrelated Thunar actions and shortcuts.</summary>
    public void RemoveOwnedEntries(string executable)
    {
        foreach (var path in OwnedFiles(executable)) File.Delete(path);
        var actions = OwnedActions(executable).ToList();
        if (actions.Count == 0) return;
        var document = actions[0].Document!;
        actions.Remove();
        LinuxDesktopIntegration.Write(ThunarPath, document.ToString());
        // The accelerator belongs to the action id, so keep it if another action with that id still exists.
        if (!document.Root!.Elements("action").Any(a => (string?)a.Element("unique-id") == LinuxDesktopIntegration.ThunarActionId)
            && RegularFile(Path.Combine(configHome, "Thunar", "accels.scm")))
            new LinuxDesktopIntegration(configHome, dataHome).SetThunarShortcut(null);
    }

    private string ThunarPath => Path.Combine(configHome, "Thunar", "uca.xml");

    private IEnumerable<string> OwnedFiles(string executable)
    {
        string desktop;
        try { desktop = LinuxDesktopIntegration.DesktopArgument(executable); }
        catch (ArgumentException) { yield break; } // Such a path could not have been registered by Filee.
        foreach (var (path, command) in new[]
        {
            (Path.Combine(configHome, "autostart", "filee.desktop"), $"Exec={desktop} --background"),
            (Path.Combine(dataHome, "Thunar", "sendto", "filee.desktop"), $"Exec={desktop} --convert %F"),
            (Path.Combine(dataHome, "nemo", "actions", "filee.nemo_action"), $"Exec={desktop} --convert %F"),
            (Path.Combine(dataHome, "kio", "servicemenus", "filee.desktop"), $"Exec={desktop} --convert %F"),
            (Path.Combine(dataHome, "nautilus", "scripts", "Filee"), $"exec {LinuxDesktopIntegration.ShellArgument(executable)} --convert \"$@\""),
        })
        {
            if (!RegularFile(path)) continue;
            var commands = File.ReadLines(path).Where(line => line.StartsWith("Exec=", StringComparison.Ordinal)
                || line.StartsWith("exec ", StringComparison.Ordinal)).ToArray();
            if (commands.Length == 1 && commands[0] == command) yield return path;
        }
    }

    private IEnumerable<XElement> OwnedActions(string executable)
    {
        if (!RegularFile(ThunarPath)) return [];
        XDocument document;
        try { document = XDocument.Load(ThunarPath, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return []; }
        if (document.Root?.Name != "actions") return [];
        string command;
        try { command = LinuxDesktopIntegration.ShellArgument(executable).Replace("%", "%%", StringComparison.Ordinal) + " --convert %F"; }
        catch (ArgumentException) { return []; }
        return document.Root.Elements("action").Where(a => (string?)a.Element("unique-id") == LinuxDesktopIntegration.ThunarActionId
            && (string?)a.Element("command") == command);
    }

    private static bool RegularFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static string Home(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured
        && Path.IsPathFullyQualified(configured) ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
}
