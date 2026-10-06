using System.Xml.Linq;

namespace Filee.Platform.MacOS;

internal static class MacOSIntegrationFiles
{
    internal static string LaunchAgent(string executablePath) => Plist(Dict(
        ("Label", "com.filee.app"),
        ("AssociatedBundleIdentifiers", Array("com.filee.app")),
        ("ProgramArguments", Array(executablePath, "--background")),
        ("LimitLoadToSessionType", "Aqua"),
        ("RunAtLoad", true),
        ("KeepAlive", false)));

    internal static string ServiceInfo(string label) => Plist(Dict(
        ("CFBundleName", label),
        ("CFBundleIdentifier", "com.filee.app.convert-service"),
        ("NSServices", Array(Dict(
            ("NSMenuItem", Dict(("default", label))),
            ("NSMessage", "runWorkflowAsService"),
            ("NSRequiredContext", Dict(("NSApplicationIdentifier", "com.apple.finder"))),
            ("NSSendFileTypes", Array("public.item")))))));

    internal static string Workflow(string executablePath) => Plist(Dict(
        ("AMDocumentVersion", "2"),
        ("actions", Array(Dict(("action", Dict(
            ("AMAccepts", Dict(("Container", "List"), ("Optional", false), ("Types", Array("com.apple.cocoa.path")))),
            ("AMProvides", Dict(("Container", "List"), ("Types", Array("com.apple.cocoa.string")))),
            ("AMApplication", Array("Automator")),
            ("AMActionVersion", "2.0.3"),
            ("AMParameterProperties", Dict(("COMMAND_STRING", Dict()), ("inputMethod", Dict()), ("shell", Dict()), ("source", Dict()))),
            ("ActionBundlePath", "/System/Library/Automator/Run Shell Script.action"),
            ("ActionName", "Run Shell Script"),
            ("ActionParameters", Dict(
                ("COMMAND_STRING", ConversionCommand(executablePath)),
                ("CheckedForUserDefaultShell", true),
                ("inputMethod", 1),
                ("shell", "/bin/sh"),
                ("source", ""))),
            ("BundleIdentifier", "com.apple.RunShellScript"),
            ("Class Name", "RunShellScriptAction"),
            ("CanShowWhenRun", false),
            ("InputUUID", "E79B8709-D276-4DB2-A0D9-07FDF599774E"),
            ("OutputUUID", "09A1D308-CB6C-4EC8-9545-68272C47BA52"),
            ("UUID", "ED186538-5D03-4A64-88BD-52FC9A905E4F"),
            ("arguments", Dict())))))),
        ("connectors", Dict()),
        ("workflowMetaData", Dict(
            ("serviceApplicationBundleID", "com.apple.finder"),
            ("serviceApplicationPath", "/System/Library/CoreServices/Finder.app"),
            ("serviceInputTypeIdentifier", "com.apple.Automator.fileSystemObject"),
            ("serviceOutputTypeIdentifier", "com.apple.Automator.nothing"),
            ("serviceProcessesInput", true),
            ("workflowTypeIdentifier", "com.apple.Automator.servicesMenu")))));

    internal static string ConversionCommand(string executablePath)
    {
        const string contents = "/Contents/MacOS/";
        var index = executablePath.LastIndexOf(contents, StringComparison.Ordinal);
        if (index > 0 && executablePath[..index].EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            // A fresh process forwards arguments to Filee's running instance; open otherwise discards them.
            return $"exec /usr/bin/open -n -a {ShellQuote(executablePath[..index])} --args --convert \"$@\"";
        }
        return $"exec {ShellQuote(executablePath)} --convert \"$@\"";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string Plist(XElement dictionary) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));

    private static XElement Dict(params (string Key, object Value)[] entries) => new("dict",
        entries.SelectMany(entry => new[] { new XElement("key", entry.Key), Value(entry.Value) }));

    private static XElement Array(params object[] values) => new("array", values.Select(Value));

    private static XElement Value(object value) => value switch
    {
        XElement element => element,
        bool flag => new XElement(flag ? "true" : "false"),
        int number => new XElement("integer", number),
        string text => new XElement("string", text),
        _ => throw new ArgumentException("Unsupported property-list value.", nameof(value)),
    };
}
