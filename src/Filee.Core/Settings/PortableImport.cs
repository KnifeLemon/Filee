// Copies the previous profile into an unused portable data folder, leaving the originals untouched.

namespace Filee.Core.Settings;

public static class PortableImport
{
    public sealed class Selection
    {
        public bool Settings { get; set; } = true;
        public bool Presets { get; set; } = true;
        public bool Profiles { get; set; } = true;
        public bool History { get; set; } = true;
        public bool Engines { get; set; } = true;
        public bool Any => Settings || Presets || Profiles || History || Engines;
        internal bool Includes(string file) => file switch
        {
            "settings.json" => Settings,
            "presets.json" => Presets,
            "profiles.json" => Profiles,
            "history.json" => History,
            _ => false,
        };
    }
    private static readonly string[] Files = ["presets.json", "profiles.json", "history.json", "settings.json"];
    public const string DecisionFile = ".portable-import-decided";

    public static bool CanOffer(string source, string destination) =>
        !SamePath(source, destination) && File.Exists(Path.Combine(source, "settings.json"))
        && !File.Exists(Path.Combine(destination, DecisionFile))
        && Files.All(name => !File.Exists(Path.Combine(destination, name)))
        && !Directory.Exists(Path.Combine(destination, "engines"));

    public static void Decline(string destination) => File.WriteAllText(Path.Combine(destination, DecisionFile), "declined");

    public static long? EngineSize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                    if (item is DirectoryInfo) pending.Push(item.FullName);
                    else total = checked(total + ((FileInfo)item).Length);
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { return null; }
    }

    /// <summary>Stages all copies first, refuses links and existing targets, and rolls back a failed commit.</summary>
    public static void Copy(string source, string engines, string destination, IProgress<double>? progress = null, Selection? selection = null)
    {
        selection ??= new Selection();
        if (!selection.Any) throw new ArgumentException("Select data to import.");
        if (!CanOffer(source, destination)) throw new IOException("Portable data is no longer empty.");
        var stage = Path.Combine(destination, ".portable-import-" + Guid.NewGuid().ToString("N"));
        var committed = new List<string>();
        Directory.CreateDirectory(stage);
        try
        {
            if (Files.Any(selection.Includes)) RejectLink(source);
            var copies = new List<(string Source, string Target)>();
            foreach (var name in Files.Where(selection.Includes))
            {
                var file = Path.Combine(source, name);
                if (!File.Exists(file)) continue;
                RejectLink(file);
                using (System.Text.Json.JsonDocument.Parse(File.ReadAllText(file))) { }
                copies.Add((file, Path.Combine(stage, name)));
            }
            if (selection.Engines && Directory.Exists(engines)) CollectDirectory(engines, Path.Combine(stage, "engines"), copies);
            var total = Math.Max(1, copies.Sum(copy => new FileInfo(copy.Source).Length));
            long completed = 0;
            var buffer = new byte[1024 * 1024];
            foreach (var copy in copies)
            {
                using var input = File.OpenRead(copy.Source);
                using var output = new FileStream(copy.Target, FileMode.CreateNew, FileAccess.Write);
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    output.Write(buffer, 0, read);
                    completed += read;
                    progress?.Report(Math.Min(99, 100.0 * completed / total));
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy.Target, File.GetUnixFileMode(copy.Source));
            }
            // Settings last: a failed import must not look like a completed profile on the next launch.
            foreach (var name in new[] { "engines" }.Concat(Files))
            {
                var from = Path.Combine(stage, name);
                var to = Path.Combine(destination, name);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else if (File.Exists(from)) File.Move(from, to);
                else continue;
                committed.Add(to);
            }
            File.WriteAllText(Path.Combine(destination, DecisionFile), "imported");
            progress?.Report(100);
        }
        catch
        {
            foreach (var path in committed.AsEnumerable().Reverse())
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            throw;
        }
        finally { Directory.Delete(stage, true); }
    }

    private static void CollectDirectory(string source, string destination, List<(string Source, string Target)> copies)
    {
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var item in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLink(item);
            var target = Path.Combine(destination, Path.GetFileName(item));
            if (Directory.Exists(item)) CollectDirectory(item, target, copies);
            else copies.Add((item, target));
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Cannot import a symbolic link or junction: {path}");
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
