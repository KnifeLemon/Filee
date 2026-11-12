// Cleans executable-owned LaunchAgents and legacy Automator actions; shared Finder service preferences stay intact.

using System.Xml;
using System.Xml.Linq;

namespace Filee.Platform.MacOS;

/// <summary>Removes only entries whose stored command identifies this portable copy.</summary>
public sealed class MacOSPortableIntegration(string userLibrary)
{
    /// <summary>Integration in the current user's Library.</summary>
    public static MacOSPortableIntegration Current => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library"));

    private string Agent => Path.Combine(userLibrary, "LaunchAgents", "com.filee.app.plist");
    private string Workflow => Path.Combine(userLibrary, "Services", "Filee.workflow");

    /// <summary>Whether a LaunchAgent or legacy workflow belongs to this executable.</summary>
    public bool HasOwnedEntries(string executable) => OwnsAgent(executable) || OwnsWorkflow(executable);

    /// <summary>Rechecks ownership; the bundle's shared NSServicesStatus preference is deliberately left alone.</summary>
    public void RemoveOwnedEntries(string executable)
    {
        if (OwnsAgent(executable)) File.Delete(Agent);
        if (OwnsWorkflow(executable))
        {
            // Refuse links anywhere in the bundle before recursively deleting a verified, owned workflow.
            var pending = new Stack<string>();
            pending.Push(Workflow);
            while (pending.TryPop(out var directory))
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Cannot remove a workflow containing links.");
                    if (item is DirectoryInfo) pending.Push(item.FullName);
                }
            Directory.Delete(Workflow, recursive: true);
        }
    }

    private bool OwnsAgent(string executable)
    {
        var root = ReadPlist(Agent)?.Element("dict");
        var target = Property(root, "Program") ?? Property(root, "ProgramArguments")?.Elements().FirstOrDefault();
        return target is { Name.LocalName: "string" } && target.Value == executable;
    }

    private bool OwnsWorkflow(string executable)
    {
        if (!Directory.Exists(Workflow) || (File.GetAttributes(Workflow) & FileAttributes.ReparsePoint) != 0) return false;
        var root = ReadPlist(Path.Combine(Workflow, "Contents", "document.wflow"));
        if (root is null) return false;
        var commands = root.Descendants("key").Where(k => k.Value == "COMMAND_STRING").ToArray();
        return commands.Length > 0 && commands.All(k => k.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "string" } value
            && value.Value == ConversionCommand(executable));
    }

    internal static string ConversionCommand(string executable)
    {
        const string contents = "/Contents/MacOS/";
        var index = executable.LastIndexOf(contents, StringComparison.Ordinal);
        return index > 0 && executable[..index].EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && executable[(index + contents.Length)..] == "Filee"
            ? $"exec /usr/bin/open -n -a {Quote(executable[..index])} --args --convert \"$@\""
            : $"exec {Quote(executable)} --convert \"$@\"";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static XElement? ReadPlist(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        try { return XDocument.Load(path).Root is { Name.LocalName: "plist" } root ? root : null; }
        catch (XmlException) { return null; }
    }

    private static XElement? Property(XElement? dictionary, string name) => dictionary?.Elements("key")
        .FirstOrDefault(k => k.Value == name)?.ElementsAfterSelf().FirstOrDefault();
}
