// UI integration for colour-mapped raster preview presets (#155): Map palette tab, PresetAvailability filtering
// (tiff and hgt only), PresetEditorViewModel visibility/save rules, and RadialViewModel Rule 4 (hgt vs tif).

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Motion")]
public class RasterColoursUiTests
{
    [Fact]
    public void Colour_presets_go_to_the_map_tab_and_plain_png_stays_on_image()
    {
        var presets = BuiltInData.CreatePresets();
        var terrain = presets.Single(p => p.Id == "geo-colours-terrain");
        var viridis = presets.Single(p => p.Id == "geo-colours-viridis");
        var plainPng = presets.Single(p => p.Id == "to-png");

        Assert.Equal(PaletteTab.Map, ToolbarPageViewModel.TabOf(terrain));
        Assert.Equal(PaletteTab.Map, ToolbarPageViewModel.TabOf(viridis));
        Assert.Equal(PaletteTab.Image, ToolbarPageViewModel.TabOf(plainPng));
    }

    [AvaloniaFact]
    public void Availability_offers_colour_presets_only_for_tiff_and_hgt()
    {
        TestServices.EnsureInitialized("en");
        var availability = AppHost.Get<PresetAvailability>();
        var presets = BuiltInData.CreatePresets();
        var terrain = presets.Single(p => p.Id == "geo-colours-terrain");
        var viridis = presets.Single(p => p.Id == "geo-colours-viridis");

        var tiff = FormatRegistry.Get("tiff");
        var hgt = FormatRegistry.Get("hgt");
        var jpg = FormatRegistry.Get("jpg");
        var png = FormatRegistry.Get("png");

        Assert.Null(availability.Check(terrain, [tiff]));
        Assert.Null(availability.Check(terrain, [hgt]));
        Assert.NotNull(availability.Check(terrain, [jpg]));
        Assert.NotNull(availability.Check(terrain, [png]));

        Assert.Null(availability.Check(viridis, [tiff]));
        Assert.Null(availability.Check(viridis, [hgt]));
        Assert.NotNull(availability.Check(viridis, [jpg]));
    }

    [AvaloniaFact]
    public void Editor_shows_raster_colours_for_png_when_maps_is_on_or_ramp_already_set_and_saves_only_while_shown()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var geoOff = new HashSet<string>(["geo"], StringComparer.OrdinalIgnoreCase);
        var noneOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Plain PNG preset: hidden when Maps is off, shown when Maps is on.
        var plain = new Preset { Name = "My PNG", TargetFormat = "png" };
        var hiddenVm = new PresetEditorViewModel(plain, loc, geoOff);
        Assert.False(hiddenVm.ShowRasterColours);

        var shownVm = new PresetEditorViewModel(plain, loc, noneOff);
        Assert.True(shownVm.ShowRasterColours);
        Assert.Equal(RasterColours.Off, shownVm.RasterColours.Value);
        shownVm.RasterColours = shownVm.RasterColourModes.Single(m => m.Value == RasterColours.Viridis);
        Assert.True(shownVm.Apply());
        Assert.Equal(RasterColours.Viridis, plain.Image.RasterColours);

        // Existing ramp stays visible even when Maps is off.
        var existingVm = new PresetEditorViewModel(plain, loc, geoOff);
        Assert.True(existingVm.ShowRasterColours);
        Assert.Equal(RasterColours.Viridis, existingVm.RasterColours.Value);

        // Switching target away from PNG hides the option and resets it to Off on Apply.
        existingVm.Target = existingVm.Targets.Single(t => t.Value == "jpg");
        Assert.False(existingVm.ShowRasterColours);
        Assert.True(existingVm.Apply());
        Assert.Equal(RasterColours.Off, plain.Image.RasterColours);
    }

    [AvaloniaFact]
    public void Radial_hides_colour_preset_for_tif_and_shows_it_for_hgt_when_maps_is_off()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<LocalizationService>();
        var availability = AppHost.Get<PresetAvailability>();

        var savedOff = store.Settings.TurnedOffFields.ToList();
        var images = store.Profiles.Single(p => p.Id == "images");
        var savedFront = images.PresetIds.ToList();
        try
        {
            store.Settings.TurnedOffFields = ["geo"];
            images.PresetIds = ["to-png", "geo-colours-terrain"];

            var vm = new RadialViewModel(store, loc, availability);
            vm.Load(["/d/dem.tif"], clickMode: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["to-png"], vm.Presets.Select(p => p.Id));

            vm.Load(["/d/N37W122.hgt"], clickMode: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["to-png", "geo-colours-terrain"], vm.Presets.Select(p => p.Id));
        }
        finally
        {
            store.Settings.TurnedOffFields = savedOff;
            images.PresetIds = savedFront;
        }
    }
}
