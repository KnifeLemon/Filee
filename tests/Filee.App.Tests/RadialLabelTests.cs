// What the live donut's slices are given for their labels: the preset's full name (for the centre hint), its icon (its
// own, its default, none), what it shows (the preset's setting, else the donut's), and the donut's Label scale.

using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class RadialLabelTests
{
    private static (UserDataStore Store, RadialViewModel Vm) Start()
    {
        TestServices.EnsureInitialized("en");
        return (AppHost.Get<UserDataStore>(), AppHost.Get<RadialViewModel>());
    }

    private static DonutItem ItemFor(RadialViewModel vm, string id) =>
        vm.Items.Concat(vm.Items.SelectMany(i => i.Children ?? [])).First(i => i.Id == id);

    [AvaloniaFact]
    public void A_slice_gets_its_presets_own_icon_its_default_or_none()
    {
        var (store, vm) = Start();
        var crop = store.FindPreset("tool-crop")!;
        var before = crop.Icon;
        try
        {
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            Assert.Equal("crop", ItemFor(vm, "tool-crop").IconKey); // the built-in default
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: false);
            Assert.Null(ItemFor(vm, "to-png").IconKey); // a format conversion, on the front

            crop.Icon = "star-outline";
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            Assert.Equal("star-outline", ItemFor(vm, "tool-crop").IconKey);

            crop.Icon = Preset.NoIcon;
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            Assert.Null(ItemFor(vm, "tool-crop").IconKey);
        }
        finally
        {
            crop.Icon = before;
        }
    }

    [AvaloniaFact]
    public void A_slice_shows_what_its_preset_says_else_what_the_donut_says()
    {
        var (store, vm) = Start();
        var images = store.Profiles.Single(p => p.Id == "images");
        var crop = store.FindPreset("tool-crop")!;
        var (show, scale, cropShow) = (images.LabelShow, images.LabelScale, crop.Show);
        try
        {
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            Assert.Null(ItemFor(vm, "tool-crop").Show);
            Assert.Equal(LabelShow.Auto, vm.DefaultShow); // unset: Auto
            Assert.Equal(1, vm.LabelScale);

            images.LabelShow = LabelShow.TextOnly;
            images.LabelScale = 1.25;
            crop.Show = LabelShow.IconOnly;
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            Assert.Equal(LabelShow.IconOnly, ItemFor(vm, "tool-crop").Show);
            Assert.Null(ItemFor(vm, "rotate").Show);
            Assert.Equal(LabelShow.TextOnly, vm.DefaultShow);
            Assert.Equal(1.25, vm.LabelScale);

            // The window passes both to the donut.
            var window = new Views.RadialWindow { DataContext = vm };
            window.Show();
            Assert.Equal(LabelShow.TextOnly, window.DonutControl.DefaultShow);
            Assert.Equal(1.25, window.DonutControl.LabelScale);
            window.Close();
        }
        finally
        {
            (images.LabelShow, images.LabelScale, crop.Show) = (show, scale, cropShow);
        }
    }

    [AvaloniaFact]
    public void The_hint_names_an_icon_only_ring_slice_and_its_group_in_full()
    {
        var (store, vm) = Start();
        var rotate = store.FindPreset("rotate")!;
        var before = rotate.ShortName;
        try
        {
            rotate.ShortName = "Rot";
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            var index = vm.Items.ToList().FindIndex(i => i.Id == "rotate");
            var group = vm.Items[index];
            Assert.Equal("Rot", group.Label);
            Assert.Equal("Rotate", group.FullName);
            var left = group.Children![0];
            Assert.Equal("Left", left.Label); // the ring's compact name
            Assert.Equal("Rotate left", left.FullName);

            vm.ShowHint(index, 0, centerHot: false);
            Assert.Contains("Rotate · Rotate left", vm.CenterSubtitle);
        }
        finally
        {
            rotate.ShortName = before;
        }
    }
}
