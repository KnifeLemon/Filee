using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Engines.Infrastructure;
using FakeConverter = Filee.App.Tests.PresetAvailabilityTests.FakeConverter;
using FakeTransformer = Filee.App.Tests.PresetAvailabilityTests.FakeTransformer;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class DefaultPlacementTests(ITestOutputHelper output)
{
    // Keys deliberately exclude face and order. Remove an entry when it no longer applies.
    private static readonly (string Donut, string Preset, string Reason)[] KnownGenericOnly = [];

    private enum Placement { Unusable, AsNamed, Generic, PlatformLimited }

    private sealed record Slot(ToolbarProfile Donut, string Face, string Id, string? Group)
    {
        public string Label => $"{Donut.Id}/{Face}: {(Group is null ? Id : $"{Group}/{Id}")}";
    }

    private sealed record InputResult(FileFormat Input, Placement Placement, string? Reason);

    private sealed record ClassifiedSlot(Slot Slot, Preset Preset, IReadOnlyList<InputResult> Inputs)
    {
        public bool Runnable => Inputs.Any(i => i.Placement != Placement.Unusable);
        public bool GenericOnly => Inputs.Any(i => i.Placement == Placement.Generic)
            && !Inputs.Any(i => i.Placement is Placement.AsNamed or Placement.PlatformLimited);
    }

    private sealed record Report(IReadOnlyList<ToolbarProfile> Donuts, IReadOnlyList<ClassifiedSlot> Slots,
        string Fingerprint, string Environment)
    {
        public string Diagnostics => $"\nGraph fingerprint: {Fingerprint}\nPartial environment signature: {Environment}";
    }

    [AvaloniaFact]
    public void Every_default_slot_runs_on_some_input_of_its_donut()
    {
        var report = ClassifyAll();
        var failures = report.Slots.Where(s => !s.Runnable).Select(s =>
            $"{s.Slot.Label} runs on none of [{string.Join(", ", s.Inputs.Select(i => i.Input.Id))}]; reasons: "
            + string.Join("; ", s.Inputs.Select(i => $"{i.Input.Id}={i.Reason}")));
        Assert.True(!failures.Any(), string.Join("\n", failures) + report.Diagnostics);
    }

    [AvaloniaFact]
    public void No_default_slot_runs_only_as_a_generic_conversion()
    {
        var report = ClassifyAll();
        var genericOnly = report.Slots.Where(s => !s.Slot.Donut.IsFallback && s.GenericOnly).ToList();
        var failures = new List<string>();
        foreach (var slot in genericOnly)
            if (!KnownGenericOnly.Any(k => k.Donut == slot.Slot.Donut.Id && k.Preset == slot.Preset.Id))
                failures.Add($"{slot.Slot.Label} requests {string.Join(", ", Requested(slot.Preset))}, "
                    + $"which holds for none of its runnable inputs [{string.Join(", ", slot.Inputs.Where(i => i.Placement == Placement.Generic).Select(i => i.Input.Id))}]; "
                    + "add it to KnownGenericOnly with a reason, or move it");
        foreach (var entry in KnownGenericOnly)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
                failures.Add($"KnownGenericOnly entry ({entry.Donut}, {entry.Preset}) has an empty reason");
            if (!genericOnly.Any(s => s.Slot.Donut.Id == entry.Donut && s.Preset.Id == entry.Preset))
                failures.Add($"KnownGenericOnly entry ({entry.Donut}, {entry.Preset}) no longer applies; remove it");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures) + report.Diagnostics);
    }

    [AvaloniaFact]
    public void No_default_slot_requests_two_special_jobs()
    {
        var report = ClassifyAll();
        var failures = report.Slots.Where(s => Requested(s.Preset).Count > 1)
            .Select(s => $"{s.Slot.Label} requests {string.Join(", ", Requested(s.Preset))}");
        Assert.True(!failures.Any(), string.Join("\n", failures));
    }

    [AvaloniaFact]
    public void Every_default_slot_is_a_known_preset()
    {
        TestServices.EnsureInitialized("en");
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var failures = BuiltInData.CreateProfiles().SelectMany(p => Slots(p, presets))
            .Where(s => !presets.ContainsKey(s.Id)).Select(s => $"{s.Label} is not a known preset");
        Assert.True(!failures.Any(), string.Join("\n", failures));
    }

    [AvaloniaFact]
    public void Placement_report()
    {
        var report = ClassifyAll();
        output.WriteLine(report.Diagnostics);
        var reported = new List<string>();
        foreach (var donut in report.Donuts)
        {
            var slots = report.Slots.Where(s => s.Slot.Donut.Id == donut.Id).ToList();
            output.WriteLine($"{donut.Id}: " + string.Join(", ", Enum.GetValues<Placement>()
                .Select(kind => $"{kind}={slots.Sum(s => s.Inputs.Count(i => i.Placement == kind))}")));
            reported.Add(donut.Id);
            foreach (var slot in slots.Where(s => s.GenericOnly))
                output.WriteLine($"  Generic-only: {slot.Slot.Label}; requests {string.Join(", ", Requested(slot.Preset))}");
            foreach (var slot in slots.Where(s => !s.GenericOnly && s.Inputs.Any(i => i.Placement == Placement.Generic)))
                output.WriteLine($"  Partly generic: {slot.Slot.Label}; inputs "
                    + string.Join(", ", slot.Inputs.Where(i => i.Placement == Placement.Generic).Select(i => i.Input.Id)));
            foreach (var slot in slots.Where(s => s.Inputs.Any(i => i.Placement == Placement.PlatformLimited)))
                output.WriteLine($"  Platform-limited: {slot.Slot.Label}; inputs "
                    + string.Join(", ", slot.Inputs.Where(i => i.Placement == Placement.PlatformLimited).Select(i => i.Input.Id)));
            foreach (var group in slots.Where(s => s.Slot.Group is not null).GroupBy(s => (s.Slot.Face, s.Slot.Group)))
                output.WriteLine($"  Group {donut.Id}/{group.Key.Face}: {group.Key.Group}; runnable inputs ["
                    + string.Join(", ", group.SelectMany(s => s.Inputs).Where(i => i.Placement != Placement.Unusable)
                        .Select(i => i.Input.Id).Distinct()) + "]");
        }
        Assert.Equal(report.Donuts.Select(d => d.Id).Order(), reported.Order());
    }

    [AvaloniaFact]
    public void Classify_reads_the_special_conditions()
    {
        TestServices.EnsureInitialized("en");
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var catalog = AppHost.Get<ConverterCatalog>();
        var loc = AppHost.Get<ILocalizer>();
        var availability = Availability(catalog, loc);
        var packageReason = loc.Format("engine.reason.needs_package", AllEnginesPackage(catalog, loc).Name);
        Placement Class(string preset, string input) =>
            Classify(presets[preset], FormatRegistry.Get(input), availability, loc, packageReason).Placement;

        Assert.Equal(Placement.AsNamed, Class("extract-frames-png", "mp4"));
        Assert.Equal(Placement.Generic, Class("extract-frames-png", "png"));
        Assert.Equal(Placement.AsNamed, Class("extract-motion", "jpg"));
        Assert.Equal(Placement.Generic, Class("extract-motion", "png"));
        Assert.Empty(Requested(presets["to-png"]));
        Assert.Equal(Placement.AsNamed, Class("to-png", "png"));
    }

    [AvaloniaFact]
    public void Availability_seam_ignores_installation()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var png = FormatRegistry.Get("png");
        var jpg = new Preset { TargetFormat = "jpg" };
        var disconnected = new ConverterCatalog([new FakeConverter("only", ("png", "jpg")) { Available = false }]);
        var availability = Availability(disconnected, loc);
        var palette = availability.ForPalette([png]);
        Assert.Multiple(
            () => Assert.Null(availability.Check(jpg, [png])),
            () => Assert.Contains(png, availability.UsableFormats(jpg, [png])),
            () => Assert.Equal(PresetUse.Usable, palette.Judge(jpg).Use));

        var rotationCatalog = new ConverterCatalog([
            new FakeConverter("plain", [new ConversionEdge("png", "jpg", Cost: 10)]),
            new FakeTransformer("turner", [new ConversionEdge("png", "jpg", Cost: 1)]) { Available = false },
        ]);
        var rotate = new Preset { TargetFormat = "jpg", Image = new() { Rotation = ImageRotation.Right90 } };
        Assert.Equal(loc["error.cannot_turn"], new PresetAvailability(rotationCatalog, loc, []).Check(rotate, [png]));
        Assert.Null(Availability(rotationCatalog, loc).Check(rotate, [png]));

        foreach (var shortcutInstalled in new[] { false, true })
        {
            var catalog = new ConverterCatalog([
                new FakeConverter("libreoffice", [
                    new ConversionEdge("png", "docx"), new("docx", "pdf"), new("pdf", "jpg"),
                ]) { Available = false },
                new FakeConverter("shortcut", [
                    new ConversionEdge("png", "html", Cost: 1), new("html", "md", Cost: 1), new("md", "pdf", Cost: 1),
                ]) { Available = shortcutInstalled },
            ]);
            Assert.Equal(loc["error.no_route"], Availability(catalog, loc).Check(jpg, [png]));
            if (!shortcutInstalled)
            {
                var partialPackage = new EnginePackageState(new EnginePackage("test-all", [], 0, ["libreoffice"]), loc)
                { Status = EnginePackageStatus.NotInstalled };
                var partial = new PresetAvailability(catalog, loc, [partialPackage],
                    () => catalog.CreatePlanner(catalog.All.Select(c => c.Id).ToArray()));
                Assert.Equal(loc.Format("engine.reason.needs_package", partialPackage.Name), partial.Check(jpg, [png]));
            }
        }
    }

    [AvaloniaFact]
    public void Graph_fingerprint_is_stable_during_the_run()
    {
        var report = ClassifyAll(); // Each traversal compares its own before/after fingerprint.
        output.WriteLine(report.Diagnostics);
    }

    private static IReadOnlyList<FileFormat> InputsOf(ToolbarProfile donut) =>
        donut.IsFallback && donut.Extensions.Count == 0
            ? FormatRegistry.Known.Where(f => f.Extensions.Count > 0).ToList()
            : donut.Extensions.Select(FormatRegistry.FindByExtension).OfType<FileFormat>().DistinctBy(f => f.Id).ToList();

    private static IEnumerable<Slot> Slots(ToolbarProfile donut, IReadOnlyDictionary<string, Preset> presets)
    {
        foreach (var (face, ids) in new[] { ("front", donut.PresetIds), ("back", donut.BackPresetIds ?? []) })
            foreach (var id in ids)
            {
                if (presets.TryGetValue(id, out var preset) && preset.IsGroup)
                {
                    foreach (var child in preset.SubPresetIds)
                        yield return new Slot(donut, face, child, id);
                }
                else
                    yield return new Slot(donut, face, id, null);
            }
    }

    private static IReadOnlyList<string> Requested(Preset preset)
    {
        var jobs = new List<string>();
        if (preset.Media.SaveEveryFrame && MediaOptions.FrameTargets.Contains(preset.TargetFormat))
            jobs.Add(nameof(Preset.SavesFramesOf));
        if (preset.Media.ExtractMotionPhoto && MediaOptions.MotionPhotoTargets.Contains(preset.TargetFormat))
            jobs.Add(nameof(Preset.ExtractsMotionOf));
        if (MediaOptions.LivePhotoTargets(preset.Media.LivePhoto).Contains(preset.TargetFormat))
            jobs.Add(nameof(Preset.MakesLivePhotoOf));
        if (preset.Media.Caption && preset.TargetFormat == "srt")
            jobs.Add(nameof(Preset.CaptionsVideo));
        if (preset.Pdf.Ocr && PdfOptions.OcrTargets.Contains(preset.TargetFormat))
            jobs.Add(nameof(Preset.RecognizesTextOf));
        if (preset.JoinsIntoOneMedia)
            jobs.Add(nameof(Preset.JoinsIntoOneMedia));
        if (preset.JoinsImages)
            jobs.Add(nameof(Preset.JoinsImages));
        return jobs;
    }

    private static InputResult Classify(Preset preset, FileFormat input, PresetAvailability availability,
        ILocalizer loc, string packageReason)
    {
        var reason = availability.Check(preset, [input]);
        if (reason == loc["engine.reason.unsupported_platform"])
            return new(input, Placement.PlatformLimited, reason);
        if (reason is not null && reason != packageReason)
            return new(input, Placement.Unusable, reason);
        var asNamed = preset.IsTool || Requested(preset).All(job => job switch
        {
            nameof(Preset.SavesFramesOf) => preset.SavesFramesOf(input.Id),
            nameof(Preset.ExtractsMotionOf) => preset.ExtractsMotionOf(input.Id),
            nameof(Preset.MakesLivePhotoOf) => preset.MakesLivePhotoOf(input.Id),
            nameof(Preset.CaptionsVideo) => preset.CaptionsVideo(input.Id),
            nameof(Preset.RecognizesTextOf) => preset.RecognizesTextOf(input.Id),
            // Availability already checks the source categories for the two joins.
            nameof(Preset.JoinsIntoOneMedia) => preset.JoinsIntoOneMedia,
            nameof(Preset.JoinsImages) => preset.JoinsImages,
            _ => throw new InvalidOperationException(job),
        });
        return new(input, asNamed ? Placement.AsNamed : Placement.Generic, reason);
    }

    private static EnginePackageState AllEnginesPackage(ConverterCatalog catalog, ILocalizer loc) =>
        new(new EnginePackage("test-all", [], 0, catalog.All.Select(c => c.Id).ToArray()), loc)
        { Status = EnginePackageStatus.NotInstalled };

    private static PresetAvailability Availability(ConverterCatalog catalog, ILocalizer loc) =>
        new(catalog, loc, [AllEnginesPackage(catalog, loc)],
            () => catalog.CreatePlanner(catalog.All.Select(c => c.Id).ToArray()));

    private static Report ClassifyAll()
    {
        TestServices.EnsureInitialized("en");
        var catalog = AppHost.Get<ConverterCatalog>();
        var loc = AppHost.Get<ILocalizer>();
        // GetStatus can rebuild edges (notably LibreOffice). Warm the catalog before taking the snapshot.
        foreach (var converter in catalog.All)
            _ = catalog.StatusOf(converter);
        var availability = Availability(catalog, loc);
        var packageReason = loc.Format("engine.reason.needs_package", AllEnginesPackage(catalog, loc).Name);
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var donuts = BuiltInData.CreateProfiles();
        var environment = EnvironmentSignature(catalog);
        var before = GraphFingerprint(catalog);
        var classified = new List<ClassifiedSlot>();
        foreach (var donut in donuts)
            foreach (var slot in Slots(donut, presets))
            {
                Assert.True(presets.ContainsKey(slot.Id), $"{slot.Label} is not a known preset");
                var preset = presets[slot.Id];
                classified.Add(new(slot, preset, InputsOf(donut)
                    .Select(input => Classify(preset, input, availability, loc, packageReason)).ToList()));
            }
        var after = GraphFingerprint(catalog);
        Assert.True(before == after, $"Graph changed during ClassifyAll: {before} -> {after}\n{environment}");
        return new(donuts, classified, before, environment);
    }

    private static string GraphFingerprint(ConverterCatalog catalog)
    {
        var edges = catalog.All.SelectMany(c => c.Edges.Select(e =>
            string.Join("|", c.Id, e.From, e.To, e.Cost.ToString(CultureInfo.InvariantCulture),
                e.FrameSequence, e.MotionPhoto, e.LivePhoto, e.Ocr, e.Caption,
                e.ImageSourcesOnly, e.NotForImageSources, e.Unshaped, e.DirectOnly))).Order(StringComparer.Ordinal);
        var graph = string.Join("\n", edges) + "\nPriority=" + string.Join("|", catalog.Priority);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(graph)));
    }

    private static string EnvironmentSignature(ConverterCatalog catalog)
    {
        string Ci(string key) => Environment.GetEnvironmentVariable(key) ?? "local";
        var libreOffice = catalog.All.Single(c => c.Id == "libreoffice");
        return $"OS={RuntimeInformation.OSDescription}; RID={RuntimeInformation.RuntimeIdentifier}; "
            + $"GITHUB_WORKFLOW={Ci("GITHUB_WORKFLOW")}; GITHUB_JOB={Ci("GITHUB_JOB")}; "
            + $"RUNNER_OS={Ci("RUNNER_OS")}; RUNNER_ARCH={Ci("RUNNER_ARCH")}; "
            + $"Pandoc={catalog.StatusOf(catalog.All.Single(c => c.Id == "pandoc")).IsAvailable}; "
            + $"LibreOffice={catalog.StatusOf(libreOffice).IsAvailable}; "
            + $"H2Orestart={libreOffice.Edges.Any(e => e.From == "hwp")}; "
            + $"LibreOfficeForSlides={EngineEnvironment.LibreOfficeForSlides}; "
            + $"LibreOfficeForHwp={EngineEnvironment.LibreOfficeForHwp}; "
            + $"LibreOfficeForDocuments={EngineEnvironment.LibreOfficeForDocuments}; "
            + $"LibreOfficeForSheets={EngineEnvironment.LibreOfficeForSheets}";
    }
}
