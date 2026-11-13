using Avalonia;
using Avalonia.Media;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace Filee.App.Services.Capture;

public enum CaptureSourceType
{
    Window,
    FullScreen,
    CustomRegion
}

public enum CaptureResolutionPreset
{
    Native,
    Res720p,
    Res1080p,
    Res2K,
    Res4K
}

public enum AnnotationTool
{
    Pointer,
    Pen,
    Arrow,
    Rectangle,
    Circle,
    Blur,
    TextCallout
}

public sealed record WindowTargetInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int X,
    int Y,
    int Width,
    int Height)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? ProcessName
        : $"{ProcessName} - {Title}";

    public bool IsEmpty => Handle == 0 || Width <= 0 || Height <= 0;
}

public sealed record ScreenTargetInfo(
    int Index,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    public string DisplayName => $"{Name} ({Width}×{Height}){(IsPrimary ? " - Primary" : "")}";
}

public sealed class CaptureConfig
{
    public CaptureSourceType SourceType { get; set; } = CaptureSourceType.FullScreen;
    public WindowTargetInfo? TargetWindow { get; set; }
    public ScreenTargetInfo? TargetScreen { get; set; }
    public PixelRect CustomRegion { get; set; }

    public CaptureResolutionPreset Resolution { get; set; } = CaptureResolutionPreset.Res1080p;
    public int Framerate { get; set; } = 30;

    public bool EnableMicrophone { get; set; } = true;
    public string? MicrophoneDevice { get; set; }
    public bool EnableSystemAudio { get; set; } = true;

    public bool HighlightCursor { get; set; } = false;
    public bool SpotlightMode { get; set; } = false;
    public double SpotlightRadius { get; set; } = 120.0;

    public bool CountdownBeforeRecording { get; set; } = true;
}

public sealed class AnnotationStroke
{
    public AnnotationTool Tool { get; set; } = AnnotationTool.Pen;
    public List<Point> Points { get; set; } = [];
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4.0;
    public string? Text { get; set; }
    public Rect Bounds { get; set; }
}

public sealed class SubtitleCueModel
{
    public int Index { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = "";
    public string? TranslatedText { get; set; }

    public string TimeRangeFormatted => $"{Start:mm\\:ss\\.ff} → {End:mm\\:ss\\.ff}";
}

public sealed record RecordedVideoMetadata(
    string VideoPath,
    TimeSpan Duration,
    int Width,
    int Height,
    int Fps,
    bool HasAudio);
