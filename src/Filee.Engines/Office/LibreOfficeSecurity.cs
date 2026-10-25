// Locks down the LibreOffice profiles Filee converts with, before every run: macros never run, links and outside data
// are never updated while a document loads, and VBA code from Office files is not made executable.
//
// LibreOffice's conversion mode (--convert-to) already doesn't execute document macros (tested with LibreOffice 26.8:
// neither an "on load" Basic macro nor a Basic function called from a cell ran, even at the lowest security level).
// This is defense in depth against a change of that behaviour and against documents that pull in outside content.
//
// The settings go into the profile's user/registrymodifications.xcu: LibreOffice reads it at start and keeps the values
// when it writes the file back. Writing it into a profile LibreOffice hasn't created yet works too. Paths and values were
// checked against the configuration schema of LibreOffice 26.8 (share/registry/main.xcd):
//  * Common/Security/Scripting: MacroSecurityLevel 3 (only macros from trusted locations, of which Filee's profiles have
//    none), DisableMacrosExecution true (Basic, BeanShell, JavaScript and Python), BlockUntrustedRefererLinks true.
//  * Writer/Content/Update Link 2 and Calc/Content/Update Link 1: never update links when loading (the two applications
//    number "never" differently).
//  * Writer|Calc/Filter/Import/VBA Executable false: VBA code in Office files is kept as text only.

using System.Xml.Linq;

namespace Filee.Engines.Office;

/// <summary>Writes Filee's security settings into a LibreOffice user profile.</summary>
public static class LibreOfficeSecurity
{
    private static readonly XNamespace Oor = "http://openoffice.org/2001/registry";

    /// <summary>The settings: configuration path, property, value.</summary>
    internal static readonly (string Path, string Property, string Value)[] Settings =
    [
        ("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3"),
        ("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true"),
        ("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true"),
        ("/org.openoffice.Office.Writer/Content/Update", "Link", "2"),
        ("/org.openoffice.Office.Calc/Content/Update", "Link", "1"),
        ("/org.openoffice.Office.Writer/Filter/Import/VBA", "Executable", "false"),
        ("/org.openoffice.Office.Calc/Filter/Import/VBA", "Executable", "false"),
    ];

    /// <summary>The settings file of a profile (<c>-env:UserInstallation</c> folder).</summary>
    public static string SettingsFile(string profileDirectory) =>
        Path.Combine(profileDirectory, "user", "registrymodifications.xcu");

    /// <summary>
    /// Makes sure the profile in <paramref name="profileDirectory"/> carries <see cref="Settings"/>: creates the settings
    /// file when there is none, else replaces only Filee's items and keeps every other one.
    /// </summary>
    public static void Apply(string profileDirectory)
    {
        var file = SettingsFile(profileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        XDocument document;
        if (File.Exists(file))
        {
            document = XDocument.Load(file, LoadOptions.PreserveWhitespace);
            if (Carries(document))
                return;
        }
        else
        {
            document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XElement(Oor + "items",
                    new XAttribute(XNamespace.Xmlns + "oor", Oor.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xs", "http://www.w3.org/2001/XMLSchema"),
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance")));
        }

        var root = document.Root!;
        foreach (var (path, property, _) in Settings)
            root.Elements("item").Where(item => Is(item, path, property)).Remove();
        foreach (var (path, property, value) in Settings)
            root.Add(new XElement("item", new XAttribute(Oor + "path", path),
                new XElement("prop", new XAttribute(Oor + "name", property), new XAttribute(Oor + "op", "fuse"),
                    new XElement("value", value))));

        var temporary = file + ".filee-tmp";
        document.Save(temporary, SaveOptions.DisableFormatting);
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>True when every setting is there with Filee's value.</summary>
    internal static bool Carries(XDocument document) =>
        Settings.All(setting => document.Root?.Elements("item").Any(item =>
            Is(item, setting.Path, setting.Property) && (string?)item.Element("prop")?.Element("value") == setting.Value) == true);

    private static bool Is(XElement item, string path, string property) =>
        (string?)item.Attribute(Oor + "path") == path && (string?)item.Element("prop")?.Attribute(Oor + "name") == property;
}
