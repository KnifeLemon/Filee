// The settings every LibreOffice worker profile gets before soffice starts, in the profile's own configuration layer
// (user/registrymodifications.xcu). Headless --convert-to doesn't run document macros in today's LibreOffice, but that
// is its default, not something the profile said; and on load LibreOffice may update a document's links (DDE fields,
// linked files and OLE objects, linked pictures), which reaches out to whatever they point at. The profile pins both
// down, whatever a LibreOffice installation's own defaults are.
//
// Names and values are LibreOffice's configuration schema (officecfg/registry/schema/org/openoffice/Office/*.xcs):
//  * Common/Security/Scripting: MacroSecurityLevel 3 ("very high", trusted locations only), DisableMacrosExecution
//    (no Basic, Beanshell, JavaScript or Python at all), SecureURL empty (no trusted locations), and
//    BlockUntrustedRefererLinks: links and linked pictures of a document outside a trusted location are not loaded.
//    sfx2's LinkManager then updates no links at all, in every application; Impress and Draw have no setting of their
//    own for link updates.
//  * Writer/Content/Update/Link and Calc/Content/Update/Link: never update links on load (2 and 1: the two
//    enumerations differ).
//  * Writer and Calc Filter/Import/VBA/Executable: VBA from Word and Excel files is kept as code, not made runnable.
// DisableActiveContent is left alone: it also refuses LibreOffice's own embedded objects (charts, formulas).

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Office;

internal static class LibreOfficeProfile
{
    private static readonly XNamespace Oor = "http://openoffice.org/2001/registry";

    /// <summary>A configuration value: the group's path, the property and its value as the .xcu writes it.</summary>
    internal sealed record Setting(string Path, string Name, string Value, string? Type = null);

    private const string Scripting = "/org.openoffice.Office.Common/Security/Scripting";

    internal static readonly Setting[] Settings =
    [
        new(Scripting, "MacroSecurityLevel", "3"),
        new(Scripting, "DisableMacrosExecution", "true"),
        new(Scripting, "SecureURL", "", "oor:string-list"),
        new(Scripting, "BlockUntrustedRefererLinks", "true"),
        new("/org.openoffice.Office.Writer/Content/Update", "Link", "2"),
        new("/org.openoffice.Office.Calc/Content/Update", "Link", "1"),
        new("/org.openoffice.Office.Writer/Filter/Import/VBA", "Executable", "false"),
        new("/org.openoffice.Office.Calc/Filter/Import/VBA", "Executable", "false"),
    ];

    /// <summary>The configuration file of a profile (-env:UserInstallation folder).</summary>
    internal static string ConfigurationFile(string profileDirectory) =>
        Path.Combine(profileDirectory, "user", "registrymodifications.xcu");

    /// <summary>
    /// Puts <see cref="Settings"/> into the profile's configuration, keeping everything else LibreOffice wrote there
    /// (it writes the file again when it ends). A file it can't read is started over; LibreOffice fills in the rest.
    /// </summary>
    public static void Apply(string profileDirectory)
    {
        var file = ConfigurationFile(profileDirectory);
        var document = Load(file) ?? new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Oor + "items",
            new XAttribute(XNamespace.Xmlns + "oor", Oor.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xs", "http://www.w3.org/2001/XMLSchema"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"), "\n"));
        var root = document.Root!;
        var changed = false;
        foreach (var setting in Settings)
        {
            var items = root.Elements("item")
                .Where(item => (string?)item.Attribute(Oor + "path") == setting.Path
                               && item.Elements("prop").Any(prop => (string?)prop.Attribute(Oor + "name") == setting.Name))
                .ToList();
            if (items is [var only] && only.Element("prop")?.Element("value") is { } value && value.Value == setting.Value
                && !value.HasElements)
                continue;
            items.ForEach(item => item.Remove());
            var prop = new XElement("prop", new XAttribute(Oor + "name", setting.Name), new XAttribute(Oor + "op", "fuse"));
            if (setting.Type is { } type)
                prop.Add(new XAttribute(Oor + "type", type));
            prop.Add(new XElement("value", setting.Value));
            root.Add(new XElement("item", new XAttribute(Oor + "path", setting.Path), prop), "\n");
            changed = true;
        }
        if (!changed)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".filee";
        using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            document.Save(writer);
        File.Move(temporary, file, overwrite: true);
    }

    private static XDocument? Load(string file)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Root?.Name == Oor + "items" ? document : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
