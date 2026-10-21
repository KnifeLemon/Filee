// The editor's right-click menus, built from the lines the editor gives (EditorMenuItem).

using Avalonia.Controls;

namespace Filee.App.Controls;

public static class EditorMenus
{
    public static ContextMenu Build(IReadOnlyList<EditorMenuItem> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
            menu.Items.Add(Line(item));
        return menu;
    }

    private static Control Line(EditorMenuItem item)
    {
        if (item.IsSeparator)
            return new Separator();
        var line = new MenuItem { Header = item.Header, IsEnabled = item.Enabled, InputGesture = item.Gesture is { } g ? Avalonia.Input.KeyGesture.Parse(g) : null };
        if (item.Items is { Count: > 0 } children)
        {
            foreach (var child in children)
                line.Items.Add(Line(child));
        }
        else if (item.Run is { } run)
            line.Click += (_, _) => run();
        return line;
    }
}
