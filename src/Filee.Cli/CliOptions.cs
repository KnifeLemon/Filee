// Parses the "filee" command line. Kept dependency-free: a handful of commands and options.

using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Cli;

/// <summary>A parsed command line. <see cref="Error"/> is set when the arguments are wrong.</summary>
internal sealed record CliOptions
{
    public string Command { get; init; } = "help";
    public List<string> Inputs { get; } = [];
    public string? To { get; set; }
    public string? Preset { get; set; }
    public string? Output { get; set; }

    /// <summary>--from: files or folders 'engines install' takes the downloads from instead of the internet (#447).</summary>
    public List<string> From { get; } = [];
    public bool Recursive { get; set; }
    public FileSortOrder? Sort { get; set; }
    public int? Quality { get; set; }
    public string? Pages { get; set; }

    /// <summary>--split-every: PDF → PDF in files of this many pages (#1259).</summary>
    public int? SplitEvery { get; set; }

    /// <summary>--split-every with a length (10m, 90s, 1:30:00): video → video in parts of this many seconds (#1259).</summary>
    public int? SplitEverySeconds { get; set; }

    /// <summary>--split-every as it was typed, for the message when it doesn't suit the target.</summary>
    public string? SplitEveryText { get; set; }

    /// <summary>--split-parts: PDF → PDF, or video → video, in this many files of the same length (#1259).</summary>
    public int? SplitParts { get; set; }

    /// <summary>The longest part --split-every takes: a day.</summary>
    internal const int MaxSplitSeconds = 24 * 3600;
    public ConflictPolicy? Conflict { get; set; }
    public bool Json { get; set; }
    public bool Quiet { get; set; }
    public bool MoveOriginals { get; set; }
    public bool KeepDates { get; set; }

    public bool Zip { get; set; }
    public double? SettleSeconds { get; set; }
    public string? Error { get; set; }

    public static readonly string[] Commands = ["convert", "watch", "formats", "presets", "engines", "help", "version"];

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CliOptions { Command = "help" };
        var first = args[0].ToLowerInvariant();
        var command = first switch
        {
            "-h" or "--help" or "/?" => "help",
            "-v" or "--version" => "version",
            _ => first,
        };
        var options = new CliOptions { Command = command };
        if (!Commands.Contains(command))
        {
            options.Error = $"Unknown command '{args[0]}'.";
            return options;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Count)
                    return args[++i];
                options.Error ??= $"{arg} needs a value.";
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-t" or "--to":
                    options.To = Value();
                    break;
                case "-p" or "--preset":
                    options.Preset = Value();
                    break;
                case "-o" or "--output":
                    options.Output = Value();
                    break;
                case "--from":
                    if (Value() is { } from)
                        options.From.Add(from);
                    break;
                case "-r" or "--recursive":
                    options.Recursive = true;
                    break;
                case "--sort":
                    var sort = Value();
                    if (sort is not null)
                    {
                        options.Sort = sort.ToLowerInvariant() switch
                        {
                            "name" => FileSortOrder.NameAscending,
                            "name-desc" => FileSortOrder.NameDescending,
                            "modified" => FileSortOrder.ModifiedAscending,
                            "modified-desc" => FileSortOrder.ModifiedDescending,
                            "created" => FileSortOrder.CreatedAscending,
                            "created-desc" => FileSortOrder.CreatedDescending,
                            _ => null,
                        };
                        if (options.Sort is null)
                            options.Error ??= "--sort must be name, name-desc, modified, modified-desc, created or created-desc.";
                    }
                    break;
                case "-q" or "--quality":
                    var quality = Value();
                    if (quality is not null)
                    {
                        if (int.TryParse(quality, out var q) && q is >= 1 and <= 100)
                            options.Quality = q;
                        else
                            options.Error ??= "--quality must be a number from 1 to 100.";
                    }
                    break;
                case "--pages":
                    var pages = Value();
                    if (pages is not null)
                    {
                        var check = Filee.Core.Conversion.PageRange.Check(pages, null);
                        if (check.IsValid)
                            options.Pages = check.Text;
                        else
                            options.Error ??= "--pages must be page numbers or ranges from 1, e.g. 13-55 or 1-3,8,12-15.";
                    }
                    break;
                case "--split-every":
                    var every = Value();
                    if (every is not null)
                    {
                        options.SplitEveryText = every;
                        // A plain number is pages; a length with a unit or colons is for videos.
                        if (int.TryParse(every, out var size) && size is >= 1 and <= Filee.Core.Conversion.PageGroups.MaxSize)
                            options.SplitEvery = size;
                        else if (TryParseLength(every, out var seconds) && seconds is >= 1 and <= MaxSplitSeconds)
                            options.SplitEverySeconds = seconds;
                        else
                            options.Error ??= $"--split-every must be a number of pages from 1 to {Filee.Core.Conversion.PageGroups.MaxSize}, "
                                + "or a length of video such as 90s, 10m, 1h30m or 1:30:00.";
                    }
                    break;
                case "--split-parts":
                    var parts = Value();
                    if (parts is not null)
                    {
                        if (int.TryParse(parts, out var count) && count is >= 2 and <= Filee.Core.Conversion.PageGroups.MaxSize)
                            options.SplitParts = count;
                        else
                            options.Error ??= $"--split-parts must be a number of parts from 2 to {Filee.Core.Conversion.PageGroups.MaxSize}.";
                    }
                    break;
                case "--overwrite":
                    options.Conflict = ConflictPolicy.Overwrite;
                    break;
                case "--skip":
                    options.Conflict = ConflictPolicy.Skip;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--move-originals":
                    options.MoveOriginals = true;
                    break;
                case "--keep-dates":
                    options.KeepDates = true;
                    break;
                case "--zip":
                    options.Zip = true;
                    break;
                case "--settle":
                    var settle = Value();
                    if (settle is not null)
                    {
                        if (double.TryParse(settle, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0)
                            options.SettleSeconds = s;
                        else
                            options.Error ??= "--settle must be a number of seconds.";
                    }
                    break;
                case "-h" or "--help":
                    options.Inputs.Insert(0, options.Command);
                    return options with { Command = "help" };
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.StartsWith('-') && arg.Length == 2))
                        options.Error ??= $"Unknown option '{arg}'.";
                    else
                        options.Inputs.Add(arg);
                    break;
            }
        }

        options.Error ??= options.Command switch
        {
            "convert" when options.Inputs.Count == 0 => "Give at least one file or folder to convert.",
            "convert" or "watch" when options.To is null == (options.Preset is null) => "Use either --to <format> or --preset <preset>.",
            "convert" or "watch" when (options.SplitEvery ?? options.SplitEverySeconds) is not null && options.SplitParts is not null =>
                "Use either --split-every or --split-parts.",
            "watch" when options.Inputs.Count != 1 => "Give exactly one folder to watch.",
            "watch" when options.Sort is not null => "--sort only works with 'filee convert'.",
            "engines" when options.Inputs.Count > 0 && options.Inputs[0].ToLowerInvariant() is not ("install" or "remove" or "download") =>
                $"Unknown engines command '{options.Inputs[0]}'. Use 'filee engines', 'filee engines install <id>', 'filee engines download <id> -o <folder>' or 'filee engines remove <id>'.",
            "engines" when options.Inputs.Count == 1 => $"Give the engine to {options.Inputs[0].ToLowerInvariant()}, e.g. 'filee engines {options.Inputs[0].ToLowerInvariant()} pandoc'.",
            "engines" when options.Inputs.Count > 2 => "Give one engine at a time.",
            "engines" when options.From.Count > 0 && (options.Inputs.Count == 0 || options.Inputs[0].ToLowerInvariant() != "install") =>
                "--from only works with 'filee engines install'.",
            "engines" when options.Inputs.Count > 0 && options.Inputs[0].ToLowerInvariant() == "download" && options.Output is null =>
                "Give the folder to save the files in, e.g. 'filee engines download pandoc -o Engines'.",
            _ => null,
        };
        return options;
    }

    /// <summary>
    /// A length of video in whole seconds: units (<c>90s</c>, <c>10m</c>, <c>1h30m</c>, <c>2.5m</c>) or colons
    /// (<c>10:00</c>, <c>1:30:00</c>). A plain number has no unit and is left to the caller (pages).
    /// </summary>
    internal static bool TryParseLength(string text, out int seconds)
    {
        seconds = 0;
        text = text.Trim().ToLowerInvariant();
        double total = 0;
        if (text.Contains(':'))
        {
            var fields = text.Split(':');
            if (fields.Length > 3)
                return false;
            foreach (var field in fields)
            {
                if (!double.TryParse(field, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    return false;
                total = total * 60 + value;
            }
        }
        else
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"^(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m(?:in)?)?(?:(\d+(?:\.\d+)?)s)?$");
            if (!match.Success || text.Length == 0)
                return false;
            double Group(int i) => match.Groups[i].Success ? double.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            total = Group(1) * 3600 + Group(2) * 60 + Group(3);
        }
        if (!double.IsFinite(total) || total > int.MaxValue)
            return false;
        seconds = (int)Math.Round(total);
        return true;
    }
}
