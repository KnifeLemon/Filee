// The "Preset gallery" window (#1530) against a stand-in for filee.sh: an HTTP handler that serves an index.json, the
// pictures (drawn with SkiaSharp here) and each preset.json, all made in the test. It checks the sorting and filters,
// that presets for a newer Filee are left out, that installing adds new presets through the counted download link and
// refuses a file that isn't the listed one, the fallback to the repository, the size limits and the picture cache, and
// renders the window in light and dark for the pull request.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Gallery;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using SkiaSharp;

namespace Filee.App.Tests;

public sealed class PresetGalleryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-gallery-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>filee.sh and the gallery's repository, answered from memory; every request is recorded.</summary>
    private sealed class FakeGallery : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<Uri> Requests { get; } = [];
        public bool SiteDown { get; set; }
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
                Requests.Add(uri);
            if (Offline || (SiteDown && uri.Host == "filee.sh"))
                throw new HttpRequestException("No route to host.");
            Assert.True(uri.Scheme == "https" && uri.Host is "filee.sh" or "raw.githubusercontent.com", uri.ToString());
            var key = uri.Host == "filee.sh" ? uri.AbsolutePath : "repo:" + uri.AbsolutePath["/KnifeLemon/Filee-Presets/main".Length..];
            return Task.FromResult(Files.TryGetValue(key, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        public int Count(string path)
        {
            lock (Requests)
                return Requests.Count(r => r.AbsolutePath == path);
        }
    }

    private static byte[] Picture(int width, int height, SKColor sky, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(sky);
            using var sun = new SKPaint { Color = SKColors.Gold, IsAntialias = true };
            canvas.DrawCircle(width * 0.7f, height * 0.4f, Math.Min(width, height) / 5f, sun);
            using var hill = new SKPaint { Color = new SKColor(60, 140, 90), IsAntialias = true };
            canvas.DrawOval(width * 0.3f, height * 1.05f, width * 0.6f, height * 0.4f, hill);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 85);
        return encoded.ToArray();
    }

    /// <summary>A preset.json as Settings → General → Export writes it, with <paramref name="presets"/> and a donut.</summary>
    private byte[] Bundle(IEnumerable<Preset> presets, ToolbarProfile? profile = null)
    {
        var data = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new UserDataStore(data);
        store.Load();
        store.Presets.Clear();
        store.Presets.AddRange(presets);
        store.Profiles.Clear();
        if (profile is not null)
            store.Profiles.Add(profile);
        var path = Path.Combine(data, "bundle.json");
        store.Export(path);
        return File.ReadAllBytes(path);
    }

    private static readonly DateTimeOffset Today = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Adds a shared preset to the gallery: its files, and its entry for index.json.</summary>
    private JsonObject Share(FakeGallery gallery, string slug, string title, byte[] preset, SKColor sky, string[] kinds, string[] to,
        string[] tags, string[] languages, string minVersion = "2.0.0", int days = 0, double hot = 0, int likes = 0, int downloads = 0,
        int images = 2, int? discussion = 7)
    {
        var folder = $"presets/{slug}/";
        gallery.Files["/presets/files/" + folder + "preset.json"] = preset;
        gallery.Files["repo:/" + folder + "preset.json"] = preset;
        gallery.Files["/presets/download/" + slug] = preset;
        var paths = new JsonArray();
        for (var i = 1; i <= images; i++)
        {
            var picture = Picture(1280, 720, i == 1 ? sky : SKColors.MediumPurple);
            gallery.Files[$"/presets/files/{folder}images/{i}.webp"] = picture;
            gallery.Files[$"repo:/{folder}images/{i}.webp"] = picture;
            paths.Add($"{folder}images/{i}.webp");
        }
        var thumb = Picture(480, 270, sky, SKEncodedImageFormat.Webp);
        gallery.Files[$"/presets/files/{folder}images/1-thumb.webp"] = thumb;
        gallery.Files[$"repo:/{folder}images/1-thumb.webp"] = thumb;
        return new JsonObject
        {
            ["id"] = slug,
            ["title"] = title,
            ["description"] = $"{title}: made for the gallery tests, with a description long enough to wrap onto a second line in the window.",
            ["author"] = "tester-" + slug[..3],
            ["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)t)]),
            ["languages"] = new JsonArray([.. languages.Select(l => (JsonNode)l)]),
            ["kinds"] = new JsonArray([.. kinds.Select(k => (JsonNode)k)]),
            ["formats"] = new JsonObject { ["from"] = new JsonArray(), ["to"] = new JsonArray([.. to.Select(t => (JsonNode)t)]) },
            ["minVersion"] = minVersion,
            ["created"] = Today.AddDays(-days).ToString("o"),
            ["updated"] = Today.AddDays(-days).ToString("o"),
            ["discussion"] = discussion,
            ["license"] = "CC0-1.0",
            ["images"] = paths,
            ["thumb"] = $"{folder}images/1-thumb.webp",
            ["preset"] = folder + "preset.json",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(preset)),
            ["stats"] = new JsonObject
            {
                ["likes"] = likes,
                ["comments"] = 1,
                ["downloads"] = downloads,
                ["hot"] = hot,
                ["best"] = new JsonObject { ["day"] = 0, ["week"] = likes, ["month"] = likes, ["year"] = likes + 1, ["all"] = likes + downloads },
            },
        };
    }

    /// <summary>
    /// Publishes the entries as the repository's index.json (paths presets/&lt;id&gt;/…) and as filee.sh's, whose paths
    /// are relative to https://filee.sh/presets/index.json (files/presets/&lt;id&gt;/…) and which has an install link.
    /// </summary>
    private static void Publish(FakeGallery gallery, params JsonObject[] entries)
    {
        static byte[] Index(IEnumerable<JsonObject> list) => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = 1,
            ["generated"] = Today.ToString("o"),
            ["presets"] = new JsonArray([.. list]),
        }.ToJsonString());
        gallery.Files["repo:/index.json"] = Index(entries.Select(e => e.DeepClone().AsObject()));
        gallery.Files["/presets/index.json"] = Index(entries.Select(e =>
        {
            var site = e.DeepClone().AsObject();
            site["images"] = new JsonArray([.. site["images"]!.AsArray().Select(p => (JsonNode)("files/" + p!.GetValue<string>()))]);
            site["thumb"] = "files/" + site["thumb"]!.GetValue<string>();
            site["preset"] = "files/" + site["preset"]!.GetValue<string>();
            site["download"] = "https://filee.sh/presets/download/" + site["id"]!.GetValue<string>();
            if (site["imagesByLang"] is JsonObject byLang)
                foreach (var (language, list) in byLang.ToList())
                    byLang[language] = new JsonArray([.. list!.AsArray().Select(p => (JsonNode)("files/" + p!.GetValue<string>()))]);
            if (site["thumbByLang"] is JsonObject thumbs)
                foreach (var (language, path) in thumbs.ToList())
                    thumbs[language] = "files/" + path!.GetValue<string>();
            return site;
        }));
    }

    private static Preset Insta() => new()
    {
        Id = "insta",
        Name = "Instagram JPG",
        TargetFormat = "jpg",
        Image = { Quality = 85, Resize = ResizeMode.LongEdge, LongEdge = 1080, KeepMetadata = false },
        Output = { FileNamePattern = "{name}_insta" },
    };

    /// <summary>A gallery of four shared presets, one of them for a newer Filee.</summary>
    private FakeGallery Gallery()
    {
        var gallery = new FakeGallery();
        var scans = new Preset { Id = "scans", Name = "Scans to one PDF", TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, Compression = PdfCompression.Ebook } };
        var left = new Preset { Id = "left", Name = "Turn left", TargetFormat = BuiltInData.SameAsSource, Image = { Rotation = ImageRotation.Left90 } };
        var right = new Preset { Id = "right", Name = "Turn right", TargetFormat = BuiltInData.SameAsSource, Image = { Rotation = ImageRotation.Right90 } };
        var turn = new Preset { Id = "turn", Name = "Turn", Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, SubPresetIds = ["left", "right"] };
        var donut = new ToolbarProfile { Id = "photos", Name = "Photo desk", Extensions = ["jpg", "png", "heic"], PresetIds = ["turn", "left", "right"], BackPresetIds = [] };
        Publish(gallery,
            Share(gallery, "instagram-jpg", "Instagram-ready JPG", Bundle([Insta()]), SKColors.SkyBlue, ["convert"], ["jpg"], ["images", "social"], ["en"],
                days: 3, hot: 9, likes: 12, downloads: 340),
            Share(gallery, "scan-merge", "Scans to one small PDF", Bundle([scans]), SKColors.LightSalmon, ["convert"], ["pdf"], ["pdf", "office"], ["en", "ko"],
                days: 1, hot: 4, likes: 30, downloads: 90),
            Share(gallery, "photo-desk", "Photo desk donut", Bundle([turn, left, right], donut), SKColors.MediumAquamarine, ["convert", "toolbar"], [],
                ["images"], ["ko"], days: 10, hot: 2, likes: 5, downloads: 20),
            Share(gallery, "from-the-future", "Needs a newer Filee", Bundle([Insta()]), SKColors.Gray, ["convert"], ["jpg"], ["images"], ["en"],
                minVersion: "99.0.0"));
        return gallery;
    }

    private UserDataStore Store()
    {
        var store = new UserDataStore(Path.Combine(_root, "library-" + Guid.NewGuid().ToString("N")[..6]));
        store.Load();
        return store;
    }

    private (PresetGalleryViewModel Vm, PresetGalleryClient Client, List<Uri> Opened, List<GalleryInstallResult> Installed) Open(
        FakeGallery gallery, UserDataStore store, string? cache = "")
    {
        var client = new PresetGalleryClient(gallery, cache);
        var opened = new List<Uri>();
        var installed = new List<GalleryInstallResult>();
        var vm = new PresetGalleryViewModel(client, store, AppHost.Get<ILocalizer>(), new Version(2, 5, 0),
            uri => { opened.Add(uri); return Task.CompletedTask; }, installed.Add);
        _ = vm.LoadAsync();
        ClipWindowTests.WaitFor(() => vm.State != GalleryState.Loading);
        return (vm, client, opened, installed);
    }

    [AvaloniaFact]
    public void The_list_leaves_out_presets_for_a_newer_filee_and_sorts_and_filters()
    {
        TestServices.EnsureInitialized();
        var (vm, client, _, _) = Open(Gallery(), Store());
        using var _client = client;
        using var _vm = vm;

        Assert.Equal(GalleryState.Ready, vm.State);
        Assert.Equal(["Instagram-ready JPG", "Scans to one small PDF", "Photo desk donut"], vm.Cards.Select(c => c.Title));
        Assert.Contains("1", vm.NewerText);

        vm.Sort = vm.Sorts.Single(s => s.Value == GallerySort.Best);
        Assert.True(vm.ShowPeriods);
        vm.Period = vm.Periods.Single(p => p.Value == "all");
        Assert.Equal("Instagram-ready JPG", vm.Cards[0].Title); // 12 + 340
        vm.Period = vm.Periods.Single(p => p.Value == "week");
        Assert.Equal("Scans to one small PDF", vm.Cards[0].Title); // 30 likes this week
        vm.Sort = vm.Sorts.Single(s => s.Value == GallerySort.New);
        Assert.False(vm.ShowPeriods);
        Assert.Equal(["Scans to one small PDF", "Instagram-ready JPG", "Photo desk donut"], vm.Cards.Select(c => c.Title));

        vm.Kind = vm.Kinds.Single(k => k.Value == "toolbar");
        Assert.Equal(["Photo desk donut"], vm.Cards.Select(c => c.Title));
        vm.Kind = vm.Kinds[0];
        vm.Format = vm.Formats.Single(f => f.Value == "pdf");
        Assert.Equal(["Scans to one small PDF"], vm.Cards.Select(c => c.Title));
        vm.Format = vm.Formats[0];
        // The Korean donut isn't written in English: shown, after the others, unless only English ones are wanted.
        vm.OnlyMyLanguage = true;
        Assert.Equal(["Scans to one small PDF", "Instagram-ready JPG"], vm.Cards.Select(c => c.Title));
        vm.Tag = vm.Tags.Single(t => t.Value == "images");
        Assert.Equal(["Instagram-ready JPG"], vm.Cards.Select(c => c.Title));
        vm.Tag = vm.Tags[0];
        vm.OnlyMyLanguage = false;
        vm.Search = "tester-sca";
        Assert.Equal(["Scans to one small PDF"], vm.Cards.Select(c => c.Title));
        vm.Search = "nothing like this";
        Assert.True(vm.IsEmpty);
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.empty"], vm.EmptyText);
    }

    [AvaloniaFact]
    public void Covers_load_into_the_cards()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        using var _vm = vm;

        ClipWindowTests.WaitFor(() => vm.Cards.All(c => c.Thumb is not null));
        Assert.Equal(480, vm.Cards[0].Thumb!.PixelSize.Width);
        Assert.Equal(1, gallery.Count("/presets/files/presets/instagram-jpg/images/1-thumb.webp"));
        Assert.Equal(0, gallery.Count("/presets/files/presets/from-the-future/images/1-thumb.webp"));
        Assert.Contains("Instagram-ready JPG", vm.Cards[0].AutomationName);
        Assert.Contains("tester-ins", vm.Cards[0].AutomationName);
    }

    [AvaloniaFact]
    public void The_chosen_preset_shows_what_it_adds_and_installs_as_new_presets_through_the_counted_link()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        var store = Store();
        var ours = store.Presets.Count;
        var (vm, client, opened, installed) = Open(gallery, store);
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single(c => c.Entry.Id == "photo-desk");
        var detail = vm.Detail!;
        ClipWindowTests.WaitFor(() => detail.Installable && detail.Images.All(i => i.Bitmap is not null));
        Assert.Equal(["Turn", "Turn left", "Turn right"], detail.Presets.Select(p => p.Name));
        Assert.Contains("Turn left", detail.Presets[0].What);
        Assert.Contains("Photo desk", detail.Donut);
        Assert.Contains("JPG", detail.Donut);
        Assert.Equal(0, gallery.Count("/presets/download/photo-desk")); // looking isn't downloading

        detail.InstallCommand.Execute(null);
        ClipWindowTests.WaitFor(() => detail.InstallState != GalleryInstallState.Installing);

        Assert.Equal(GalleryInstallState.Installed, detail.InstallState);
        Assert.Equal(1, gallery.Count("/presets/download/photo-desk"));
        Assert.Equal(ours + 3, store.Presets.Count);
        var result = Assert.Single(installed);
        Assert.DoesNotContain(result.Presets, p => p.Id is "turn" or "left" or "right");
        Assert.Same(result.Profile, store.Profiles[^1]);
        Assert.False(detail.InstallCommand.CanExecute(null)); // once

        detail.OpenCommentsCommand.Execute(null);
        Assert.Equal(new Uri("https://github.com/KnifeLemon/Filee-Presets/discussions/7"), Assert.Single(opened));
    }

    [AvaloniaFact]
    public void A_preset_described_in_the_detail_lists_its_target_and_options()
    {
        TestServices.EnsureInitialized();
        var (vm, client, _, _) = Open(Gallery(), Store());
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single(c => c.Entry.Id == "instagram-jpg");
        ClipWindowTests.WaitFor(() => vm.Detail!.Installable);
        var summary = Assert.Single(vm.Detail!.Presets);
        Assert.Equal("Instagram JPG", summary.Name);
        Assert.Contains("JPG", summary.What);
        Assert.Contains("85", summary.Options);
        Assert.Contains("1080", summary.Options);
        Assert.Contains("{name}_insta", summary.Options);
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.donut_none"], vm.Detail.Donut);
    }

    [AvaloniaFact]
    public void A_download_that_isnt_the_listed_file_installs_nothing()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        var store = Store();
        var before = store.Presets.Count;
        // The counted link answers with another file than the one index.json lists (and the detail view checked).
        var other = Insta();
        other.Image.Quality = 5;
        gallery.Files["/presets/download/instagram-jpg"] = Bundle([other]);
        var (vm, client, _, installed) = Open(gallery, store);
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single(c => c.Entry.Id == "instagram-jpg");
        ClipWindowTests.WaitFor(() => vm.Detail!.Installable);
        vm.Detail!.InstallCommand.Execute(null);
        ClipWindowTests.WaitFor(() => vm.Detail.InstallState != GalleryInstallState.Installing);

        Assert.Equal(GalleryInstallState.Failed, vm.Detail.InstallState);
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.mismatch"], vm.Detail.InstallMessage);
        Assert.Equal(before, store.Presets.Count);
        Assert.Empty(installed);
    }

    [AvaloniaFact]
    public void A_listed_preset_the_rules_refuse_cant_be_installed()
    {
        TestServices.EnsureInitialized();
        var gallery = new FakeGallery();
        var bad = Insta();
        bad.Output.Location = OutputLocation.CustomFolder;
        bad.Output.CustomFolder = @"C:\Users\Public";
        Publish(gallery, Share(gallery, "bad-one", "Bad one", Bundle([bad]), SKColors.Red, ["convert"], ["jpg"], [], ["en"]));
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single();
        ClipWindowTests.WaitFor(() => vm.Detail!.SummaryNote != AppHost.Get<ILocalizer>()["gallery.loading_details"]);
        Assert.False(vm.Detail!.Installable);
        Assert.False(vm.Detail.InstallCommand.CanExecute(null));
        Assert.Contains("presets[0].output.", vm.Detail.SummaryNote);
    }

    [AvaloniaFact]
    public void Without_filee_sh_the_repository_is_read_and_without_a_network_retry_works_later()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        gallery.SiteDown = true;
        var store = Store();
        var (vm, client, _, _) = Open(gallery, store);
        using var _client = client;
        using var _vm = vm;

        Assert.Equal(GalleryState.Ready, vm.State);
        Assert.True(client.FromRepository);
        ClipWindowTests.WaitFor(() => vm.Cards.All(c => c.Thumb is not null));
        vm.Selected = vm.Cards.Single(c => c.Entry.Id == "instagram-jpg");
        ClipWindowTests.WaitFor(() => vm.Detail!.Installable);
        vm.Detail!.InstallCommand.Execute(null);
        ClipWindowTests.WaitFor(() => vm.Detail.InstallState == GalleryInstallState.Installed);
        // Every file came from the repository, its preset.json in place of the counted link.
        Assert.Equal(2, gallery.Requests.Count(r => r.AbsolutePath == "/KnifeLemon/Filee-Presets/main/presets/instagram-jpg/preset.json"));
        Assert.DoesNotContain(gallery.Requests, r => r.Host == "filee.sh" && r.AbsolutePath != "/presets/index.json");

        gallery.Offline = true;
        var (offline, offlineClient, _, _) = Open(gallery, store);
        using var _offlineClient = offlineClient;
        using var _offline = offline;
        Assert.Equal(GalleryState.Failed, offline.State);
        Assert.True(offline.IsFailed);
        gallery.Offline = false;
        gallery.SiteDown = false;
        offline.LoadCommand.Execute(null);
        ClipWindowTests.WaitFor(() => offline.State == GalleryState.Ready);
        Assert.Equal(3, offline.Cards.Count);
    }

    [AvaloniaFact]
    public void Answers_larger_than_their_limit_are_refused()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        gallery.Files["/presets/files/presets/instagram-jpg/images/1-thumb.webp"] = new byte[PresetGalleryClient.MaxThumbBytes + 1];
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        using var _vm = vm;
        ClipWindowTests.WaitFor(() => vm.Cards.Where(c => c.Entry.Id != "instagram-jpg").All(c => c.Thumb is not null));
        Assert.Null(vm.Cards.Single(c => c.Entry.Id == "instagram-jpg").Thumb);

        gallery.Files["/presets/index.json"] = new byte[GalleryIndex.MaxBytes + 1];
        gallery.Files["repo:/index.json"] = new byte[GalleryIndex.MaxBytes + 1];
        var (big, bigClient, _, _) = Open(gallery, Store());
        using var _bigClient = bigClient;
        using var _big = big;
        Assert.Equal(GalleryState.Failed, big.State);
    }

    [AvaloniaFact]
    public void Pictures_are_kept_in_the_cache_for_the_next_time()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        var cache = Path.Combine(_root, "cache");
        var path = "/presets/files/presets/scan-merge/images/1-thumb.webp";
        var (first, firstClient, _, _) = Open(gallery, Store(), cache);
        ClipWindowTests.WaitFor(() => first.Cards.All(c => c.Thumb is not null));
        first.Dispose();
        firstClient.Dispose();
        Assert.Equal(1, gallery.Count(path));
        Assert.NotEmpty(Directory.GetFiles(cache, "*.img"));

        var (second, secondClient, _, _) = Open(gallery, Store(), cache);
        using var _secondClient = secondClient;
        using var _second = second;
        ClipWindowTests.WaitFor(() => second.Cards.All(c => c.Thumb is not null));
        Assert.Equal(1, gallery.Count(path));
    }

    /// <summary>A gallery of one preset that replaces the originals (a high warning) and strips metadata.</summary>
    private FakeGallery RiskyGallery()
    {
        var gallery = new FakeGallery();
        var risky = Insta();
        risky.Output.ReplaceOriginal = true;
        Publish(gallery, Share(gallery, "in-place-jpg", "JPG in place", Bundle([risky]), SKColors.Tomato, ["convert"], ["jpg"], ["images"], ["en"]));
        return gallery;
    }

    [AvaloniaFact]
    public void A_preset_that_can_replace_originals_warns_and_installs_only_once_the_user_agrees()
    {
        TestServices.EnsureInitialized();
        var store = Store();
        var (vm, client, _, installed) = Open(RiskyGallery(), store);
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single();
        var detail = vm.Detail!;
        ClipWindowTests.WaitFor(() => detail.Installable);
        var loc = AppHost.Get<ILocalizer>();
        Assert.Equal([loc["gallery.warning.replaces_original"], loc["gallery.warning.strips_metadata"]], detail.Warnings.Select(w => w.Text));
        Assert.True(detail.HasHighWarnings);
        Assert.False(detail.InstallCommand.CanExecute(null));

        detail.WarningsAccepted = true;
        Assert.True(detail.InstallCommand.CanExecute(null));
        detail.InstallCommand.Execute(null);
        ClipWindowTests.WaitFor(() => detail.InstallState == GalleryInstallState.Installed);

        // Kept as shared, and marked as from the gallery.
        var preset = Assert.Single(Assert.Single(installed).Presets);
        Assert.True(preset.Output.ReplaceOriginal);
        Assert.Equal("in-place-jpg", preset.Gallery);
        var editor = new PresetEditorViewModel(preset, loc);
        Assert.Contains("in-place-jpg", editor.GalleryNote);
        Assert.Null(new PresetEditorViewModel(Insta(), loc).GalleryNote);
    }

    [AvaloniaFact]
    public void A_preset_with_only_milder_warnings_installs_without_the_tick()
    {
        TestServices.EnsureInitialized();
        var (vm, client, _, _) = Open(Gallery(), Store());
        using var _client = client;
        using var _vm = vm;

        vm.Selected = vm.Cards.Single(c => c.Entry.Id == "instagram-jpg");
        ClipWindowTests.WaitFor(() => vm.Detail!.Installable);
        Assert.True(vm.Detail!.HasWarnings); // it removes metadata
        Assert.False(vm.Detail.HasHighWarnings);
        Assert.True(vm.Detail.InstallCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Pictures_follow_the_apps_language_and_change_with_it()
    {
        TestServices.EnsureInitialized("en");
        var gallery = Gallery();
        var entry = Share(gallery, "korean-shots", "Korean screenshots", Bundle([Insta()]), SKColors.SkyBlue, ["convert"], ["jpg"], [], ["ko"]);
        var korean = Picture(1280, 720, SKColors.HotPink);
        var koreanThumb = Picture(480, 270, SKColors.HotPink, SKEncodedImageFormat.Webp);
        gallery.Files["/presets/files/presets/korean-shots/images/ko/1.webp"] = korean;
        gallery.Files["/presets/files/presets/korean-shots/images/ko/1-thumb.webp"] = koreanThumb;
        entry["imagesByLang"] = new JsonObject { ["ko"] = new JsonArray("presets/korean-shots/images/ko/1.webp", "presets/korean-shots/images/2.webp") };
        entry["thumbByLang"] = new JsonObject { ["ko"] = "presets/korean-shots/images/ko/1-thumb.webp" };
        Publish(gallery, entry);
        var localization = AppHost.Get<LocalizationService>();
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        try
        {
            var card = vm.Cards.Single();
            ClipWindowTests.WaitFor(() => card.Thumb is not null);
            Assert.Equal(0, gallery.Count("/presets/files/presets/korean-shots/images/ko/1-thumb.webp"));
            vm.Selected = card;
            ClipWindowTests.WaitFor(() => vm.Detail!.Images.All(i => i.Bitmap is not null));
            var english = card.Thumb;

            localization.SetLanguage("ko");
            ClipWindowTests.WaitFor(() => card.Thumb != english && vm.Detail!.Images.All(i => i.Bitmap is not null));
            Assert.Equal(1, gallery.Count("/presets/files/presets/korean-shots/images/ko/1-thumb.webp"));
            Assert.Equal(1, gallery.Count("/presets/files/presets/korean-shots/images/ko/1.webp"));
            Assert.Equal(2, vm.Detail!.Images.Count);
            Assert.Equal(AppHost.Get<ILocalizer>().Format("gallery.image", 1, 2), vm.Detail.Images[0].Name);
        }
        finally
        {
            vm.Dispose();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void Presets_in_the_apps_language_come_first_in_it_and_the_others_carry_their_language()
    {
        TestServices.EnsureInitialized("en");
        var gallery = new FakeGallery();
        var korean = Share(gallery, "korean-first", "한국어 프리셋", Bundle([Insta()]), SKColors.SkyBlue, ["convert"], ["jpg"], [], [], hot: 9);
        korean["language"] = "ko";
        korean["titleByLang"] = new JsonObject { ["en"] = "Korean preset in English" };
        korean["descriptionByLang"] = new JsonObject { ["en"] = "An English description of the Korean preset." };
        var japanese = Share(gallery, "japanese-only", "日本語のプリセット", Bundle([Insta()]), SKColors.Gold, ["convert"], ["jpg"], [], [], hot: 8);
        japanese["language"] = "ja";
        var arabic = Share(gallery, "arabic-only", "إعداد مسبق عربي", Bundle([Insta()]), SKColors.Teal, ["convert"], ["jpg"], [], [], hot: 1);
        arabic["language"] = "ar";
        var english = Share(gallery, "english-only", "English preset", Bundle([Insta()]), SKColors.Pink, ["convert"], ["jpg"], [], [], hot: 2);
        english["language"] = "en";
        Publish(gallery, korean, japanese, arabic, english);
        var localization = AppHost.Get<LocalizationService>();
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        try
        {
            // English first (hot 9, then 2), the others after by their score.
            Assert.Equal(["Korean preset in English", "English preset", "日本語のプリセット", "إعداد مسبق عربي"], vm.Cards.Select(c => c.Title));
            var japaneseCard = vm.Cards.Single(c => c.Entry.Id == "japanese-only");
            Assert.Equal("日本語", japaneseCard.Text.Badge);
            Assert.Null(vm.Cards[0].Text.Badge);
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, vm.Cards.Single(c => c.Entry.Id == "arabic-only").Text.Direction);
            Assert.Contains("日本語", japaneseCard.AutomationName);
            vm.Search = "한국어";
            Assert.Equal(["Korean preset in English"], vm.Cards.Select(c => c.Title)); // the main title matches too
            vm.Search = "";
            vm.Selected = vm.Cards[0];
            Assert.Equal("An English description of the Korean preset.", vm.Detail!.Description);

            localization.SetLanguage("ko");
            Assert.Equal(["한국어 프리셋", "日本語のプリセット", "English preset", "إعداد مسبق عربي"], vm.Cards.Select(c => c.Title));
            Assert.Equal("English", vm.Cards.Single(c => c.Entry.Id == "english-only").Text.Badge);
            Assert.Equal("한국어 프리셋", vm.Detail!.Title);
            Assert.Null(vm.Detail.Text.Badge);
            vm.OnlyMyLanguage = true;
            Assert.Equal(["한국어 프리셋"], vm.Cards.Select(c => c.Title));
        }
        finally
        {
            vm.Dispose();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void The_validator_knows_every_language_of_the_app()
    {
        Assert.Equal(LocalizationService.Languages.Select(l => l.Code).Order(StringComparer.Ordinal),
            SharedPresetFolderCheck.Languages.Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public void Export_for_the_gallery_checks_the_choice_and_saves_preset_json()
    {
        TestServices.EnsureInitialized();
        var store = Store();
        var own = store.FindPreset("to-png")!;
        var opened = new List<Uri>();
        var path = Path.Combine(_root, "shared", "preset.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var vm = new GalleryExportViewModel(store, AppHost.Get<ILocalizer>(), own, uri => { opened.Add(uri); return Task.CompletedTask; },
            () => Task.FromResult<string?>(path));

        Assert.True(vm.CanSave);
        Assert.Single(vm.Presets, p => p.IsChosen);
        vm.SaveCommand.Execute(null);
        ClipWindowTests.WaitFor(() => vm.Status == AppHost.Get<ILocalizer>()["gallery.export_saved"]);
        Assert.True(SharedBundleCheck.Check(File.ReadAllBytes(path)).Ok);
        // The names are in the app's language: the gallery takes it as the preset's main language.
        Assert.Equal("en", System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("language").GetString());
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.export_saved"], vm.Status);

        // A folder of one's own can't be shared: listed, and Save waits.
        own.Output.Location = OutputLocation.CustomFolder;
        own.Output.CustomFolder = Path.Combine(_root, "out");
        own.Output.ReplaceOriginal = true;
        vm.Presets.Single(p => p.Preset == own).IsChosen = false;
        vm.Presets.Single(p => p.Preset == own).IsChosen = true;
        Assert.False(vm.CanSave);
        Assert.Contains(vm.Problems, p => p.StartsWith("presets[0].output.customFolder", StringComparison.Ordinal));
        // Without the folder it can be shared, with the warning the gallery will show.
        own.Output.CustomFolder = "";
        vm.Presets.Single(p => p.Preset == own).IsChosen = false;
        vm.Presets.Single(p => p.Preset == own).IsChosen = true;
        Assert.True(vm.CanSave);
        Assert.Contains(vm.Warnings, w => w.Code == "replaces_original" && w.IsHigh);

        vm.Presets.Single(p => p.Preset == own).IsChosen = false;
        Assert.False(vm.CanSave);
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.export_none"], vm.Status);

        vm.OpenFormCommand.Execute(null);
        Assert.Equal(GalleryExportViewModel.SubmitUri, Assert.Single(opened));
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    [InlineData("ar", false)]
    public void The_export_and_the_warnings_render(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var (vm, client, _, _) = Open(RiskyGallery(), Store());
            using var _client = client;
            using var _vm = vm;
            vm.Selected = vm.Cards.Single();
            ClipWindowTests.WaitFor(() => vm.Detail!.Installable && vm.Detail.Images.All(i => i.Bitmap is not null));
            var window = new PresetGalleryWindow { DataContext = vm };
            window.Show();
            RenderTests.Pump();
            Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WarningPanel").IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "AcceptRisk").IsEffectivelyVisible);
            RenderTests.Save(window, $"preset-gallery-warning-{language}-{(dark ? "dark" : "light")}.png");
            window.Close();

            var store = Store();
            var own = store.FindPreset("to-png")!;
            own.Output.Location = OutputLocation.CustomFolder;
            own.Output.CustomFolder = @"C:\Users\Public\Out";
            own.Output.Conflict = Filee.Core.Presets.ConflictPolicy.Overwrite;
            var export = new GalleryExportWindow
            {
                DataContext = new GalleryExportViewModel(store, AppHost.Get<ILocalizer>(), own, _ => Task.CompletedTask, () => Task.FromResult<string?>(null)),
            };
            export.Show();
            RenderTests.Pump();
            RenderTests.Save(export, $"gallery-export-{language}-{(dark ? "dark" : "light")}.png");
            export.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    [AvaloniaFact]
    public void The_presets_page_opens_the_gallery()
    {
        TestServices.EnsureInitialized();
        var page = new PresetsPage { DataContext = new PresetsPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>()) };
        var window = new Window { Content = page, Width = 1000, Height = 700 };
        window.Show();
        RenderTests.Pump();
        var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GalleryButton");
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.title"], AutomationProperties.GetName(button));
        Assert.True(button.Command!.CanExecute(null));
        var export = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GalleryExportButton");
        Assert.Equal(AppHost.Get<ILocalizer>()["gallery.export"], AutomationProperties.GetName(export));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("ko", false)]
    [InlineData("ar", true)]
    [InlineData("mn", false)]
    public void The_window_renders_with_cards_and_the_chosen_preset(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var (vm, client, _, _) = Open(Gallery(), Store());
            using var _client = client;
            using var _vm = vm;
            ClipWindowTests.WaitFor(() => vm.Cards.All(c => c.Thumb is not null));
            vm.Selected = vm.Cards.Single(c => c.Entry.Id == "photo-desk");
            ClipWindowTests.WaitFor(() => vm.Detail!.Installable && vm.Detail.Images.All(i => i.Bitmap is not null));

            var window = new PresetGalleryWindow { DataContext = vm };
            window.Show();
            RenderTests.Pump();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CardList");
            Assert.True(list.IsEffectivelyVisible);
            Assert.Equal(3, list.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.All(list.GetVisualDescendants().OfType<ListBoxItem>(), item => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(item))));
            var install = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallButton");
            Assert.True(install.IsEffectivelyVisible);
            RenderTests.Save(window, $"preset-gallery-{language}-{(dark ? "dark" : "light")}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    [AvaloniaFact]
    public void The_window_renders_while_filee_sh_cant_be_reached()
    {
        TestServices.EnsureInitialized();
        var gallery = Gallery();
        gallery.Offline = true;
        var (vm, client, _, _) = Open(gallery, Store());
        using var _client = client;
        using var _vm = vm;
        var window = new PresetGalleryWindow { DataContext = vm };
        window.Show();
        RenderTests.Pump();
        Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RetryButton").IsEffectivelyVisible);
        RenderTests.Save(window, "preset-gallery-offline-en.png");
        window.Close();
    }
}
