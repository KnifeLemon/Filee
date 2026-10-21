// The editor's texts and pictures for the conversion: each drawn as the preview draws it (its font, style, see-through
// and turn) into a transparent picture of the written frame's size, which FFmpeg lays over the video while it shows.
// So the written clip looks like the preview with any font this computer has, and no FFmpeg text filter is needed.
// The pictures go into a folder of their own in the temp folder (the conversion may run after the editor closed);
// folders a day old are taken away when an editor opens.

using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Filee.App.Controls;
using Filee.Core.Presets;

namespace Filee.App.Services;

public static class EditorLayers
{
    private const string FolderPrefix = "clip-layers-";

    private static string Root => Path.Combine(Path.GetTempPath(), "Filee");

    /// <summary>
    /// A copy of <paramref name="edit"/> whose texts and pictures carry their drawn picture
    /// (<see cref="ClipText.Rendered"/>, <see cref="ClipImage.Rendered"/>) for a frame of
    /// <paramref name="width"/> × <paramref name="height"/>. Must run on the UI thread.
    /// </summary>
    public static ClipEdit Render(ClipEdit edit, int width, int height, Func<string, Bitmap?> imageOf)
    {
        var copy = edit.Clone();
        if (copy.Texts.Count == 0 && copy.Images.Count == 0)
            return copy;
        var folder = Path.Combine(Root, FolderPrefix + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        var size = new PixelSize(Math.Max(2, width), Math.Max(2, height));
        var frame = new Rect(0, 0, size.Width, size.Height);
        for (var i = 0; i < copy.Texts.Count; i++)
        {
            var text = copy.Texts[i];
            text.Rendered = Draw(size, Path.Combine(folder, $"text-{i}.png"), context => EditorDrawing.DrawText(context, text, frame));
        }
        for (var i = 0; i < copy.Images.Count; i++)
        {
            var image = copy.Images[i];
            if (imageOf(image.Path) is not { } bitmap)
                continue;
            image.Rendered = Draw(size, Path.Combine(folder, $"picture-{i}.png"), context => EditorDrawing.DrawImage(context, image, bitmap, frame));
        }
        return copy;
    }

    private static string Draw(PixelSize size, string path, Action<Avalonia.Media.DrawingContext> draw)
    {
        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
            draw(context);
        target.Save(path);
        return path;
    }

    /// <summary>Takes away the layer folders of conversions long done (a day old or more).</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root))
                return;
            foreach (var folder in Directory.EnumerateDirectories(Root, FolderPrefix + "*"))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromDays(1))
                    Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or not ours to take: next time.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
