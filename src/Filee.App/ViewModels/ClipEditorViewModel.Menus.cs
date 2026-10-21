// The editor's right-click menus: for parts (cut, copy, paste, duplicate, still, backwards, speed, sound, turn, cut
// out), a gap, a transition, a text, a picture, a sound (tracks, fade), and an empty place (paste, add a text, close
// the gaps). Every line does what the toolbar, the keys or the properties do.

using Filee.App.Controls;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed partial class ClipEditorViewModel
{
    /// <summary>
    /// The menu of <paramref name="item"/> (already selected), or of the empty place at <paramref name="seconds"/>
    /// into the clip (NaN: the preview, at the playhead).
    /// </summary>
    public IReadOnlyList<EditorMenuItem> MenuFor(EditorItem? item, double seconds)
    {
        StopPlaying();
        return item switch
        {
            { Kind: EditorItemKind.Part } part when part.Index < _project.Parts.Count && _project.Parts[part.Index].Blank => GapMenu(part.Index),
            { Kind: EditorItemKind.Part } part => PartMenu(part.Index, seconds),
            { Kind: EditorItemKind.Transition } transition => TransitionMenu(transition.Index),
            { Kind: EditorItemKind.Text } => TextMenu(),
            { Kind: EditorItemKind.Image } => ImageMenu(),
            { Kind: EditorItemKind.Sound } => SoundMenu(),
            _ => EmptyMenu(seconds),
        };
    }

    private EditorMenuItem Line(string key, Action run, bool enabled = true, string? gesture = null) => new(_loc[key], run, enabled, Gesture: gesture);

    private IReadOnlyList<EditorMenuItem> PartMenu(int index, double seconds)
    {
        var part = _project.Parts[index];
        var moving = !part.IsStill;
        return
        [
            Line("clip.cut", () =>
            {
                if (double.IsFinite(seconds))
                    Seek(seconds);
                Split();
            }, gesture: "C"),
            EditorMenuItem.Separator,
            Line("editor.menu_copy", Copy, gesture: "Ctrl+C"),
            Line("editor.menu_paste", Paste, _clipboard.Count > 0 || _textClipboard is not null, "Ctrl+V"),
            Line("editor.duplicate", Duplicate, gesture: "Ctrl+D"),
            EditorMenuItem.Separator,
            Line("editor.freeze", Freeze, moving),
            Line(part.Reverse ? "editor.menu_forwards" : "editor.reverse", ToggleReverse, moving),
            new(_loc["editor.speed"], Enabled: moving, Items: Speeds.Select(speed =>
                new EditorMenuItem(speed.Label + (Math.Abs(part.PlaySpeed - speed.Value) < 1e-9 ? "  ✓" : ""),
                    () => _project.ChangeParts(TargetParts, p => p.Speed = speed.Value))).ToList()),
            Line(part.Muted ? "clip.unmute" : "clip.mute", () => _project.ChangeParts(TargetParts, p => p.Muted = !part.Muted), moving),
            Line("editor.menu_turn", TurnRight),
            Line("editor.reset_frame", ResetFrame, part.PlayZoom != 1 || part.PlayRotation != 0 || part.FocusX != 0.5 || part.FocusY != 0.5),
            EditorMenuItem.Separator,
            Line("editor.menu_delete_gap", () => Delete(closeUp: false), gesture: "Delete"),
            Line("editor.menu_delete_close", () => Delete(closeUp: true), gesture: "Shift+Delete"),
        ];
    }

    private IReadOnlyList<EditorMenuItem> GapMenu(int index) =>
    [
        Line("clip.close_gap", () => CloseGap(index)),
        Line("clip.close_gaps", CloseGaps, HasGaps),
    ];

    private IReadOnlyList<EditorMenuItem> TransitionMenu(int index) =>
        Transitions.Select(kind => new EditorMenuItem(kind.Label + (_project.Parts[index].Transition == kind.Value ? "  ✓" : ""),
            () => _project.ChangePart(index, p => p.Transition = kind.Value))).ToList();

    private IReadOnlyList<EditorMenuItem> TextMenu() =>
    [
        Line("editor.menu_edit_text", () => EditText(SelectedText)),
        new(_loc["editor.text_style"], Items: TextStyles.Select(style => new EditorMenuItem(style.Label, () => TextStyleChoice = style)).ToList()),
        Line("editor.menu_copy", Copy, gesture: "Ctrl+C"),
        Line("editor.duplicate", Duplicate, gesture: "Ctrl+D"),
        Line("editor.menu_turn", TurnRight),
        Line("editor.reset_frame", ResetFrame),
        EditorMenuItem.Separator,
        Line("clip.delete", () => Delete(closeUp: false), gesture: "Delete"),
    ];

    private IReadOnlyList<EditorMenuItem> ImageMenu() =>
    [
        Line("editor.duplicate", Duplicate, gesture: "Ctrl+D"),
        Line("editor.menu_turn", TurnRight),
        Line("editor.reset_frame", ResetFrame),
        Line("editor.menu_whole_clip", () => _project.ChangeImage(SelectedImage, i =>
        {
            i.Start = 0;
            i.Length = Length;
        })),
        EditorMenuItem.Separator,
        Line("clip.delete", () => Delete(closeUp: false), gesture: "Delete"),
    ];

    private IReadOnlyList<EditorMenuItem> SoundMenu()
    {
        var sound = _project.Sounds[SelectedSound];
        return
        [
            Line("editor.duplicate", Duplicate, gesture: "Ctrl+D"),
            Line("editor.menu_track_up", () => MoveSoundTrackCommand.Execute("-1"), sound.Track > 0),
            Line("editor.menu_track_down", () => MoveSoundTrackCommand.Execute("1"), sound.Track < MaxSoundTracks - 1),
            Line(sound.Fade ? "editor.menu_no_fade" : "editor.music_fade", () => SoundFade = !sound.Fade),
            Line("editor.menu_to_playhead", () => _project.ChangeSound(SelectedSound, s => s.Offset = Playhead)),
            EditorMenuItem.Separator,
            Line("clip.delete", () => Delete(closeUp: false), gesture: "Delete"),
        ];
    }

    private IReadOnlyList<EditorMenuItem> EmptyMenu(double seconds) =>
    [
        Line("editor.menu_paste", () =>
        {
            if (double.IsFinite(seconds))
                Seek(seconds);
            Paste();
        }, _clipboard.Count > 0 || _textClipboard is not null, "Ctrl+V"),
        new(_loc["editor.menu_add_text"], Items:
        [
            new(_loc["editor.style_title"], () => AddText("Title")),
            new(_loc["editor.style_caption"], () => AddText("Caption")),
            new(_loc["editor.style_label"], () => AddText("Label")),
        ]),
        Line("clip.close_gaps", CloseGaps, HasGaps),
        Line("editor.menu_select_all", SelectAll, gesture: "Ctrl+A"),
    ];
}
