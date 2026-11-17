using Avalonia.Data.Converters;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App.ViewModels.Pages;

/// <summary>Converters used by the Theme page.</summary>
public static class ThemeConverters
{
    /// <summary>A swatch's check mark: what reads on that swatch, which isn't always the accent (Material You shifts it).</summary>
    public static readonly IValueConverter OnColor =
        new FuncValueConverter<Color, IBrush>(color => new SolidColorBrush(SoftPalette.OnAccent(color)));
}
