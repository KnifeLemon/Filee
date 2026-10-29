// Moving a donut profile up or down (#522): the list order decides which profile opens for a shared extension, a
// profile only trades places with one of its own kind, and the order is what profiles.json keeps.

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ProfileOrderTests
{
    private static ToolbarProfile Normal(string id, params string[] extensions) => new() { Id = id, Extensions = [.. extensions] };

    private static ToolbarProfile Mixed(string id, params string[] extensions) =>
        new() { Id = id, IsFallback = true, Extensions = [.. extensions] };

    private static List<string> Ids(IEnumerable<ToolbarProfile> profiles) => profiles.Select(p => p.Id).ToList();

    [Fact]
    public void A_new_profile_moved_above_images_opens_for_jpg()
    {
        var profiles = BuiltInData.CreateProfiles();
        var mine = Normal("mine", "jpg", "heic");
        profiles.Insert(profiles.FindIndex(p => p.IsFallback), mine);
        Assert.Equal("images", ProfileSelector.Select(profiles, ["a.jpg"])!.Id);

        while (profiles.IndexOf(mine) > profiles.FindIndex(p => p.Id == "images"))
            Assert.NotNull(ProfileOrder.Move(profiles, mine, -1));

        Assert.Equal("mine", ProfileSelector.Select(profiles, ["a.jpg"])!.Id);
        Assert.Equal("mine", ProfileSelector.Select(profiles, ["a.JPG", "b.heic"])!.Id);
        Assert.Equal("images", ProfileSelector.Select(profiles, ["a.png"])!.Id); // what only Images has stays there

        // And back down: Images wins again.
        Assert.NotNull(ProfileOrder.Move(profiles, mine, 1));
        Assert.Equal("images", ProfileSelector.Select(profiles, ["a.jpg"])!.Id);
    }

    [Fact]
    public void Moving_passes_one_profile_and_reports_both_indexes()
    {
        List<ToolbarProfile> profiles = [Normal("a"), Normal("b"), Normal("c"), Mixed("mixed")];

        Assert.Equal((2, 1), ProfileOrder.Move(profiles, profiles[2], -1));
        Assert.Equal(["a", "c", "b", "mixed"], Ids(profiles));
        Assert.Equal((0, 1), ProfileOrder.Move(profiles, profiles[0], 1));
        Assert.Equal(["c", "a", "b", "mixed"], Ids(profiles));
    }

    [Fact]
    public void A_profile_only_trades_places_with_its_own_kind()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("mine", "jpg"), Mixed("mixed"), Mixed("scans", "jpg", "pdf")];

        // The last normal profile can't go below the mixed ones, nor the first mixed one above the normal ones.
        Assert.Null(ProfileOrder.Neighbor(profiles, profiles[1], 1));
        Assert.Null(ProfileOrder.Move(profiles, profiles[1], 1));
        Assert.Null(ProfileOrder.Move(profiles, profiles[2], -1));
        Assert.Null(ProfileOrder.Move(profiles, profiles[0], -1));
        Assert.Null(ProfileOrder.Move(profiles, profiles[3], 1));
        Assert.Equal(["images", "mine", "mixed", "scans"], Ids(profiles));

        // Among the mixed ones the order counts too: the first one with nothing checked is the catch-all.
        Assert.Equal("mixed", ProfileSelector.CatchAll(profiles)!.Id);
        profiles.Add(Mixed("second"));
        Assert.NotNull(ProfileOrder.Move(profiles, profiles[4], -1));
        Assert.NotNull(ProfileOrder.Move(profiles, profiles[3], -1));
        Assert.Equal(["images", "mine", "second", "mixed", "scans"], Ids(profiles));
        Assert.Equal("second", ProfileSelector.CatchAll(profiles)!.Id);
    }

    [Fact]
    public void Profiles_of_the_other_kind_in_between_keep_their_place()
    {
        // An imported normal profile lands after the mixed ones: it still moves up past the normal one above it.
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Mixed("mixed"), Normal("imported", "jpg")];

        Assert.Same(profiles[0], ProfileOrder.Neighbor(profiles, profiles[2], -1));
        Assert.Equal((2, 0), ProfileOrder.Move(profiles, profiles[2], -1));
        Assert.Equal(["imported", "images", "mixed"], Ids(profiles));
        Assert.Equal("imported", ProfileSelector.Select(profiles, ["a.jpg"])!.Id);
    }

    [Fact]
    public void Profiles_a_search_hides_are_passed_over_and_keep_their_order()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("pdf", "pdf"), Normal("video", "mp4"), Normal("mine", "jpg"), Mixed("mixed")];
        bool ShowsJpg(ToolbarProfile p) => p.Matches("jpg");

        // A search for "jpg" shows Images and mine: one step up puts mine right above Images.
        Assert.Equal((3, 0), ProfileOrder.Move(profiles, profiles[3], -1, ShowsJpg));
        Assert.Equal(["mine", "images", "pdf", "video", "mixed"], Ids(profiles));
        Assert.Null(ProfileOrder.Move(profiles, profiles[0], -1, ShowsJpg));

        // And one step down puts it right below Images again.
        Assert.Equal((0, 1), ProfileOrder.Move(profiles, profiles[0], 1, ShowsJpg));
        Assert.Equal(["images", "mine", "pdf", "video", "mixed"], Ids(profiles));
        Assert.Null(ProfileOrder.Move(profiles, profiles[1], 1, ShowsJpg));
    }

    [Fact]
    public void A_profile_not_in_the_list_or_no_direction_moves_nothing()
    {
        List<ToolbarProfile> profiles = [Normal("a"), Normal("b")];
        Assert.Null(ProfileOrder.Move(profiles, Normal("elsewhere"), -1));
        Assert.Null(ProfileOrder.Move(profiles, profiles[1], 0));
        Assert.Equal(["a", "b"], Ids(profiles));
    }

    [Fact]
    public void The_order_is_saved_and_reset_to_default_leaves_it_alone()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var mine = new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["heic"], PresetIds = ["to-jpg"] };
        store.Profiles.Insert(store.Profiles.FindIndex(p => p.IsFallback), mine);
        while (store.Profiles[0] != mine)
            ProfileOrder.Move(store.Profiles, mine, -1);
        var images = store.Profiles.Single(p => p.Id == "images");
        ProfileOrder.Move(store.Profiles, images, 1); // below "pdf"
        store.SaveLibrary();
        var order = Ids(store.Profiles);

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(order, Ids(reloaded.Profiles));
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);

        // "Reset to default" puts the donut's content back, not its place: the user chose that.
        var reloadedImages = reloaded.Profiles.Single(p => p.Id == "images");
        reloadedImages.Name = "Pictures";
        Assert.True(reloaded.ResetProfile(reloadedImages));
        Assert.Equal(order, Ids(reloaded.Profiles));
        Assert.Equal("heic", Assert.Single(reloaded.Profiles[0].Extensions));
        Assert.DoesNotContain("heic", reloadedImages.Extensions); // mine, above it, still has .heic
    }
}
