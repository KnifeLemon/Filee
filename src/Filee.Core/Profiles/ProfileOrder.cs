// Moving a donut profile up or down the list (#522). The list order is what ProfileSelector reads: of two normal
// profiles with the same extension the higher one opens, and of the mixed ones the higher match (and catch-all) wins.
// So a profile only trades places with one of its own kind; normal and mixed profiles never compete for a file.

namespace Filee.Core.Profiles;

public static class ProfileOrder
{
    /// <summary>
    /// The profile <paramref name="profile"/> would pass: the nearest one of the same kind (normal or mixed) above it
    /// (<paramref name="direction"/> -1) or below it (+1), among those <paramref name="shown"/> lets through (all when
    /// null, e.g. the ones a search box shows). Null when there is none.
    /// </summary>
    public static ToolbarProfile? Neighbor(IReadOnlyList<ToolbarProfile> profiles, ToolbarProfile profile, int direction,
        Func<ToolbarProfile, bool>? shown = null)
    {
        var index = IndexOf(profiles, profile);
        if (index < 0 || direction == 0)
            return null;
        for (var i = index + Math.Sign(direction); i >= 0 && i < profiles.Count; i += Math.Sign(direction))
        {
            var other = profiles[i];
            if (other.IsFallback == profile.IsFallback && (shown is null || shown(other)))
                return other;
        }
        return null;
    }

    /// <summary>
    /// Moves <paramref name="profile"/> just past its <see cref="Neighbor"/>: above it when going up, below it when going
    /// down. Profiles in between (hidden by <paramref name="shown"/>, or of the other kind) keep their order.
    /// </summary>
    /// <returns>The old and the new index, or null when there is nothing to pass.</returns>
    public static (int From, int To)? Move(List<ToolbarProfile> profiles, ToolbarProfile profile, int direction,
        Func<ToolbarProfile, bool>? shown = null)
    {
        if (Neighbor(profiles, profile, direction, shown) is not { } neighbor)
            return null;
        var from = IndexOf(profiles, profile);
        // Removing the profile first shifts a neighbor below it up by one, which is the "just below it" slot.
        var to = IndexOf(profiles, neighbor);
        profiles.RemoveAt(from);
        profiles.Insert(to, profile);
        return (from, to);
    }

    private static int IndexOf(IReadOnlyList<ToolbarProfile> profiles, ToolbarProfile profile)
    {
        for (var i = 0; i < profiles.Count; i++)
        {
            if (ReferenceEquals(profiles[i], profile))
                return i;
        }
        return -1;
    }
}
