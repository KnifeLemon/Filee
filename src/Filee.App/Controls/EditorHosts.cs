// What the editor's timeline and preview ask of the editor (ClipEditorViewModel): the clip, its pictures and sound,
// the selection, the right-click menus, and every edit a drag or a click makes. The controls only draw and hit-test.

using Avalonia.Media.Imaging;
using Filee.Core.Presets;

namespace Filee.App.Controls;

/// <summary>What can be selected in the editor.</summary>
public enum EditorItemKind
{
    /// <summary>A part on V1 (or its sound on A1).</summary>
    Part,

    /// <summary>The transition from part <see cref="EditorItem.Index"/> into the next one.</summary>
    Transition,

    Text,

    /// <summary>A picture over the video.</summary>
    Image,

    /// <summary>A sound from another file, on A2, A3, …</summary>
    Sound,
}

/// <summary>One thing in the editor: its kind and its index.</summary>
public readonly record struct EditorItem(EditorItemKind Kind, int Index);

/// <summary>A line of a right-click menu: what it says, what it does, or a submenu; a separator when everything is empty.</summary>
public sealed record EditorMenuItem(string Header, Action? Run = null, bool Enabled = true, IReadOnlyList<EditorMenuItem>? Items = null, string? Gesture = null)
{
    public static EditorMenuItem Separator { get; } = new("-");

    public bool IsSeparator => Header == "-";
}

/// <summary>The editor as the timeline sees it.</summary>
public interface IEditorTimelineHost
{
    /// <summary>Raised when anything the timeline shows changed (the clip, the selection, pictures, the playhead).</summary>
    event Action? TimelineChanged;

    /// <summary>The clip as it is now (read only).</summary>
    ClipEdit Edit { get; }

    /// <summary>Seconds into the clip.</summary>
    double Playhead { get; }

    IReadOnlyCollection<int> SelectedParts { get; }

    /// <summary>The selected text, picture, sound or transition (null: parts, or nothing).</summary>
    EditorItem? SelectedItem { get; }

    /// <summary>Sound tracks shown: those in use and an empty one to drag a sound to.</summary>
    int SoundTracks { get; }

    /// <summary>A small picture of <paramref name="source"/> near <paramref name="seconds"/>, or null while none is there.</summary>
    Bitmap? PictureAt(string? source, double seconds);

    /// <summary>The picture of an image file over the video, or null.</summary>
    Bitmap? ImageOf(string path);

    /// <summary>The loudness of a video's or a sound's file over its whole length (0 to 1), empty without sound.</summary>
    IReadOnlyList<float> LoudnessOf(string? source);

    double DurationOf(string? source);

    string GapText { get; }
    string NoSoundText { get; }

    void Seek(double seconds);
    void SelectPart(int index, ClipSelect mode);
    void Select(EditorItem item);
    void ClearSelection();
    void MovePart(int from, int to);

    /// <summary>An edge of part <paramref name="index"/> dragged so that it plays <paramref name="length"/> seconds.</summary>
    void DragEdge(int index, bool end, double length);

    /// <summary>A text or a picture moved to start at <paramref name="start"/>, or its edges dragged.</summary>
    void PlaceItem(EditorItem item, double start, double length);

    /// <summary>A sound moved: to <paramref name="offset"/> seconds into the clip, on <paramref name="track"/>.</summary>
    void MoveSound(int index, double offset, int track);

    /// <summary>A sound's edge dragged: it starts <paramref name="from"/> seconds into its file, plays <paramref name="play"/> seconds.</summary>
    void TrimSound(int index, double offset, double from, double play);

    void SetPartVolume(int index, double volume);
    void SetSoundVolume(int index, double volume);
    void CloseGap(int index);

    /// <summary>A drag ended: the next change is its own undo step.</summary>
    void EndDrag();

    /// <summary>Files dropped on the timeline, to go in as part <paramref name="at"/> on.</summary>
    void DropFiles(IReadOnlyList<string> files, int at);

    /// <summary>The right-click menu of <paramref name="item"/> (selected first), or of the clip at <paramref name="seconds"/> when null.</summary>
    IReadOnlyList<EditorMenuItem> MenuFor(EditorItem? item, double seconds);
}

/// <summary>The editor as the preview sees it.</summary>
public interface IEditorPreviewHost
{
    /// <summary>Raised when the picture, the frame or the texts changed.</summary>
    event Action? PreviewChanged;

    /// <summary>A frame of a video (placed by <see cref="PicturePart"/>), or the clip as it plays (<see cref="IsComposed"/>).</summary>
    Bitmap? Picture { get; }

    /// <summary>The picture already is the whole frame (the clip playing).</summary>
    bool IsComposed { get; }

    /// <summary>The frame's size in pixels (its shape).</summary>
    (int Width, int Height) Canvas { get; }

    ClipFit Fit { get; }

    /// <summary>The part the picture is of (its zoom, turn and place set where it goes); null for black (a pause).</summary>
    ClipPart? PicturePart { get; }

    /// <summary>The index of <see cref="PicturePart"/> in the clip, or -1.</summary>
    int PictureIndex { get; }

    /// <summary>The video's picture size as shown, for placing it.</summary>
    (int Width, int Height) PictureSize { get; }

    /// <summary>The texts shown now, with their index in the clip.</summary>
    IReadOnlyList<(int Index, ClipText Text)> VisibleTexts { get; }

    /// <summary>The pictures over the video shown now, with their index in the clip.</summary>
    IReadOnlyList<(int Index, ClipImage Image)> VisibleImages { get; }

    /// <summary>The picture of an image file, or null.</summary>
    Bitmap? ImageOf(string path);

    /// <summary>
    /// What has the frame with handles: a text, a picture over the video, or the part shown (<see cref="EditorItemKind.Part"/>
    /// with <see cref="PictureIndex"/>); null for nothing.
    /// </summary>
    EditorItem? Framed { get; }

    void Select(EditorItem item);

    /// <summary>
    /// Moves, sizes and turns <paramref name="item"/>: its middle at <paramref name="x"/>, <paramref name="y"/> of the
    /// frame, its size (a part's zoom, a text's height, a picture's width as parts of the frame) and turn in degrees.
    /// </summary>
    void Transform(EditorItem item, double x, double y, double size, double rotation);

    void EndDrag();
    void ToggleFullPreview();
    void EditText(int index);
    IReadOnlyList<EditorMenuItem> MenuFor(EditorItem? item, double seconds);
}
