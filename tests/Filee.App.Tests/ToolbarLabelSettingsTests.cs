// The Toolbar page's slice-label settings: each donut keeps its own Label scale and Slice labels, and the page's
// preview donut shows them, with the slices' icons and their presets' own Show settings.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class ToolbarLabelSettingsTests
{
    private static ToolbarPageViewModel Page(UserDataStore store) =>
        new(store, AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);

    [AvaloniaFact]
    public void Each_donut_keeps_its_own_label_scale_and_slice_labels()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = Page(store);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            Assert.Equal(100, vm.LabelScale);
            Assert.Equal(LabelShow.Auto, vm.SliceLabels.Value);
            Assert.Equal(["Auto", "Icon & text", "Text only", "Icon only"], vm.SliceLabelChoices.Select(c => c.Label));

            vm.LabelScale = 125;
            vm.SliceLabels = vm.SliceLabelChoices.Single(c => c.Value == LabelShow.IconOnly);
            var images = store.Profiles.Single(p => p.Id == "images");
            Assert.Equal(1.25, images.LabelScale);
            Assert.Equal(LabelShow.IconOnly, images.LabelShow);
            Assert.Equal(LabelShow.IconOnly, vm.DefaultShow);
            Assert.Equal((1.25).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), vm.LabelScaleText);

            // Another donut has its own; coming back shows this one's again.
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "video");
            Assert.Equal(100, vm.LabelScale);
            Assert.Equal(LabelShow.Auto, vm.SliceLabels.Value);
            Assert.Equal(1, store.Profiles.Single(p => p.Id == "video").LabelScale);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            Assert.Equal(125, vm.LabelScale);
            Assert.Equal(LabelShow.IconOnly, vm.SliceLabels.Value);

            // Auto again is stored as unset, like a donut that never chose.
            vm.SliceLabels = vm.SliceLabelChoices[0];
            Assert.Null(images.LabelShow);
        }
        finally
        {
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void The_preview_donut_shows_the_slices_icons_and_full_names()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var crop = store.FindPreset("tool-crop")!;
        var before = crop.Show;
        try
        {
            crop.Show = LabelShow.TextOnly;
            using var vm = Page(store);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            var item = vm.DonutItems.Single(i => i.Id == "tool-crop");
            Assert.Equal("crop", item.IconKey);
            Assert.Equal(LabelShow.TextOnly, item.Show);
            Assert.Equal("Crop", item.FullName);
            vm.EditingBack = false;
            Assert.Null(vm.DonutItems.Single(i => i.Id == "to-png").IconKey); // a format conversion, on the front
        }
        finally
        {
            crop.Show = before;
        }
    }
}
