// The "Preset gallery" window (#1530): presets other people shared on filee.sh, as cards with their cover, sorted by
// Hot, Best (of a day, week, month, year or all time) or New and filtered by kind, format, tag, language and text. The
// one chosen shows its pictures, its description and what each of its presets does, and installs as new presets (see
// GalleryInstaller). Presets that need a newer Filee are left out and counted. filee.sh is contacted only while the
// window is open (PresetGalleryClient).

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Gallery;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels;

/// <summary>The orders the gallery offers.</summary>
public enum GallerySort
{
    /// <summary>Likes and downloads of late count most (the site's score).</summary>
    Hot,

    /// <summary>The most liked and downloaded of a period.</summary>
    Best,

    /// <summary>The newest first.</summary>
    New,
}

/// <summary>Where the list is: still loading, shown, or filee.sh couldn't be reached.</summary>
public enum GalleryState
{
    Loading,
    Ready,
    Failed,
}

public sealed partial class PresetGalleryViewModel : ObservableObject, IDisposable
{
    /// <summary>The filter value meaning "any".</summary>
    public const string Any = "";

    /// <summary>Thumbnails loading at the same time.</summary>
    private const int ThumbsAtOnce = 4;

    private readonly PresetGalleryClient _client;
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly Version _appVersion;
    private readonly Func<Uri, Task> _open;
    private readonly Action<GalleryInstallResult>? _installed;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<PresetGalleryCardViewModel> _all = [];
    private bool _refreshing;

    /// <param name="appVersion">This Filee's version: presets that need a newer one are left out.</param>
    /// <param name="open">Opens a page in the browser (the comments).</param>
    /// <param name="installed">Told after an install added presets, so the Presets page lists them.</param>
    public PresetGalleryViewModel(PresetGalleryClient client, UserDataStore store, ILocalizer loc, Version appVersion,
        Func<Uri, Task> open, Action<GalleryInstallResult>? installed = null)
    {
        _client = client;
        _store = store;
        _loc = loc;
        _appVersion = appVersion;
        _open = open;
        _installed = installed;
        Sorts = [.. Enum.GetValues<GallerySort>().Select(s => new Choice<GallerySort>(s, loc[$"gallery.sort.{s}"]))];
        _sort = Sorts[0];
        Periods = [.. GalleryStats.Periods.Select(p => new Choice<string>(p, loc[$"gallery.period.{p}"]))];
        _period = Periods[1];
        Kinds = [new(Any, loc["gallery.kind.all"]), .. SharedPresetFolderCheck.Kinds.Select(k => new Choice<string>(k, loc[$"gallery.kind.{k}"]))];
        _kind = Kinds[0];
        Formats = [new(Any, loc["gallery.format.all"])];
        _format = Formats[0];
        Tags = [new(Any, loc["gallery.tag.all"])];
        _tag = Tags[0];
        Languages = [new(Any, loc["gallery.language.all"])];
        _language = Languages[0];
    }

    /// <summary>This Filee's version, as the update check reads it (a pre-release counts as its release).</summary>
    public static Version CurrentVersion =>
        Version.TryParse(UpdateService.CurrentVersion.Split('-')[0], out var version) ? version : new Version(0, 0, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsFailed), nameof(IsReady), nameof(IsEmpty), nameof(EmptyText))]
    private GalleryState _state = GalleryState.Loading;

    public bool IsLoading => State == GalleryState.Loading;
    public bool IsFailed => State == GalleryState.Failed;
    public bool IsReady => State == GalleryState.Ready;

    /// <summary>The list is shown but nothing is in it (none shared yet, or none matches the filters).</summary>
    public bool IsEmpty => IsReady && Cards.Count == 0;

    public string EmptyText => _all.Count == 0 ? _loc["gallery.none"] : _loc["gallery.empty"];

    /// <summary>The cards that pass the filters, in the chosen order.</summary>
    public ObservableCollection<PresetGalleryCardViewModel> Cards { get; } = [];

    public IReadOnlyList<Choice<GallerySort>> Sorts { get; }
    public IReadOnlyList<Choice<string>> Periods { get; }
    public IReadOnlyList<Choice<string>> Kinds { get; }
    public ObservableCollection<Choice<string>> Formats { get; }
    public ObservableCollection<Choice<string>> Tags { get; }
    public ObservableCollection<Choice<string>> Languages { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeriods))]
    private Choice<GallerySort> _sort;

    /// <summary>The period of Best; the list only shows it with that order.</summary>
    [ObservableProperty] private Choice<string> _period;

    public bool ShowPeriods => Sort.Value == GallerySort.Best;

    [ObservableProperty] private Choice<string> _kind;
    [ObservableProperty] private Choice<string> _format;
    [ObservableProperty] private Choice<string> _tag;
    [ObservableProperty] private Choice<string> _language;
    [ObservableProperty] private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PresetGalleryCardViewModel? _selected;

    /// <summary>What the chosen preset is and does, with Install and Comments.</summary>
    [ObservableProperty] private PresetGalleryDetailViewModel? _detail;

    public bool HasSelection => Selected is not null;

    /// <summary>"3 more need a newer Filee", or null when none does.</summary>
    [ObservableProperty] private string? _newerText;

    partial void OnSortChanged(Choice<GallerySort> value) => Refresh();
    partial void OnPeriodChanged(Choice<string> value) => Refresh();
    partial void OnKindChanged(Choice<string> value) => Refresh();
    partial void OnFormatChanged(Choice<string> value) => Refresh();
    partial void OnTagChanged(Choice<string> value) => Refresh();
    partial void OnLanguageChanged(Choice<string> value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();

    partial void OnSelectedChanged(PresetGalleryCardViewModel? value)
    {
        // The list refills while it filters: the preset stays chosen when it is still in it.
        if (_refreshing)
            return;
        Detail?.Dispose();
        Detail = value is null ? null : new PresetGalleryDetailViewModel(value.Entry, _client, _store, _loc, _open, Installed, _closing.Token);
        _ = Detail?.LoadAsync();
    }

    private void Installed(GalleryInstallResult result) => _installed?.Invoke(result);

    /// <summary>Loads (or loads again) the list from filee.sh.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        State = GalleryState.Loading;
        GalleryIndex index;
        try
        {
            index = await _client.GetIndexAsync(_closing.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (!_closing.IsCancellationRequested)
                State = GalleryState.Failed;
            return;
        }

        foreach (var card in _all)
            card.Dispose();
        _all.Clear();
        var newer = 0;
        foreach (var entry in index.Entries)
        {
            if (entry.MinVersion > _appVersion)
                newer++;
            else
                _all.Add(new PresetGalleryCardViewModel(entry, _loc));
        }
        NewerText = newer == 0 ? null : _loc.Format("gallery.newer", newer);
        Fill(Formats, _all.SelectMany(c => c.Entry.FormatsFrom.Concat(c.Entry.FormatsTo)), id => FormatLabels.NameOf(_loc, id) ?? id.ToUpperInvariant());
        Fill(Tags, _all.SelectMany(c => c.Entry.Tags), tag => tag);
        Fill(Languages, _all.SelectMany(c => c.Entry.Languages), LanguageName);
        State = GalleryState.Ready;
        Refresh();
        _ = LoadThumbsAsync();
    }

    /// <summary>Fills a filter's list after its "any" item with the values the presets have, by their names.</summary>
    private static void Fill(ObservableCollection<Choice<string>> list, IEnumerable<string> values, Func<string, string> label)
    {
        while (list.Count > 1)
            list.RemoveAt(list.Count - 1);
        foreach (var value in values.Distinct(StringComparer.Ordinal).Select(v => new Choice<string>(v, label(v))).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase))
            list.Add(value);
    }

    private static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName == code || culture.NativeName.Length == 0 ? code : culture.NativeName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Applies the filters and the order to the list.</summary>
    private void Refresh()
    {
        if (State != GalleryState.Ready)
            return;
        var query = Search.Trim();
        var cards = _all.Where(c =>
            (Kind.Value == Any || c.Entry.Kinds.Contains(Kind.Value))
            && (Format.Value == Any || c.Entry.FormatsFrom.Contains(Format.Value) || c.Entry.FormatsTo.Contains(Format.Value))
            && (Tag.Value == Any || c.Entry.Tags.Contains(Tag.Value))
            && (Language.Value == Any || c.Entry.Languages.Contains(Language.Value))
            && (query.Length == 0 || c.Matches(query)));
        var sorted = Sort.Value switch
        {
            GallerySort.Hot => cards.OrderByDescending(c => c.Entry.Stats.Hot),
            GallerySort.Best => cards.OrderByDescending(c => c.Entry.Stats.BestOf(Period.Value)).ThenByDescending(c => c.Entry.Stats.Likes),
            _ => cards.OrderByDescending(c => c.Entry.Created),
        };
        var ordered = sorted.ThenByDescending(c => c.Entry.Created).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var selected = Selected;
        _refreshing = true;
        try
        {
            Cards.Clear();
            foreach (var card in ordered)
                Cards.Add(card);
            Selected = selected is not null && ordered.Contains(selected) ? selected : null;
        }
        finally
        {
            _refreshing = false;
        }
        if (Selected != selected)
            OnSelectedChanged(Selected);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Loads the cards' covers in the order they are listed, a few at a time.</summary>
    private async Task LoadThumbsAsync()
    {
        using var gate = new SemaphoreSlim(ThumbsAtOnce);
        var tasks = Cards.ToList().Concat(_all).Distinct().Select(async card =>
        {
            await gate.WaitAsync(_closing.Token);
            try
            {
                await card.LoadThumbAsync(_client, _closing.Token);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        Detail?.Dispose();
        foreach (var card in _all)
            card.Dispose();
        _closing.Dispose();
    }
}

/// <summary>One shared preset in the list: its cover, title, author, likes, downloads and tags.</summary>
public sealed partial class PresetGalleryCardViewModel : ObservableObject, IDisposable
{
    /// <summary>The cover's size in the list, in pixels of its long edge.</summary>
    public const int ThumbEdge = 480;

    private bool _loading;

    public PresetGalleryCardViewModel(GalleryEntry entry, ILocalizer loc)
    {
        Entry = entry;
        Byline = entry.Author.Length == 0 ? "" : loc.Format("gallery.by", entry.Author);
        Likes = loc.Format("gallery.likes", entry.Stats.Likes.ToString("N0", CultureInfo.CurrentCulture));
        Downloads = loc.Format("gallery.downloads", entry.Stats.Downloads.ToString("N0", CultureInfo.CurrentCulture));
        AutomationName = string.Join(", ", new[] { entry.Title, Byline, entry.HasStats ? Likes : "", entry.HasStats ? Downloads : "" }.Where(s => s.Length > 0));
    }

    public GalleryEntry Entry { get; }
    public string Title => Entry.Title;
    public string Byline { get; }
    public IReadOnlyList<string> Tags => Entry.Tags;
    public bool HasStats => Entry.HasStats;
    public string Likes { get; }
    public string Downloads { get; }

    /// <summary>What a screen reader says for the card.</summary>
    public string AutomationName { get; }

    [ObservableProperty] private Bitmap? _thumb;

    /// <summary>True when the query is in the title, description, author or a tag.</summary>
    public bool Matches(string query) =>
        Entry.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Entry.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Entry.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Entry.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Loads the cover's thumbnail (the cover itself when there is none); a failure leaves the placeholder.</summary>
    public async Task LoadThumbAsync(PresetGalleryClient client, CancellationToken cancellationToken)
    {
        if (Thumb is not null || _loading || (Entry.Thumb ?? Entry.Images.FirstOrDefault()) is not { } path)
            return;
        _loading = true;
        try
        {
            var data = await client.GetImageAsync(Entry, path, cancellationToken);
            var bitmap = await Task.Run(() => GalleryBitmaps.Decode(data, ThumbEdge), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                bitmap?.Dispose();
            else
                Thumb = bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or ArgumentException)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    public void Dispose()
    {
        Thumb?.Dispose();
        Thumb = null;
    }
}

/// <summary>A picture of the chosen preset, with what a screen reader says for it.</summary>
public sealed partial class PresetGalleryImageViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private Bitmap? _bitmap;
}

/// <summary>One preset of a shared preset.json: its name, what it does, its main options.</summary>
public sealed record PresetGallerySummary(string Name, string What, string Options)
{
    public bool HasOptions => Options.Length > 0;
}

/// <summary>Whether the chosen preset is being installed, was, or couldn't be.</summary>
public enum GalleryInstallState
{
    Idle,
    Installing,
    Installed,
    Failed,
}

/// <summary>The chosen preset: its pictures, description, what it adds, Install and Comments.</summary>
public sealed partial class PresetGalleryDetailViewModel : ObservableObject, IDisposable
{
    /// <summary>The pictures' size in the window, in pixels of the long edge.</summary>
    public const int ImageEdge = 1280;

    private readonly PresetGalleryClient _client;
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly Func<Uri, Task> _open;
    private readonly Action<GalleryInstallResult> _installed;
    private readonly CancellationTokenSource _cancel;

    internal PresetGalleryDetailViewModel(GalleryEntry entry, PresetGalleryClient client, UserDataStore store, ILocalizer loc,
        Func<Uri, Task> open, Action<GalleryInstallResult> installed, CancellationToken closing)
    {
        Entry = entry;
        _client = client;
        _store = store;
        _loc = loc;
        _open = open;
        _installed = installed;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(closing);
        Byline = entry.Author.Length == 0 ? "" : loc.Format("gallery.by", entry.Author);
        License = loc.Format("gallery.license", entry.Author.Length == 0 ? "?" : entry.Author);
        Stats = entry.HasStats
            ? string.Join(" · ", loc.Format("gallery.likes", entry.Stats.Likes.ToString("N0", CultureInfo.CurrentCulture)),
                loc.Format("gallery.downloads", entry.Stats.Downloads.ToString("N0", CultureInfo.CurrentCulture)))
            : "";
        for (var i = 0; i < entry.Images.Count; i++)
            Images.Add(new PresetGalleryImageViewModel(loc.Format("gallery.image", i + 1, entry.Images.Count)));
        _shownImage = Images.FirstOrDefault();
    }

    public GalleryEntry Entry { get; }
    public string Title => Entry.Title;
    public string Byline { get; }
    public string Description => Entry.Description;
    public IReadOnlyList<string> Tags => Entry.Tags;
    public string Stats { get; }
    public bool HasStats => Stats.Length > 0;

    /// <summary>The CC0 note, with who shared it.</summary>
    public string License { get; }

    public ObservableCollection<PresetGalleryImageViewModel> Images { get; } = [];

    /// <summary>The picture shown large; the others are thumbnails below it.</summary>
    [ObservableProperty] private PresetGalleryImageViewModel? _shownImage;

    public bool HasSeveralImages => Images.Count > 1;

    /// <summary>What each preset in it does.</summary>
    public ObservableCollection<PresetGallerySummary> Presets { get; } = [];

    /// <summary>Which donut it goes on: the one it brings, or none.</summary>
    [ObservableProperty] private string? _donut;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _installable;

    /// <summary>Loading what it does, or why it can't be installed here.</summary>
    [ObservableProperty] private string? _summaryNote;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    private GalleryInstallState _installState;

    public bool IsInstalling => InstallState == GalleryInstallState.Installing;

    /// <summary>What the install did, or why it didn't.</summary>
    [ObservableProperty] private string? _installMessage;

    public bool HasComments => Entry.Discussion is not null;

    /// <summary>Loads the pictures and what the preset does.</summary>
    public async Task LoadAsync()
    {
        SummaryNote = _loc["gallery.loading_details"];
        var token = _cancel.Token;
        await LoadSummaryAsync(token);
        for (var i = 0; i < Entry.Images.Count && !token.IsCancellationRequested; i++)
        {
            try
            {
                var data = await _client.GetImageAsync(Entry, Entry.Images[i], token);
                var bitmap = await Task.Run(() => GalleryBitmaps.Decode(data, ImageEdge), token);
                if (token.IsCancellationRequested)
                    bitmap?.Dispose();
                else
                    Images[i].Bitmap = bitmap;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or ArgumentException)
            {
            }
        }
    }

    private async Task LoadSummaryAsync(CancellationToken token)
    {
        byte[] json;
        try
        {
            json = await _client.GetPresetAsync(Entry, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            SummaryNote = token.IsCancellationRequested ? null : _loc["gallery.download_failed"];
            return;
        }
        if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json)), Entry.Sha256, StringComparison.Ordinal))
        {
            SummaryNote = _loc["gallery.mismatch"];
            return;
        }
        var check = SharedBundleCheck.Check(json);
        if (check.Bundle is not { } bundle)
        {
            SummaryNote = _loc.Format("gallery.not_installable", check.Errors.FirstOrDefault()?.ToString() ?? "");
            return;
        }
        foreach (var preset in bundle.Presets)
            Presets.Add(GallerySummaries.Describe(preset, bundle.Presets, _loc));
        Donut = bundle.Profiles.FirstOrDefault() is { } profile
            ? _loc.Format("gallery.donut_new", _loc.DisplayName(profile),
                profile.Extensions.Count == 0 ? _loc["gallery.donut_mixed"] : string.Join(", ", profile.Extensions.Take(8).Select(e => e.ToUpperInvariant())) + (profile.Extensions.Count > 8 ? " …" : ""))
            : _loc["gallery.donut_none"];
        SummaryNote = null;
        Installable = true;
    }

    private bool CanInstall() => Installable && InstallState is not (GalleryInstallState.Installing or GalleryInstallState.Installed);

    /// <summary>Downloads it through the install link and adds it to the library as new presets.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        InstallState = GalleryInstallState.Installing;
        InstallMessage = _loc["gallery.installing"];
        byte[] json;
        try
        {
            json = await _client.DownloadPresetAsync(Entry, _cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            InstallState = GalleryInstallState.Failed;
            InstallMessage = _loc["gallery.download_failed"];
            return;
        }
        var result = GalleryInstaller.Install(_store, json, Entry.Sha256, _loc.DisplayName, _loc.DisplayName);
        if (!result.Ok)
        {
            InstallState = GalleryInstallState.Failed;
            InstallMessage = result.Errors[0].Code == "install.sha256"
                ? _loc["gallery.mismatch"]
                : _loc.Format("gallery.install_failed", result.Errors[0].ToString());
            return;
        }
        InstallState = GalleryInstallState.Installed;
        InstallMessage = result.Profile is { } donut
            ? _loc.Format("gallery.installed_donut", result.Presets.Count, _loc.DisplayName(donut))
            : _loc.Format("gallery.installed", result.Presets.Count);
        _installed(result);
    }

    /// <summary>Opens the preset's discussion (comments and likes) in the browser.</summary>
    [RelayCommand]
    private Task OpenCommentsAsync() =>
        Entry.Discussion is { } number ? _open(PresetGalleryClient.DiscussionUri(number)) : Task.CompletedTask;

    [RelayCommand]
    private void ShowImage(PresetGalleryImageViewModel image) => ShownImage = image;

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
        foreach (var image in Images)
        {
            image.Bitmap?.Dispose();
            image.Bitmap = null;
        }
    }
}

/// <summary>Turns the gallery's pictures into bitmaps, checking their header before anything decodes them.</summary>
internal static class GalleryBitmaps
{
    /// <summary>
    /// A still PNG, JPEG or WebP of at most <see cref="SharedPresetFolderCheck.MaxLongEdge"/> pixels, scaled to at most
    /// <paramref name="edge"/> on its long edge; null for anything else.
    /// </summary>
    public static Bitmap? Decode(byte[] data, int edge)
    {
        if (GalleryImageHeader.Read(data) is not { Animated: false } info
            || Math.Max(info.Width, info.Height) > SharedPresetFolderCheck.MaxLongEdge)
            return null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return info.Width >= info.Height
                ? Bitmap.DecodeToWidth(stream, Math.Min(edge, info.Width), BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(stream, Math.Min(edge, info.Height), BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return null;
        }
    }
}
