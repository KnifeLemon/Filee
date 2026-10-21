// The editor's font list shows each font in itself.

using Avalonia.Data.Converters;
using Filee.App.Controls;

namespace Filee.App.ViewModels;

public static class FontNames
{
    /// <summary>A font's name ("" for Filee's own) as the font family to draw it in.</summary>
    public static IValueConverter Family { get; } = new FuncValueConverter<string?, Avalonia.Media.FontFamily>(EditorDrawing.FontOf);
}
