using System.Xml.Linq;
using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class MacOSIntegrationTests
{
    [Fact]
    public void LaunchAgentKeepsExecutableAndArgumentsSeparate()
    {
        const string executable = "/Users/a & b/도구 '$(touch nope)'/Filee.app/Contents/MacOS/Filee";
        var root = ReadPlist(MacOSIntegrationFiles.LaunchAgent(executable));
        Assert.Equal([executable, "--background"], Property(root, "ProgramArguments").Elements().Select(x => x.Value));
        Assert.Equal("true", Property(root, "RunAtLoad").Name.LocalName);
        Assert.Equal("false", Property(root, "KeepAlive").Name.LocalName);
        Assert.Equal("Aqua", Property(root, "LimitLoadToSessionType").Value);
    }

    [Fact]
    public void ServiceMenuEscapesLocalizedLabelAndOnlyAcceptsFinderFiles()
    {
        const string label = "Filee로 변환 <빠르게> & 'convert'";
        var root = ReadPlist(MacOSIntegrationFiles.ServiceInfo(label));
        var service = Property(root, "NSServices").Element("dict")!;
        Assert.Equal(label, Property(Property(service, "NSMenuItem"), "default").Value);
        Assert.Equal(label, Property(root, "CFBundleName").Value);
        Assert.Equal("com.apple.finder", Property(Property(service, "NSRequiredContext"), "NSApplicationIdentifier").Value);
        Assert.Equal("public.item", Property(service, "NSSendFileTypes").Element("string")!.Value);
    }

    [Fact]
    public void QuickActionLaunchesFreshBundleInstanceToForwardArguments()
    {
        const string executable = "/Applications/O'Brien & Friends/Filee.app/Contents/MacOS/Filee";
        var workflow = ReadPlist(MacOSIntegrationFiles.Workflow(executable));
        var action = Property(Property(workflow, "actions").Element("dict")!, "action");
        var parameters = Property(action, "ActionParameters");
        Assert.Equal("1", Property(parameters, "inputMethod").Value);
        Assert.Equal("/bin/sh", Property(parameters, "shell").Value);
        Assert.Equal("exec /usr/bin/open -n -a '/Applications/O'\"'\"'Brien & Friends/Filee.app' --args --convert \"$@\"",
            Property(parameters, "COMMAND_STRING").Value);
        var metadata = Property(workflow, "workflowMetaData");
        Assert.Equal("com.apple.Automator.fileSystemObject", Property(metadata, "serviceInputTypeIdentifier").Value);
        Assert.Equal("com.apple.finder", Property(metadata, "serviceApplicationBundleID").Value);
    }

    [Fact]
    public void RawExecutableIsQuotedWithoutEvaluatingShellMetacharacters()
    {
        Assert.Equal("exec '/tmp/$(touch nope) `echo bad` \"quote\"' --convert \"$@\"",
            MacOSIntegrationFiles.ConversionCommand("/tmp/$(touch nope) `echo bad` \"quote\""));
    }

    [Fact]
    public void SelectionJsonPreservesWhitespaceUnicodeAndNewlines()
    {
#pragma warning disable CA1416 // Pure JSON parsing does not access the operating system.
        var paths = MacOSPlatformServices.ParseSelection("[\"/tmp/ 한글 .txt\",\"/tmp/line\\nbreak.txt\",\"/tmp/quote\\\".txt\"]");
#pragma warning restore CA1416
        Assert.Equal(["/tmp/ 한글 .txt", "/tmp/line\nbreak.txt", "/tmp/quote\".txt"], paths);
    }

    private static XElement ReadPlist(string text)
    {
        var document = XDocument.Parse(text);
        Assert.Equal("plist", document.Root!.Name.LocalName);
        Assert.Equal("1.0", document.Root.Attribute("version")!.Value);
        return document.Root.Element("dict")!;
    }

    private static XElement Property(XElement dictionary, string key) =>
        dictionary.Elements("key").Single(x => x.Value == key).ElementsAfterSelf().First();
}
