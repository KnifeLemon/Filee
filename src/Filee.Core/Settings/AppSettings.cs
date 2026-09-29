// Root settings object persisted to settings.json.

namespace Filee.Core.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Look and feel.</summary>
public sealed class ThemeSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.System;

    /// <summary>Accent colour as <c>#RRGGBB</c>.</summary>
    public string Accent { get; set; } = "#7F77DD";

    /// <summary>Base corner radius in pixels. Larger = "softer" UI.</summary>
    public double CornerRadius { get; set; } = 16;

    /// <summary>
    /// Disables animations (for low-end PCs or motion sensitivity).
    /// <c>null</c> follows the operating system setting.
    /// </summary>
    public bool? ReduceMotion { get; set; }

    public ThemeSettings Clone() => (ThemeSettings)MemberwiseClone();
}

/// <summary>Geometry and appearance of the donut toolbar.</summary>
public sealed class DonutSettings
{
    /// <summary>Outer radius in logical pixels.</summary>
    public double OuterRadius { get; set; } = 150;

    /// <summary>Inner (hole) radius as a fraction of the outer radius (0.2 - 0.7).</summary>
    public double HoleRatio { get; set; } = 0.42;

    /// <summary>Background opacity of the slices (0.5 - 1).</summary>
    public double Opacity { get; set; } = 0.96;

    public DonutSettings Clone() => (DonutSettings)MemberwiseClone();
}

/// <summary>All user settings except presets and profiles (which have their own files).</summary>
public sealed class AppSettings
{
    /// <summary>Current schema version. Increase when the format changes and add a migration.</summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary><c>"auto"</c>, <c>"en"</c>, <c>"ko"</c> or <c>"zh-CN"</c>.</summary>
    public string Language { get; set; } = "auto";

    public bool StartWithSystem { get; set; } = true;

    /// <summary>Show the Explorer "Convert with Filee" context menu entry.</summary>
    public bool ContextMenuEnabled { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Newest version the user was already told about, so the update notice shows once per release.</summary>
    public string? NotifiedUpdateVersion { get; set; }

    /// <summary>Temporarily disable all trigger gestures (tray "Pause").</summary>
    public bool Paused { get; set; }

    public ThemeSettings Theme { get; set; } = new();

    public DonutSettings Donut { get; set; } = new();

    public List<TriggerGesture> Triggers { get; set; } = TriggerGesture.Defaults();

    /// <summary>Process names (without .exe) in which gestures never trigger.</summary>
    public List<string> ExcludedProcesses { get; set; } = [];

    /// <summary>Engine ids in preferred order (see <see cref="Conversion.ConverterCatalog.Priority"/>).</summary>
    public List<string> EnginePriority { get; set; } = ["magick", "pdfium", "pdfsharp", "rhwp", "unhwp", "markdown", "hwpx-writer", "word", "pandoc", "libreoffice"];

    /// <summary>User-provided executable paths per engine id, overriding auto-detection.</summary>
    public Dictionary<string, string> EnginePaths { get; set; } = [];

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool FirstRunCompleted { get; set; }
}
