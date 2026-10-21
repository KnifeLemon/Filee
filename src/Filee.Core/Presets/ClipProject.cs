// The editor's editing, without the window: a clip made of parts from one or more videos in the order they play. Parts
// are added, cut with the blade, cut out (leaving a pause or closing up), moved, copied, trimmed by their edges (taking
// more or less of their video), held as a still, played backwards or at another speed, joined by transitions; texts go
// over the picture and music under it. Every change can be undone. ToEdit() gives what the conversion writes.

namespace Filee.Core.Presets;

/// <summary>Edits an editor clip (<see cref="ClipEdit.Editor"/>); see the file comment.</summary>
public sealed class ClipProject
{
    /// <summary>Changes that can be undone.</summary>
    private const int UndoDepth = 100;

    /// <summary>A still made with <see cref="Freeze"/> lasts this long to start with.</summary>
    public const double FreezeSeconds = 2;

    private ClipEdit _edit;
    private readonly Func<string?, double> _durationOf;
    private readonly Stack<ClipEdit> _undo = new();
    private readonly Stack<ClipEdit> _redo = new();

    /// <summary>What the last change did, when changes alike join into one undo step (a slider or an edge dragged).</summary>
    private string? _joining;

    /// <summary>Inside <see cref="Batch"/>: changes are made but remembered (and told) once, at its end.</summary>
    private int _batching;

    /// <param name="edit">The clip to start with (a copy is edited).</param>
    /// <param name="durationOf">How long each video is, in seconds (null: the window's video).</param>
    public ClipProject(ClipEdit edit, Func<string?, double> durationOf)
    {
        _edit = edit.Clone();
        _edit.Editor = true;
        // The editor keeps every sound on its tracks: the quick cut's music becomes the first.
        if (_edit.Music is { } music)
        {
            music.Track = 0;
            _edit.Sounds.Insert(0, music);
            _edit.Music = null;
        }
        _durationOf = durationOf;
    }

    /// <summary>Raised after every change (also undo and redo).</summary>
    public event Action? Changed;

    /// <summary>The clip as it is now: read it, change it only through this class.</summary>
    public ClipEdit Edit => _edit;

    public IReadOnlyList<ClipPart> Parts => _edit.Parts;
    public IReadOnlyList<ClipText> Texts => _edit.Texts;
    public IReadOnlyList<ClipImage> Images => _edit.Images;
    public IReadOnlyList<ClipMusic> Sounds => _edit.Sounds;

    /// <summary>The sound tracks in use (A2 is 0): at least one.</summary>
    public int SoundTracks => Math.Max(1, _edit.Sounds.Count == 0 ? 1 : _edit.Sounds.Max(s => s.Track) + 1);

    /// <summary>How long the clip plays, in seconds.</summary>
    public double Length => _edit.Length;

    /// <summary>True when parts were cut out leaving a pause.</summary>
    public bool HasGaps => _edit.Parts.Any(p => p.Blank);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Where each part starts playing in the clip.</summary>
    public double[] Starts() => _edit.PartStarts();

    /// <summary>How long the video <paramref name="source"/> is (null: the window's video).</summary>
    public double DurationOf(string? source) => Math.Max(ClipRange.MinLength, _durationOf(source));

    /// <summary>The copy the conversion writes.</summary>
    public ClipEdit ToEdit() => _edit.Clone();

    /// <summary>The part playing <paramref name="seconds"/> into the clip (in a transition, the one coming in); -1 when there is none.</summary>
    public int IndexAt(double seconds)
    {
        var parts = _edit.Parts;
        if (parts.Count == 0 || !double.IsFinite(seconds))
            return -1;
        var starts = Starts();
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (seconds >= starts[i] - 1e-9)
                return i;
        }
        return 0;
    }

    // ───────────────────────── Parts ─────────────────────────

    /// <summary>
    /// Adds the whole of a video (or <paramref name="start"/> to <paramref name="end"/> of it) as a part at
    /// <paramref name="at"/> (null: at the end). Returns its index.
    /// </summary>
    public int AddVideo(string? source, int? at = null, double start = 0, double? end = null)
    {
        var duration = DurationOf(source);
        var from = Math.Clamp(start, 0, duration - ClipRange.MinLength);
        var to = Math.Clamp(end ?? duration, from + ClipRange.MinLength, duration);
        return Insert([new ClipPart { Source = source, Start = from, End = to }], at ?? _edit.Parts.Count);
    }

    /// <summary>
    /// Puts copies of <paramref name="parts"/> in at <paramref name="at"/> (pasting, duplicating). Returns the index of
    /// the first.
    /// </summary>
    public int Insert(IReadOnlyList<ClipPart> parts, int at)
    {
        if (parts.Count == 0)
            return -1;
        var index = Math.Clamp(at, 0, _edit.Parts.Count);
        Change(() => _edit.Parts.InsertRange(index, parts.Select(p => p.Clone())));
        return index;
    }

    /// <summary>The blade: cuts the part playing at <paramref name="seconds"/> in two. False too close to a cut.</summary>
    public bool Split(double seconds)
    {
        var index = IndexAt(seconds);
        if (index < 0)
            return false;
        var at = seconds - Starts()[index];
        if (_edit.Parts[index].SplitAt(at) is not { } split)
            return false;
        Change(() =>
        {
            _edit.Parts[index] = split.Before;
            _edit.Parts.Insert(index + 1, split.After);
        });
        return true;
    }

    /// <summary>
    /// Cuts the parts <paramref name="indices"/> out: with <paramref name="closeUp"/> what follows closes up, otherwise
    /// each leaves a pause as long as it played (until the gap is closed). False when nothing would be left to play.
    /// </summary>
    public bool Remove(IReadOnlyCollection<int> indices, bool closeUp)
    {
        var chosen = indices.Where(i => i >= 0 && i < _edit.Parts.Count).Distinct().ToHashSet();
        if (chosen.Count == 0 || !_edit.Parts.Where((p, i) => !p.Blank && !chosen.Contains(i)).Any())
            return false;
        if (!closeUp && chosen.All(i => _edit.Parts[i].Blank))
            return false;
        Change(() =>
        {
            var parts = new List<ClipPart>();
            for (var i = 0; i < _edit.Parts.Count; i++)
            {
                var part = _edit.Parts[i];
                if (!chosen.Contains(i))
                    parts.Add(part);
                else if (!closeUp)
                    parts.Add(part.Blank ? part : Pause(part.Length));
            }
            _edit.Parts = parts;
        });
        return true;
    }

    /// <summary>Closes the gap at part <paramref name="index"/> (a pause): what follows closes up.</summary>
    public bool CloseGap(int index)
    {
        if (index < 0 || index >= _edit.Parts.Count || !_edit.Parts[index].Blank)
            return false;
        Change(() => _edit.Parts.RemoveAt(index));
        return true;
    }

    /// <summary>Closes every gap. Returns how many.</summary>
    public int CloseGaps()
    {
        var gaps = _edit.Parts.Count(p => p.Blank);
        if (gaps > 0)
            Change(() => _edit.Parts = _edit.Parts.Where(p => !p.Blank).ToList());
        return gaps;
    }

    /// <summary>Moves part <paramref name="from"/> to play as part <paramref name="to"/> (a drag on the timeline).</summary>
    public bool Move(int from, int to)
    {
        var count = _edit.Parts.Count;
        if (from < 0 || from >= count || to < 0 || to >= count || from == to)
            return false;
        Change(() =>
        {
            var part = _edit.Parts[from];
            _edit.Parts.RemoveAt(from);
            _edit.Parts.Insert(to, part);
        });
        return true;
    }

    /// <summary>
    /// Drags an edge of part <paramref name="index"/> on the timeline so that it plays <paramref name="length"/>
    /// seconds: its end edge (<paramref name="end"/>) takes more or less from the end of its video, its start edge from
    /// the start of it, while the part keeps its place. Outwards as far as the video goes; inwards down to
    /// <see cref="ClipRange.MinLength"/>. What follows moves along. A still's or pause's edge sets how long it lasts.
    /// A drag is one undo step. Returns how long the part plays now.
    /// </summary>
    public double TrimEdge(int index, bool end, double length)
    {
        if (index < 0 || index >= _edit.Parts.Count || !double.IsFinite(length))
            return double.NaN;
        var part = _edit.Parts[index];
        var trimmed = Trimmed(part, end, Math.Max(ClipRange.MinLength, length));
        if (Math.Abs(trimmed.Length - part.Length) > 1e-9 || trimmed.Start != part.Start || trimmed.End != part.End)
            Change(() => _edit.Parts[index] = trimmed, join: $"trim {index} {end}");
        return _edit.Parts[index].Length;
    }

    private ClipPart Trimmed(ClipPart part, bool end, double length)
    {
        var copy = part.Clone();
        if (part.IsStill)
        {
            copy.Hold = Math.Clamp(length, ClipRange.MinLength, ClipPart.MaxHold);
            return copy;
        }
        if (part.Blank)
        {
            copy.End = copy.Start + length * part.PlaySpeed;
            return copy;
        }
        // Seconds of video per second played (a smooth change of speed: on average).
        var rate = part.Length > 0 ? part.SourceLength / part.Length : part.PlaySpeed;
        var change = (length - part.Length) * rate;
        var duration = DurationOf(part.Source);
        var min = ClipRange.MinLength * rate;
        // The edge where the part stops playing is its end in the video, unless it plays backwards.
        if (end != part.Reverse)
            copy.End = Math.Clamp(part.End + change, part.Start + min, duration);
        else
            copy.Start = Math.Clamp(part.Start - change, 0, part.End - min);
        return copy;
    }

    /// <summary>Changes part <paramref name="index"/> (its settings kept within their limits).</summary>
    /// <param name="join">Changes in a row with the same value are one undo step (a slider dragged).</param>
    public void ChangePart(int index, Action<ClipPart> change, string? join = null) => ChangeParts([index], change, join);

    /// <summary>Changes the parts <paramref name="indices"/> alike, as one undo step.</summary>
    public void ChangeParts(IReadOnlyCollection<int> indices, Action<ClipPart> change, string? join = null)
    {
        var chosen = indices.Where(i => i >= 0 && i < _edit.Parts.Count).Distinct().ToList();
        if (chosen.Count == 0)
            return;
        var changed = chosen.Select(i =>
        {
            var copy = _edit.Parts[i].Clone();
            change(copy);
            return Normalized(copy, i);
        }).ToList();
        if (chosen.Select((i, k) => Same(_edit.Parts[i], changed[k])).All(same => same))
            return;
        Change(() =>
        {
            for (var k = 0; k < chosen.Count; k++)
                _edit.Parts[chosen[k]] = changed[k];
        }, join: join);
    }

    /// <summary>
    /// A still of the picture at <paramref name="seconds"/>: the part there is cut and the still, as long as
    /// <paramref name="length"/>, put in between. Returns the still's index, or -1.
    /// </summary>
    public int Freeze(double seconds, double length = FreezeSeconds)
    {
        var index = IndexAt(seconds);
        if (index < 0 || _edit.Parts[index].Blank)
            return -1;
        var part = _edit.Parts[index];
        var at = Math.Clamp(seconds - Starts()[index], 0, part.Length);
        var still = part.Clone();
        still.Hold = Math.Clamp(length, ClipRange.MinLength, ClipPart.MaxHold);
        still.Start = still.End = part.SourceAt(at);
        still.SpeedEnd = 0;
        still.Speed = 1;
        still.Reverse = false;
        still.Transition = ClipTransitionKind.Cut;
        var split = part.IsStill ? null : part.SplitAt(at);
        var place = split is not null ? index + 1 : at < part.Length / 2 ? index : index + 1;
        Change(() =>
        {
            if (split is { } halves)
            {
                _edit.Parts[index] = halves.Before;
                _edit.Parts.Insert(index + 1, halves.After);
            }
            _edit.Parts.Insert(place, still);
        });
        return place;
    }

    /// <summary>
    /// Takes the silent stretches out of the parts that play forwards at one speed: <paramref name="silencesOf"/>
    /// gives them for each video (from FFmpeg's silencedetect), each at least <see cref="ClipCutter.MinSilence"/> long
    /// less <see cref="ClipCutter.SilencePadding"/> next to sound. Something always stays. Returns how many were taken
    /// out; one undo puts them all back.
    /// </summary>
    public int RemoveSilences(Func<string?, IReadOnlyList<ClipRange>> silencesOf)
    {
        var parts = new List<ClipPart>();
        var removed = 0;
        foreach (var part in _edit.Parts)
        {
            if (part.Blank || part.IsStill || part.Reverse || part.IsRamp)
            {
                parts.Add(part);
                continue;
            }
            var pieces = new List<ClipPart> { part };
            foreach (var silence in silencesOf(part.Source).OrderBy(s => s.Start))
            {
                var start = silence.Start <= part.Start + 1e-6 ? part.Start : silence.Start + ClipCutter.SilencePadding;
                var end = silence.End >= part.End - 1e-6 ? part.End : silence.End - ClipCutter.SilencePadding;
                if (end - start < Math.Max(ClipCutter.MinSilence - 2 * ClipCutter.SilencePadding, ClipRange.MinLength))
                    continue;
                var next = new List<ClipPart>();
                foreach (var piece in pieces)
                {
                    if (end <= piece.Start || start >= piece.End)
                    {
                        next.Add(piece);
                        continue;
                    }
                    if (start - piece.Start >= ClipRange.MinLength)
                        next.Add(With(piece, piece.Start, start));
                    if (piece.End - end >= ClipRange.MinLength)
                        next.Add(With(piece, end, piece.End));
                }
                removed++;
                pieces = next;
            }
            for (var k = 0; k < pieces.Count - 1; k++)
                pieces[k].Transition = ClipTransitionKind.Cut;
            parts.AddRange(pieces);
        }
        if (removed == 0 || !parts.Any(p => !p.Blank))
            return 0;
        Change(() => _edit.Parts = parts);
        return removed;

        static ClipPart With(ClipPart piece, double start, double end)
        {
            var copy = piece.Clone();
            copy.Start = start;
            copy.End = end;
            return copy;
        }
    }

    // ───────────────────────── Texts ─────────────────────────

    /// <summary>Adds a text. Returns its index.</summary>
    public int AddText(ClipText text)
    {
        var copy = text.Clone();
        copy.Start = Math.Clamp(double.IsFinite(text.Start) ? text.Start : 0, 0, Math.Max(0, Length - ClipRange.MinLength));
        copy.Length = Math.Clamp(double.IsFinite(text.Length) ? text.Length : 3, ClipRange.MinLength, Math.Max(ClipRange.MinLength, Length - copy.Start));
        Change(() => _edit.Texts.Add(copy));
        return _edit.Texts.Count - 1;
    }

    /// <summary>Changes text <paramref name="index"/> (a drag is one undo step with <paramref name="join"/>).</summary>
    public void ChangeText(int index, Action<ClipText> change, string? join = null)
    {
        if (index < 0 || index >= _edit.Texts.Count)
            return;
        var copy = _edit.Texts[index].Clone();
        change(copy);
        copy.Start = double.IsFinite(copy.Start) ? Math.Clamp(copy.Start, 0, Math.Max(0, Length - ClipRange.MinLength)) : 0;
        copy.Length = double.IsFinite(copy.Length) ? Math.Max(ClipRange.MinLength, copy.Length) : 3;
        copy.X = double.IsFinite(copy.X) ? Math.Clamp(copy.X, 0, 1) : 0.5;
        copy.Y = double.IsFinite(copy.Y) ? Math.Clamp(copy.Y, 0, 1) : 0.5;
        copy.Opacity = copy.PlayOpacity;
        copy.Rotation = ClipCanvas.Angle(copy.Rotation);
        if (Same(_edit.Texts[index], copy))
            return;
        Change(() => _edit.Texts[index] = copy, join: join);
    }

    /// <summary>Takes the texts <paramref name="indices"/> away.</summary>
    public bool RemoveTexts(IReadOnlyCollection<int> indices)
    {
        var chosen = indices.Where(i => i >= 0 && i < _edit.Texts.Count).ToHashSet();
        if (chosen.Count == 0)
            return false;
        Change(() => _edit.Texts = _edit.Texts.Where((_, i) => !chosen.Contains(i)).ToList());
        return true;
    }

    // ───────────────────────── Pictures over the video ─────────────────────────

    /// <summary>Adds a picture over the video. Returns its index.</summary>
    public int AddImage(ClipImage image)
    {
        var copy = image.Clone();
        copy.Start = Math.Clamp(double.IsFinite(image.Start) ? image.Start : 0, 0, Math.Max(0, Length - ClipRange.MinLength));
        copy.Length = Math.Clamp(double.IsFinite(image.Length) ? image.Length : 5, ClipRange.MinLength, Math.Max(ClipRange.MinLength, Length - copy.Start));
        Change(() => _edit.Images.Add(copy));
        return _edit.Images.Count - 1;
    }

    /// <summary>Changes picture <paramref name="index"/> (a drag is one undo step with <paramref name="join"/>).</summary>
    public void ChangeImage(int index, Action<ClipImage> change, string? join = null)
    {
        if (index < 0 || index >= _edit.Images.Count)
            return;
        var copy = _edit.Images[index].Clone();
        change(copy);
        copy.Start = double.IsFinite(copy.Start) ? Math.Clamp(copy.Start, 0, Math.Max(0, Length - ClipRange.MinLength)) : 0;
        copy.Length = double.IsFinite(copy.Length) ? Math.Max(ClipRange.MinLength, copy.Length) : 5;
        copy.X = double.IsFinite(copy.X) ? Math.Clamp(copy.X, 0, 1) : 0.5;
        copy.Y = double.IsFinite(copy.Y) ? Math.Clamp(copy.Y, 0, 1) : 0.5;
        copy.Width = copy.PlayWidth;
        copy.Opacity = copy.PlayOpacity;
        copy.Rotation = ClipCanvas.Angle(copy.Rotation);
        if (Same(_edit.Images[index], copy))
            return;
        Change(() => _edit.Images[index] = copy, join: join);
    }

    /// <summary>Takes the pictures <paramref name="indices"/> away.</summary>
    public bool RemoveImages(IReadOnlyCollection<int> indices)
    {
        var chosen = indices.Where(i => i >= 0 && i < _edit.Images.Count).ToHashSet();
        if (chosen.Count == 0)
            return false;
        Change(() => _edit.Images = _edit.Images.Where((_, i) => !chosen.Contains(i)).ToList());
        return true;
    }

    // ───────────────────────── Sounds ─────────────────────────

    /// <summary>
    /// Adds a sound from a file of <paramref name="length"/> seconds on <paramref name="track"/> (null: A2): at
    /// <paramref name="offset"/> seconds into the clip, or right after the last sound on that track. Returns its index.
    /// </summary>
    public int AddSound(string path, double length, int? track = null, double? offset = null)
    {
        var on = Math.Max(0, track ?? 0);
        var after = _edit.Sounds.Where(s => s.Track == on).Select(s => s.Offset + s.PlayLength(Length)).DefaultIfEmpty(0).Max();
        var sound = new ClipMusic
        {
            Path = path,
            Length = Math.Max(ClipRange.MinLength, length),
            Track = on,
            Offset = Math.Clamp(offset ?? after, 0, Math.Max(0, Length - ClipRange.MinLength)),
        };
        Change(() => _edit.Sounds.Add(sound));
        return _edit.Sounds.Count - 1;
    }

    /// <summary>Changes sound <paramref name="index"/> (a drag is one undo step with <paramref name="join"/>).</summary>
    public void ChangeSound(int index, Action<ClipMusic> change, string? join = null)
    {
        if (index < 0 || index >= _edit.Sounds.Count)
            return;
        var copy = _edit.Sounds[index].Clone();
        change(copy);
        copy.Offset = double.IsFinite(copy.Offset) ? Math.Clamp(copy.Offset, 0, Math.Max(0, Length - ClipRange.MinLength)) : 0;
        copy.Volume = double.IsFinite(copy.Volume) ? Math.Clamp(Math.Round(copy.Volume, 2), 0, 2) : ClipMusic.DefaultVolume;
        copy.From = double.IsFinite(copy.From) ? Math.Clamp(copy.From, 0, Math.Max(0, copy.Length - ClipRange.MinLength)) : 0;
        copy.Play = copy.Play > 0 && double.IsFinite(copy.Play) ? Math.Max(ClipRange.MinLength, copy.Play) : 0;
        copy.Track = Math.Clamp(copy.Track, 0, 7);
        if (Same(_edit.Sounds[index], copy))
            return;
        Change(() => _edit.Sounds[index] = copy, join: join);
    }

    /// <summary>Takes the sounds <paramref name="indices"/> away.</summary>
    public bool RemoveSounds(IReadOnlyCollection<int> indices)
    {
        var chosen = indices.Where(i => i >= 0 && i < _edit.Sounds.Count).ToHashSet();
        if (chosen.Count == 0)
            return false;
        Change(() => _edit.Sounds = _edit.Sounds.Where((_, i) => !chosen.Contains(i)).ToList());
        return true;
    }

    /// <summary>
    /// Takes everything of the file <paramref name="path"/> out (a video's parts, its sounds, its pictures), as one undo
    /// step; <paramref name="source"/> is how the parts name it (null: the window's video). False when nothing of the
    /// video would be left to play.
    /// </summary>
    public bool RemoveFile(string path, string? source)
    {
        var parts = _edit.Parts.Where(p => p.Blank || !string.Equals(p.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!parts.Any(p => !p.Blank))
            return false;
        Change(() =>
        {
            _edit.Parts = parts;
            while (_edit.Parts.Count > 0 && _edit.Parts[0].Blank)
                _edit.Parts.RemoveAt(0);
            _edit.Sounds = _edit.Sounds.Where(s => !string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            _edit.Images = _edit.Images.Where(i => !string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        });
        return true;
    }

    // ───────────────────────── The whole clip ─────────────────────────

    /// <summary>Changes the clip's own settings: fades, frame, sound (a slider is one undo step with <paramref name="join"/>).</summary>
    public void ChangeClip(Action<ClipEdit> change, string? join = null)
    {
        var copy = _edit.Clone();
        change(copy);
        copy.FadeIn = ClipEdit.Fade(copy.FadeIn);
        copy.FadeOut = ClipEdit.Fade(copy.FadeOut);
        if ((copy.FadeIn, copy.FadeOut, copy.Aspect, copy.Fit, copy.Resolution, copy.Fps, copy.MaxMegabytes, copy.Separate,
                copy.LevelLoudness, copy.DuckMusic, copy.ReduceNoise, copy.LiftSpeech)
            == (_edit.FadeIn, _edit.FadeOut, _edit.Aspect, _edit.Fit, _edit.Resolution, _edit.Fps, _edit.MaxMegabytes, _edit.Separate,
                _edit.LevelLoudness, _edit.DuckMusic, _edit.ReduceNoise, _edit.LiftSpeech))
            return;
        Change(() =>
        {
            copy.Parts = _edit.Parts;
            copy.Texts = _edit.Texts;
            copy.Images = _edit.Images;
            copy.Sounds = _edit.Sounds;
            copy.Music = _edit.Music;
            _edit = copy;
        }, join: join);
    }

    /// <summary>
    /// Several changes as one: one undo step and one <see cref="Changed"/>. Nothing is remembered when nothing changed.
    /// </summary>
    public void Batch(Action changes, string? join = null)
    {
        var before = _edit.Clone();
        _batching++;
        try
        {
            changes();
        }
        finally
        {
            _batching--;
        }
        if (_batching > 0 || Same(before, _edit))
            return;
        if (join is null || join != _joining || _undo.Count == 0)
            Remember(before);
        else
            _redo.Clear();
        _joining = join;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends a drag: the next change is its own undo step even when it is alike (the same edge dragged again).
    /// </summary>
    public void Seal() => _joining = null;

    /// <summary>Takes back the last change.</summary>
    public bool Undo() => Step(_undo, _redo);

    /// <summary>Does the last undone change again.</summary>
    public bool Redo() => Step(_redo, _undo);

    // ───────────────────────── Inside ─────────────────────────

    /// <summary>A pause (black and silence) of <paramref name="seconds"/>.</summary>
    public static ClipPart Pause(double seconds) => new() { Blank = true, Start = 0, End = Math.Max(ClipRange.MinLength, seconds) };

    /// <summary><paramref name="part"/> with its settings within their limits and inside its video.</summary>
    private ClipPart Normalized(ClipPart part, int index)
    {
        part.Volume = double.IsFinite(part.Volume) ? Math.Clamp(Math.Round(part.Volume, 2), 0, ClipPart.MaxVolume) : 1;
        part.Speed = part.PlaySpeed;
        part.SpeedEnd = part.SpeedEnd > 0 && double.IsFinite(part.SpeedEnd) && Math.Abs(part.PlaySpeedEnd - part.PlaySpeed) > 1e-6
            ? part.PlaySpeedEnd : 0;
        part.Zoom = part.PlayZoom;
        part.Rotation = part.PlayRotation;
        part.FocusX = double.IsFinite(part.FocusX) ? Math.Clamp(part.FocusX, 0, 1) : 0.5;
        part.FocusY = double.IsFinite(part.FocusY) ? Math.Clamp(part.FocusY, 0, 1) : 0.5;
        part.TransitionSeconds = ClipEdit.TransitionLength(part);
        if (index == _edit.Parts.Count - 1)
            part.Transition = ClipTransitionKind.Cut;
        if (part.IsStill)
            part.Hold = Math.Clamp(part.Hold, ClipRange.MinLength, ClipPart.MaxHold);
        return part;
    }

    private static bool Same(ClipPart a, ClipPart b) =>
        (a.Start, a.End, a.Blank, a.Volume, a.Muted, a.Speed, a.Source, a.Reverse, a.Hold, a.SpeedEnd, a.Transition, a.TransitionSeconds, a.Zoom, a.FocusX, a.FocusY)
        == (b.Start, b.End, b.Blank, b.Volume, b.Muted, b.Speed, b.Source, b.Reverse, b.Hold, b.SpeedEnd, b.Transition, b.TransitionSeconds, b.Zoom, b.FocusX, b.FocusY)
        && a.Rotation == b.Rotation;

    private static bool Same(ClipText a, ClipText b) =>
        (a.Text, a.Style, a.Start, a.Length, a.X, a.Y, a.Size, a.Color, a.Font, a.Opacity, a.Rotation)
        == (b.Text, b.Style, b.Start, b.Length, b.X, b.Y, b.Size, b.Color, b.Font, b.Opacity, b.Rotation);

    private static bool Same(ClipImage a, ClipImage b) =>
        (a.Path, a.Start, a.Length, a.X, a.Y, a.Width, a.Rotation, a.Opacity) == (b.Path, b.Start, b.Length, b.X, b.Y, b.Width, b.Rotation, b.Opacity);

    private static bool Same(ClipMusic? a, ClipMusic? b) =>
        a is null ? b is null
        : b is not null && (a.Path, a.Offset, a.Volume, a.From, a.Fade, a.Track, a.Play, a.Length) == (b.Path, b.Offset, b.Volume, b.From, b.Fade, b.Track, b.Play, b.Length);

    private static bool Same(ClipEdit a, ClipEdit b) =>
        a.Parts.Count == b.Parts.Count && a.Parts.Zip(b.Parts).All(p => Same(p.First, p.Second))
        && a.Texts.Count == b.Texts.Count && a.Texts.Zip(b.Texts).All(t => Same(t.First, t.Second))
        && a.Images.Count == b.Images.Count && a.Images.Zip(b.Images).All(i => Same(i.First, i.Second))
        && a.Sounds.Count == b.Sounds.Count && a.Sounds.Zip(b.Sounds).All(s => Same(s.First, s.Second))
        && Same(a.Music, b.Music)
        && (a.FadeIn, a.FadeOut, a.Aspect, a.Fit, a.Resolution, a.Fps, a.MaxMegabytes, a.Separate, a.LevelLoudness, a.DuckMusic, a.ReduceNoise, a.LiftSpeech)
            == (b.FadeIn, b.FadeOut, b.Aspect, b.Fit, b.Resolution, b.Fps, b.MaxMegabytes, b.Separate, b.LevelLoudness, b.DuckMusic, b.ReduceNoise, b.LiftSpeech);

    private void Remember(ClipEdit state)
    {
        _undo.Push(state);
        _redo.Clear();
        if (_undo.Count > UndoDepth)
        {
            var kept = _undo.Take(UndoDepth).Reverse().ToList();
            _undo.Clear();
            foreach (var item in kept)
                _undo.Push(item);
        }
    }

    private void Change(Action change, string? join = null)
    {
        if (_batching > 0)
        {
            change();
            return;
        }
        var before = _edit.Clone();
        change();
        // The last part has no next one to go over into.
        if (_edit.Parts.Count > 0)
            _edit.Parts[^1].Transition = ClipTransitionKind.Cut;
        if (join is null || join != _joining || _undo.Count == 0)
            Remember(before);
        else
            _redo.Clear();
        _joining = join;
        Changed?.Invoke();
    }

    private bool Step(Stack<ClipEdit> from, Stack<ClipEdit> to)
    {
        if (from.Count == 0)
            return false;
        to.Push(_edit.Clone());
        _edit = from.Pop();
        _joining = null;
        Changed?.Invoke();
        return true;
    }
}
