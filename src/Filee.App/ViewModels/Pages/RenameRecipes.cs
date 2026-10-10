// Ready-made replacements for the names of converted files (Watch folders → More options → Replace part of the
// name). Each one adds a step the user can edit afterwards.

namespace Filee.App.ViewModels.Pages;

/// <param name="TitleKey">Localization key of what it does.</param>
/// <param name="Find">Regular expression.</param>
/// <param name="Replace">What goes in its place; empty removes the match.</param>
/// <param name="Example">A name it changes, shown with the result.</param>
public sealed record RenameRecipe(string TitleKey, string Find, string Replace, string Example);

public static class RenameRecipes
{
    public static IReadOnlyList<RenameRecipe> All { get; } =
    [
        new("watch.recipe.photo_number", @"^(?:IMG|DSC|PXL)_(\d+)", "Photo_$1", "IMG_0412"),
        new("watch.recipe.drop_camera_prefix", @"^(?:IMG|DSC|PXL)_", "", "DSC_0815"),
        new("watch.recipe.spaces", @"\s+", "_", "trip to busan"),
        new("watch.recipe.copy_number", @"\s*\(\d+\)$", "", "report (2)"),
        new("watch.recipe.brackets", @"\s*[\(\[][^\)\]]*[\)\]]", "", "song [remix] (live)"),
        new("watch.recipe.leading_number", @"^\d+[\s._-]*", "", "01 - intro"),
        new("watch.recipe.date_dashes", @"(\d{4})(\d{2})(\d{2})", "$1-$2-$3", "scan_20261011"),
        new("watch.recipe.symbols", @"[^\w\s.-]", "", "invoice#12@acme!"),
        new("watch.recipe.prefix", "^", "new_", "report"),
        new("watch.recipe.suffix", "$", "_final", "report"),
    ];
}
