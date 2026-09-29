// Command line options:
//   Filee.exe                    open the main window
//   Filee.exe --background       start in the tray only (used by "start with Windows")
//   Filee.exe --settings         open the main window
//   Filee.exe --convert a b ...  open the donut for these files (Explorer context menu / Send To)

namespace Filee.App.Services;

public sealed record CommandLine(bool Background, bool ShowSettings, IReadOnlyList<string> ConvertFiles)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var background = false;
        var settings = false;
        var files = new List<string>();
        var collecting = false;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--background":
                    background = true;
                    break;
                case "--settings":
                    settings = true;
                    break;
                case "--convert":
                    collecting = true;
                    break;
                default:
                    // Velopack passes its own --veloapp-* arguments; ignore anything unknown that isn't a file.
                    if (collecting && !arg.StartsWith("--", StringComparison.Ordinal) && (File.Exists(arg) || Directory.Exists(arg)))
                        files.Add(Path.GetFullPath(arg));
                    break;
            }
        }
        return new CommandLine(background, settings, files);
    }
}
