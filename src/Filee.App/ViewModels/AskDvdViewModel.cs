// "Choose the DVD title": the titles of a DVD-Video disc image dropped on a video or audio preset (AskedDvd), the
// longest chosen to start with, and the audio and subtitle tracks of the title chosen, all ticked to start with. The
// title list can be typed in: a number picks that title. The tracks are read with ffprobe each time the title changes.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.App.ViewModels;

/// <summary>A title of the disc in the list: its number, length and chapters.</summary>
public sealed record DvdTitleChoice(int Number, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An audio or subtitle track of the title chosen, ticked to keep it.</summary>
public sealed partial class DvdTrackChoice(int id, string label) : ObservableObject
{
    public int Id => id;
    public string Label => label;

    [ObservableProperty]
    private bool _keep = true;
}

public sealed partial class AskDvdViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizer _loc;
    private readonly Func<int, CancellationToken, Task<DvdVideoIso.Tracks>> _readTracks;
    private CancellationTokenSource? _reading;

    /// <param name="path">The disc image.</param>
    /// <param name="titles">Its titles (DvdVideoIso.TitlesOf), at least one.</param>
    /// <param name="readTracks">The tracks of a title (DvdVideoIso.TracksAsync); a test gives its own.</param>
    /// <param name="target">The preset's target: a video keeps subtitles, and only MKV keeps a DVD's, which are pictures.</param>
    /// <param name="index">Which of the discs dropped together this is, from 0.</param>
    /// <param name="total">How many discs were dropped together.</param>
    internal AskDvdViewModel(string path, IReadOnlyList<DvdVideoIso.Title> titles,
        Func<int, CancellationToken, Task<DvdVideoIso.Tracks>> readTracks, ILocalizer loc, string target = "mkv", int index = 0, int total = 1)
    {
        _loc = loc;
        ShowsSubtitles = FormatRegistry.FindById(target)?.Category == FormatCategory.Video;
        KeepsSubtitles = target == "mkv";
        _readTracks = readTracks;
        FileName = Path.GetFileName(path);
        Position = total > 1 ? loc.Format("pages.file_of", index + 1, total) : "";
        Titles = [.. titles.Select(t => new DvdTitleChoice(t.Number, loc.Format("dvd.title_item", t.Number, Length(t.Length), t.Chapters)))];
        var longest = DvdVideoIso.Longest(titles).Number;
        SummaryText = loc.Format("dvd.titles_found", titles.Count);
        _selectedTitle = Titles.First(t => t.Number == longest);
        _titleText = _selectedTitle.Label;
        _ = ReadTracksAsync(longest);
    }

    public string FileName { get; }

    /// <summary>"File 2 of 3" when several discs were dropped together; empty for one.</summary>
    public string Position { get; }
    public bool ShowsPosition => Position.Length > 0;

    /// <summary>"12 titles": under the file name.</summary>
    public string SummaryText { get; }

    public IReadOnlyList<DvdTitleChoice> Titles { get; }

    [ObservableProperty]
    private DvdTitleChoice? _selectedTitle;

    /// <summary>
    /// The text of the title box: a title's label when one is picked from the list, or what the user types. A number in
    /// it picks that title.
    /// </summary>
    [ObservableProperty]
    private string _titleText;

    /// <summary>A video target: the subtitle column shows. An audio target has none to keep.</summary>
    public bool ShowsSubtitles { get; }

    /// <summary>MKV: a DVD's subtitles are pictures, which the other video targets can't hold (they are left out).</summary>
    public bool KeepsSubtitles { get; }

    /// <summary>The audio card's column span: both columns when the subtitle card is hidden (an audio target).</summary>
    public static readonly IValueConverter SpanWhenAlone = new FuncValueConverter<bool, int>(showsSubtitles => showsSubtitles ? 1 : 2);

    /// <summary>The hint under the subtitles when the target can't keep them.</summary>
    public bool ShowsMkvHint => ShowsSubtitles && !KeepsSubtitles;

    public ObservableCollection<DvdTrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<DvdTrackChoice> SubtitleTracks { get; } = [];

    /// <summary>The tracks of the title chosen are being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(ShowsNoAudio), nameof(ShowsNoSubtitles))]
    private bool _isReading;

    /// <summary>What went wrong reading the tracks, or what is typed that names no title; empty when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string _status = "";

    public bool ShowsNoAudio => !IsReading && AudioTracks.Count == 0;
    public bool ShowsNoSubtitles => !IsReading && SubtitleTracks.Count == 0;

    /// <summary>A title is chosen and its tracks are read: Start converts it.</summary>
    public bool IsValid => SelectedTitle is not null && !IsReading && Status.Length == 0;

    partial void OnSelectedTitleChanged(DvdTitleChoice? value)
    {
        if (value is null)
            return;
        if (TitleText != value.Label)
            TitleText = value.Label;
        _ = ReadTracksAsync(value.Number);
    }

    partial void OnTitleTextChanged(string value)
    {
        if (SelectedTitle is { } selected && value == selected.Label)
            return;
        // Typed: the first number is the title ("3", "Title 3", "title 3 …").
        var match = Regex.Match(value ?? "", @"\d+");
        var number = match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        if (Titles.FirstOrDefault(t => t.Number == number) is { } title)
        {
            Status = "";
            if (title != SelectedTitle)
                SelectedTitle = title;
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            SelectedTitle = null;
            Status = _loc.Format("dvd.no_such_title", Titles.Max(t => t.Number));
        }
    }

    private async Task ReadTracksAsync(int title)
    {
        _reading?.Cancel();
        _reading?.Dispose();
        var reading = _reading = new CancellationTokenSource();
        IsReading = true;
        Status = "";
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        try
        {
            var tracks = await _readTracks(title, reading.Token);
            if (reading.IsCancellationRequested)
                return;
            foreach (var audio in tracks.Audio)
                AudioTracks.Add(new DvdTrackChoice(audio.Id, TrackLabel(audio)));
            foreach (var subtitle in tracks.Subtitles)
                SubtitleTracks.Add(new DvdTrackChoice(subtitle.Id, TrackLabel(subtitle)) { Keep = KeepsSubtitles });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            if (!reading.IsCancellationRequested)
                Status = _loc["dvd.tracks_failed"];
        }
        if (!reading.IsCancellationRequested)
            IsReading = false;
    }

    /// <summary>"Français · AC-3 5.1", "English · DVD subtitles": the language by its own name, then the format.</summary>
    internal string TrackLabel(DvdVideoIso.Track track)
    {
        var language = LanguageName(track.Language) ?? _loc["dvd.unknown_language"];
        var format = track.Codec switch
        {
            "ac3" => "AC-3",
            "eac3" => "E-AC-3",
            "dts" => "DTS",
            "mp2" => "MPEG",
            "pcm_dvd" or "pcm_s16be" or "pcm_s24be" => "LPCM",
            "dvd_subtitle" => null,
            { } other => other.ToUpperInvariant(),
            null => null,
        };
        var channels = track.Channels switch
        {
            <= 0 => null,
            1 => "1.0",
            2 => "2.0",
            >= 6 => $"{track.Channels - 1}.1",
            var c => $"{c}.0",
        };
        var detail = string.Join(' ', new[] { format, channels }.Where(s => s is not null));
        return detail.Length > 0 ? $"{language} · {detail}" : language;
    }

    /// <summary>
    /// A DVD's language code (ISO 639-2, the bibliographic "fre" or the terminology "fra") as the language calls itself
    /// ("français"), first letter capitalized; null for none or one .NET doesn't know.
    /// </summary>
    internal static string? LanguageName(string? code)
    {
        if (string.IsNullOrEmpty(code) || code is "und" or "unk")
            return null;
        code = code.ToLowerInvariant();
        code = Bibliographic.TryGetValue(code, out var terminology) ? terminology : code;
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.ThreeLetterISOLanguageName == code || c.TwoLetterISOLanguageName == code);
        if (culture is null || culture.Equals(CultureInfo.InvariantCulture))
            return code;
        var name = culture.NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : code;
    }

    /// <summary>ISO 639-2 codes whose bibliographic form (what DVDs and FFmpeg use) differs from .NET's terminology one.</summary>
    private static readonly Dictionary<string, string> Bibliographic = new()
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "zho",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["mao"] = "mri",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym",
    };

    private static string Length(TimeSpan length) =>
        length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : length.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The title and the tracks kept; null when no title is chosen or its tracks aren't read yet.</summary>
    public DvdChoice? Result() => IsValid && SelectedTitle is { } title
        ? new DvdChoice(title.Number, [.. AudioTracks.Where(t => t.Keep).Select(t => t.Id)], [.. SubtitleTracks.Where(t => t.Keep).Select(t => t.Id)])
        : null;

    public void Dispose()
    {
        _reading?.Cancel();
        _reading?.Dispose();
        _reading = null;
    }
}
