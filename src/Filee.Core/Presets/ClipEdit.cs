// What "Cut a clip" writes: parts of one video one after the other, each with its own volume and speed (its sound
// muted or not), pauses (black and silence) where the user removed a part without closing the gap, music from another
// file under it, and a fade in and out. The clip window
// edits it with ClipCutter; the conversion writes it with FFmpeg (MediaEncoding.ClipGraph).
//
// The editor (ClipProject) writes more into it, marked with Editor: parts from several videos in any order, played
// backwards, held as a still, or sped up and slowed down smoothly; a transition between two parts; a closer look at the
// picture and where it sits in a frame of another shape; texts over the picture; and the sound evened out. Those are
// written by MediaEncoding.EditorGraph; a clip without Editor is written as before.

using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>How one part of an editor clip goes over into the next.</summary>
public enum ClipTransitionKind
{
    /// <summary>Straight on (the default).</summary>
    Cut,

    /// <summary>The pictures and the sound blend into each other.</summary>
    Crossfade,

    /// <summary>The picture goes dark and comes back with the next part; the sound blends.</summary>
    Black,

    /// <summary>The picture cuts; only the sound fades out and in, so the join isn't heard.</summary>
    Sound,
}

/// <summary>One part of a clip: a stretch of the video, or a pause of the same length.</summary>
public sealed class ClipPart
{
    /// <summary>Slowest and fastest speed of a part.</summary>
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    /// <summary>Loudest volume of a part: four times as loud (+12 dB). 0 is silent.</summary>
    public const double MaxVolume = 4;

    /// <summary>The longest still (<see cref="Hold"/>) and transition, in seconds.</summary>
    public const double MaxHold = 30, MaxTransition = 2;

    /// <summary>The closest look at the picture (<see cref="Zoom"/>): three times as close; and the smallest, a fifth.</summary>
    public const double MaxZoom = 3, MinZoom = 0.2;

    /// <summary>The steps a smooth speed change is written in (each at its own speed; together exactly as long).</summary>
    public const int RampSteps = 10;

    /// <summary>The speeds the clip window offers.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>Where the part starts in the video, in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Where the part ends in the video, in seconds.</summary>
    public double End { get; set; }

    /// <summary>
    /// A part removed with the magnet off: black and silence for as long as it played, so what follows stays where
    /// it was.
    /// </summary>
    public bool Blank { get; set; }

    /// <summary>How loud, 1 as recorded (0 silent, <see cref="MaxVolume"/> loudest).</summary>
    public double Volume { get; set; } = 1;

    /// <summary>The part's own sound is cut out (its picture plays): silence, or only the music.</summary>
    public bool Muted { get; set; }

    /// <summary>How fast, 1 as recorded (<see cref="MinSpeed"/> to <see cref="MaxSpeed"/>); the sound keeps its pitch.</summary>
    public double Speed { get; set; } = 1;

    // ───── The editor's (with ClipEdit.Editor) ─────

    /// <summary>The file the part comes from; null for the video the window was opened with (the conversion's input).</summary>
    public string? Source { get; set; }

    /// <summary>Played backwards, its sound too.</summary>
    public bool Reverse { get; set; }

    /// <summary>
    /// A still: the picture at <see cref="Start"/> shown for this many seconds, silent (0 for a part that moves).
    /// </summary>
    public double Hold { get; set; }

    /// <summary>
    /// The speed at the part's end: it changes smoothly from <see cref="Speed"/> to this (0 keeps <see cref="Speed"/>).
    /// </summary>
    public double SpeedEnd { get; set; }

    /// <summary>How this part goes over into the next one.</summary>
    public ClipTransitionKind Transition { get; set; }

    /// <summary>How long the transition into the next part takes, in seconds.</summary>
    public double TransitionSeconds { get; set; } = 0.5;

    /// <summary>How large the picture is: 1 as it fits (or fills) the frame, <see cref="MinZoom"/> to <see cref="MaxZoom"/>.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>The picture turned clockwise, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>Where the picture's middle is in the frame, across and down: 0.5 the middle, 0 the left (top) edge.</summary>
    public double FocusX { get; set; } = 0.5;

    /// <summary>The same as <see cref="FocusX"/>, down.</summary>
    public double FocusY { get; set; } = 0.5;

    /// <summary><see cref="Speed"/> as written: within its limits, 1 for a value that makes no sense.</summary>
    [JsonIgnore]
    public double PlaySpeed => Valid(Speed);

    /// <summary><see cref="SpeedEnd"/> as written: <see cref="PlaySpeed"/> when there is no change of speed.</summary>
    [JsonIgnore]
    public double PlaySpeedEnd => SpeedEnd > 0 && double.IsFinite(SpeedEnd) ? Valid(SpeedEnd) : PlaySpeed;

    /// <summary>True when the speed changes smoothly over the part.</summary>
    [JsonIgnore]
    public bool IsRamp => !Blank && !IsStill && Math.Abs(PlaySpeedEnd - PlaySpeed) > 1e-6;

    /// <summary>True for a still (<see cref="Hold"/>).</summary>
    [JsonIgnore]
    public bool IsStill => !Blank && Hold > 0 && double.IsFinite(Hold);

    /// <summary><see cref="Zoom"/> as written: <see cref="MinZoom"/> to <see cref="MaxZoom"/>.</summary>
    [JsonIgnore]
    public double PlayZoom => double.IsFinite(Zoom) ? Math.Clamp(Zoom, MinZoom, MaxZoom) : 1;

    /// <summary><see cref="Rotation"/> as written: −180 to 180 degrees.</summary>
    [JsonIgnore]
    public double PlayRotation => ClipCanvas.Angle(Rotation);

    /// <summary><see cref="Volume"/> as written: within its limits, 1 for a value that makes no sense, 0 when muted.</summary>
    [JsonIgnore]
    public double PlayVolume => Muted ? 0 : double.IsFinite(Volume) && Volume >= 0 ? Math.Min(Volume, MaxVolume) : 1;

    /// <summary>How much of the video the part uses, in seconds.</summary>
    [JsonIgnore]
    public double SourceLength => Math.Max(0, End - Start);

    /// <summary>How long the part plays in the clip, in seconds (its stretch of the video at its speed).</summary>
    [JsonIgnore]
    public double Length => IsStill ? Math.Min(Hold, MaxHold) : IsRamp ? RampLength(SourceLength, PlaySpeed, PlaySpeedEnd) : SourceLength / PlaySpeed;

    /// <summary>True when the part plays as recorded: full volume, normal speed, not a pause.</summary>
    [JsonIgnore]
    public bool IsPlain => !Blank && !Muted && PlayVolume == 1 && PlaySpeed == 1 && !IsRamp && !IsStill && !Reverse && PlayZoom == 1 && PlayRotation == 0;

    public ClipPart Clone() => (ClipPart)MemberwiseClone();

    private static double Valid(double speed) => double.IsFinite(speed) && speed > 0 ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;

    /// <summary>
    /// How long <paramref name="source"/> seconds of video play when the speed changes evenly over them from
    /// <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static double RampLength(double source, double from, double to) =>
        Math.Abs(to - from) < 1e-9 ? source / from : source / (to - from) * Math.Log(to / from);

    /// <summary>
    /// The speed of a smooth change from <paramref name="from"/> to <paramref name="to"/> over <paramref name="source"/>
    /// seconds of video, <paramref name="seconds"/> after it started playing.
    /// </summary>
    public static double RampSpeedAt(double source, double from, double to, double seconds) =>
        Math.Abs(to - from) < 1e-9 || source <= 0 ? from : Math.Clamp(from * Math.Exp(seconds * (to - from) / source), Math.Min(from, to), Math.Max(from, to));

    /// <summary>
    /// The steps the part is written in, in the order they play: how far into its stretch of the video (as it plays,
    /// so from the end for a part played backwards) each starts and ends, and its speed. One step at
    /// <see cref="PlaySpeed"/>, or <see cref="RampSteps"/> for a smooth change, together exactly <see cref="Length"/>.
    /// </summary>
    public IReadOnlyList<(double From, double To, double Speed)> Steps()
    {
        var length = SourceLength;
        if (!IsRamp)
            return [(0, length, PlaySpeed)];
        var steps = new List<(double, double, double)>(RampSteps);
        var (from, to) = (PlaySpeed, PlaySpeedEnd);
        for (var k = 0; k < RampSteps; k++)
        {
            var a = length * k / RampSteps;
            var b = length * (k + 1) / RampSteps;
            // The speed at each end of the step, then how long the step plays exactly.
            var speedA = from + (to - from) * a / length;
            var speedB = from + (to - from) * b / length;
            var time = RampLength(b - a, speedA, speedB);
            steps.Add((a, b, (b - a) / time));
        }
        return steps;
    }

    /// <summary>
    /// Where in its video the part is <paramref name="seconds"/> after it started playing (for its picture there).
    /// </summary>
    public double SourceAt(double seconds)
    {
        if (IsStill || Blank)
            return Start;
        var t = Math.Clamp(seconds, 0, Length);
        double played;
        if (IsRamp)
        {
            var speed = RampSpeedAt(SourceLength, PlaySpeed, PlaySpeedEnd, t);
            played = SourceLength * (speed - PlaySpeed) / (PlaySpeedEnd - PlaySpeed);
        }
        else
            played = t * PlaySpeed;
        played = Math.Clamp(played, 0, SourceLength);
        return Reverse ? End - played : Start + played;
    }

    /// <summary>
    /// The part cut in two <paramref name="seconds"/> after it starts playing: what plays before and after, each with
    /// the part's settings (a smooth change of speed split where it is; a transition stays with the second). Null when
    /// either would be shorter than <see cref="ClipRange.MinLength"/>.
    /// </summary>
    public (ClipPart Before, ClipPart After)? SplitAt(double seconds) =>
        seconds < ClipRange.MinLength || Length - seconds < ClipRange.MinLength ? null : Cut(seconds);

    /// <summary>What plays of the part from <paramref name="seconds"/> after it starts on (for playing from there).</summary>
    public ClipPart Rest(double seconds) => seconds <= 0 ? Clone() : Cut(Math.Min(seconds, Length)).After;

    private (ClipPart Before, ClipPart After) Cut(double seconds)
    {
        var length = Length;
        var before = Clone();
        var after = Clone();
        before.Transition = ClipTransitionKind.Cut;
        if (IsStill)
        {
            before.Hold = seconds;
            after.Hold = length - seconds;
            return (before, after);
        }
        if (Blank)
        {
            var at = Start + seconds * PlaySpeed;
            before.End = at;
            after.Start = at;
            return (before, after);
        }
        var source = SourceAt(seconds);
        if (IsRamp)
        {
            var speed = RampSpeedAt(SourceLength, PlaySpeed, PlaySpeedEnd, seconds);
            before.SpeedEnd = speed;
            after.Speed = speed;
        }
        if (Reverse)
        {
            before.Start = source;
            after.End = source;
        }
        else
        {
            before.End = source;
            after.Start = source;
        }
        return (before, after);
    }
}

/// <summary>Music (or any sound) from another file played under a clip, its own track.</summary>
public sealed class ClipMusic
{
    /// <summary>How loud the music is to start with: under the clip's own sound, not over it.</summary>
    public const double DefaultVolume = 0.6;

    /// <summary>Seconds the music fades in and out at its ends, so it doesn't start or stop with a jump.</summary>
    public const double FadeSeconds = 1;

    /// <summary>The file the music comes from (any audio, or the sound of a video).</summary>
    public string Path { get; set; } = "";

    /// <summary>How far into the clip the music starts, in seconds.</summary>
    public double Offset { get; set; }

    /// <summary>Where in the file it starts playing, in seconds.</summary>
    public double From { get; set; }

    /// <summary>How long the file is, in seconds.</summary>
    public double Length { get; set; }

    /// <summary>How loud, 1 as recorded (0 to 2).</summary>
    public double Volume { get; set; } = DefaultVolume;

    /// <summary>Fade in and out at its ends (<see cref="FadeSeconds"/>).</summary>
    public bool Fade { get; set; } = true;

    /// <summary>The editor's sound track it is on: 0 for A2, 1 for A3, and so on.</summary>
    public int Track { get; set; }

    /// <summary>How long it plays at most, in seconds (0: until the file or the clip ends).</summary>
    public double Play { get; set; }

    /// <summary><see cref="Volume"/> as written: 0 to 2, the default for a value that makes no sense.</summary>
    [JsonIgnore]
    public double PlayVolume => double.IsFinite(Volume) && Volume >= 0 ? Math.Min(Volume, 2) : DefaultVolume;

    /// <summary>How long the music plays in a clip of <paramref name="clipLength"/> seconds: until it or the clip ends.</summary>
    public double PlayLength(double clipLength) =>
        Math.Max(0, Math.Min(Math.Min(Length - From, clipLength - Offset), Play > 0 && double.IsFinite(Play) ? Play : double.MaxValue));

    /// <summary>
    /// The sound as written under a clip of <paramref name="clipLength"/> seconds: inside its file and the clip, its volume
    /// within limits; null when nothing of it plays.
    /// </summary>
    public ClipMusic? Clamped(double clipLength)
    {
        if (Path.Length == 0 || !double.IsFinite(Offset) || !double.IsFinite(From) || !double.IsFinite(Length))
            return null;
        var kept = Clone();
        kept.Offset = Math.Max(0, kept.Offset);
        kept.From = Math.Clamp(kept.From, 0, Math.Max(0, kept.Length));
        kept.Volume = kept.PlayVolume;
        kept.Track = Math.Max(0, kept.Track);
        kept.Play = kept.Play > 0 && double.IsFinite(kept.Play) ? kept.Play : 0;
        return kept.PlayLength(clipLength) >= ClipRange.MinLength ? kept : null;
    }

    /// <summary>What plays of it from <paramref name="seconds"/> into the clip on (it keeps its place against the pictures).</summary>
    public ClipMusic After(double seconds)
    {
        var rest = Clone();
        var played = Math.Max(0, seconds - Offset);
        rest.From += played;
        rest.Offset = Math.Max(0, Offset - Math.Max(0, seconds));
        if (rest.Play > 0)
            rest.Play = Math.Max(0.001, rest.Play - played);
        return rest;
    }

    public ClipMusic Clone() => (ClipMusic)MemberwiseClone();
}

/// <summary>A clip of a video: its parts in the order they play, music under them, and how it fades in and out.</summary>
public sealed class ClipEdit
{
    /// <summary>The longest fade, in seconds.</summary>
    public const double MaxFade = 10;

    /// <summary>The parts, in the order they play (also their order in the video).</summary>
    public List<ClipPart> Parts { get; set; } = [];

    /// <summary>Seconds the clip fades in from black and silence; 0 for none.</summary>
    public double FadeIn { get; set; }

    /// <summary>Seconds the clip fades out to black and silence; 0 for none.</summary>
    public double FadeOut { get; set; }

    /// <summary>Music under the clip, or null.</summary>
    public ClipMusic? Music { get; set; }

    // ───── The editor's ─────

    /// <summary>
    /// Made in the editor: the parts may come from several videos in any order, and everything below applies. Written
    /// with the editor's filter graph.
    /// </summary>
    public bool Editor { get; set; }

    /// <summary>Texts over the picture.</summary>
    public List<ClipText> Texts { get; set; } = [];

    /// <summary>Pictures over the video (a logo, a photo, a sticker).</summary>
    public List<ClipImage> Images { get; set; } = [];

    /// <summary>Sounds from other files under the clip, on the editor's tracks A2, A3, … (besides <see cref="Music"/>).</summary>
    public List<ClipMusic> Sounds { get; set; } = [];

    /// <summary>Every sound under the clip: <see cref="Music"/> and <see cref="Sounds"/>.</summary>
    [JsonIgnore]
    public IEnumerable<ClipMusic> AllSounds => Music is null ? Sounds : Sounds.Prepend(Music);

    /// <summary>The frame's shape, one of <see cref="ClipCanvas.Aspects"/>; null for the first video's.</summary>
    public string? Aspect { get; set; }

    /// <summary>How a picture of another shape fills the frame.</summary>
    public ClipFit Fit { get; set; }

    /// <summary>The frame's shorter side in pixels; 0 for the first video's.</summary>
    public int Resolution { get; set; }

    /// <summary>Frames per second; 0 for the first video's.</summary>
    public double Fps { get; set; }

    /// <summary>The largest file, in megabytes (1024 × 1024 bytes); 0 for no limit.</summary>
    public double MaxMegabytes { get; set; }

    /// <summary>Each part that plays is written to its own file (several highlights of a video).</summary>
    public bool Separate { get; set; }

    /// <summary>The sound evened out to one loudness (EBU R128, as streaming sites play it).</summary>
    public bool LevelLoudness { get; set; }

    /// <summary>The music goes quieter while the video's own sound (speech) plays.</summary>
    public bool DuckMusic { get; set; }

    /// <summary>Steady background noise (hum, wind, fans) taken out of the video's own sound.</summary>
    public bool ReduceNoise { get; set; }

    /// <summary>Quiet speech lifted and loud peaks tamed (a compressor on the video's own sound).</summary>
    public bool LiftSpeech { get; set; }

    /// <summary>How long the clip plays, in seconds (transitions overlap the parts they join).</summary>
    [JsonIgnore]
    public double Length => Parts.Sum(p => p.Length) - Enumerable.Range(0, Math.Max(0, Parts.Count - 1)).Sum(Overlap);

    /// <summary>Where the clip starts in the video (its first part that isn't a pause).</summary>
    [JsonIgnore]
    public double Start => Parts.FirstOrDefault(p => !p.Blank)?.Start ?? 0;

    /// <summary>Where the clip ends in the video (its last part that isn't a pause).</summary>
    [JsonIgnore]
    public double End => Parts.LastOrDefault(p => !p.Blank)?.End ?? 0;

    /// <summary>
    /// True for one stretch of the video as recorded, without fades: it can be copied as it is (cut at key frames).
    /// Anything else is encoded.
    /// </summary>
    [JsonIgnore]
    public bool IsPlainCut => !Editor && Parts.Count == 1 && Parts[0].IsPlain && Fade(FadeIn) == 0 && Fade(FadeOut) == 0 && Music is null && Sounds.Count == 0;

    /// <summary>A fade as written: 0 to <see cref="MaxFade"/> seconds.</summary>
    public static double Fade(double seconds) => double.IsFinite(seconds) ? Math.Clamp(seconds, 0, MaxFade) : 0;

    /// <summary>One stretch of the video, as recorded.</summary>
    public static ClipEdit Of(ClipRange range) => new() { Parts = [new ClipPart { Start = range.Start, End = range.End }] };

    public ClipEdit Clone()
    {
        var copy = (ClipEdit)MemberwiseClone();
        copy.Parts = Parts.Select(p => p.Clone()).ToList();
        copy.Texts = Texts.Select(t => t.Clone()).ToList();
        copy.Images = Images.Select(i => i.Clone()).ToList();
        copy.Sounds = Sounds.Select(s => s.Clone()).ToList();
        copy.Music = Music?.Clone();
        return copy;
    }

    /// <summary>
    /// Seconds part <paramref name="index"/> and the next one play at once in their transition (a crossfade or one
    /// through black): its length, at most half of either part.
    /// </summary>
    public double Overlap(int index)
    {
        if (!Editor || index < 0 || index + 1 >= Parts.Count)
            return 0;
        var part = Parts[index];
        if (part.Transition is not (ClipTransitionKind.Crossfade or ClipTransitionKind.Black))
            return 0;
        return Math.Min(TransitionLength(part), Math.Min(part.Length, Parts[index + 1].Length) / 2);
    }

    /// <summary>A part's transition as written: 0.1 to <see cref="ClipPart.MaxTransition"/> seconds.</summary>
    public static double TransitionLength(ClipPart part) =>
        double.IsFinite(part.TransitionSeconds) ? Math.Clamp(part.TransitionSeconds, ClipRange.MinLength, ClipPart.MaxTransition) : 0.5;

    /// <summary>Where each part starts playing in the clip, in seconds.</summary>
    public double[] PartStarts()
    {
        var starts = new double[Parts.Count];
        var at = 0.0;
        for (var i = 0; i < Parts.Count; i++)
        {
            starts[i] = at;
            at += Parts[i].Length - Overlap(i);
        }
        return starts;
    }

    /// <summary>
    /// The clip as it can be written from a video of <paramref name="duration"/> seconds (null when unknown): every part
    /// inside the video and at least <see cref="ClipRange.MinLength"/> long, in order and not overlapping (a part that
    /// would be left empty is dropped), speeds and volumes within their limits, and fades no longer than half the clip.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing of the video is left to play.</exception>
    public ClipEdit Clamped(double? duration) => Clamped(source => source is null ? duration : null);

    /// <summary>
    /// <see cref="Clamped(double?)"/> for parts from several videos: <paramref name="durationOf"/> tells how long each
    /// video is (null for the window's video, the conversion's input; null back when unknown). An editor clip keeps
    /// its parts in any order (they may use the same stretch twice), its texts within the clip, and its frame as
    /// written.
    /// </summary>
    public ClipEdit Clamped(Func<string?, double?> durationOf)
    {
        var parts = new List<ClipPart>();
        var last = 0.0;
        foreach (var part in Parts)
        {
            if (!double.IsFinite(part.Start) || !double.IsFinite(part.End))
                continue;
            var known = durationOf(part.Blank ? null : part.Source) is > 0 and var d && double.IsFinite(d) ? d : (double?)null;
            var start = Math.Max(part.Start, part.Blank || Editor ? part.Start : last);
            var end = part.End;
            if (known is { } length && !part.Blank)
            {
                start = Math.Min(start, length);
                end = Math.Min(end, length);
            }
            start = Math.Max(0, start);
            var copy = part.Clone();
            copy.Start = start;
            copy.End = end;
            copy.Volume = part.Muted ? part.Volume : part.PlayVolume;
            copy.Speed = part.PlaySpeed;
            if (Editor)
            {
                copy.SpeedEnd = part.IsRamp ? part.PlaySpeedEnd : 0;
                copy.Zoom = part.PlayZoom;
                copy.Rotation = part.PlayRotation;
                copy.FocusX = double.IsFinite(part.FocusX) ? Math.Clamp(part.FocusX, 0, 1) : 0.5;
                copy.FocusY = double.IsFinite(part.FocusY) ? Math.Clamp(part.FocusY, 0, 1) : 0.5;
                copy.TransitionSeconds = TransitionLength(part);
                if (part.IsStill)
                {
                    // A still needs one picture of the video, not a stretch of it.
                    copy.Hold = Math.Clamp(part.Hold, ClipRange.MinLength, ClipPart.MaxHold);
                    copy.Start = known is { } video ? Math.Min(start, Math.Max(0, video - 0.05)) : start;
                    copy.End = copy.Start;
                    parts.Add(copy);
                    continue;
                }
                copy.Hold = 0;
            }
            else
            {
                // A quick cut knows nothing of the editor's settings: written as before.
                copy.Source = null;
                copy.Reverse = false;
                copy.Hold = 0;
                copy.SpeedEnd = 0;
                copy.Transition = ClipTransitionKind.Cut;
                copy.Zoom = 1;
                copy.Rotation = 0;
                copy.FocusX = copy.FocusY = 0.5;
            }
            if (end - start < ClipRange.MinLength)
                continue;
            parts.Add(copy);
            if (!part.Blank)
                last = end;
        }
        if (!parts.Any(p => !p.Blank))
            throw new InvalidOperationException("The clip has no part of the video left.");
        // Pauses only between parts that play: at either end they would only be black.
        while (parts[0].Blank)
            parts.RemoveAt(0);
        while (parts[^1].Blank)
            parts.RemoveAt(parts.Count - 1);
        parts[^1].Transition = ClipTransitionKind.Cut;
        var clip = new ClipEdit { Parts = parts };
        if (Editor)
        {
            clip.Editor = true;
            clip.Aspect = ClipCanvas.Aspects.Contains(Aspect) ? Aspect : null;
            clip.Fit = Enum.IsDefined(Fit) ? Fit : ClipFit.Bars;
            clip.Resolution = Resolution is >= ClipCanvas.MinResolution and <= ClipCanvas.MaxResolution ? Resolution : 0;
            clip.Fps = Fps is >= 1 and <= 120 ? Fps : 0;
            clip.MaxMegabytes = MaxMegabytes is > 0 and < 100_000 ? MaxMegabytes : 0;
            (clip.Separate, clip.LevelLoudness, clip.DuckMusic, clip.ReduceNoise, clip.LiftSpeech) =
                (Separate, LevelLoudness, DuckMusic, ReduceNoise, LiftSpeech);
        }
        var total = clip.Length;
        var half = total / 2;
        clip.FadeIn = Math.Min(Fade(FadeIn), half);
        clip.FadeOut = Math.Min(Fade(FadeOut), half);
        if (Editor)
        {
            foreach (var text in Texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text) || !double.IsFinite(text.Start) || !double.IsFinite(text.Length))
                    continue;
                var start = Math.Clamp(text.Start, 0, total);
                var end = Math.Min(total, text.Start + text.Length);
                if (end - start < ClipRange.MinLength)
                    continue;
                var kept = text.Clone();
                kept.Start = start;
                kept.Length = end - start;
                kept.X = double.IsFinite(text.X) ? Math.Clamp(text.X, 0, 1) : 0.5;
                kept.Y = double.IsFinite(text.Y) ? Math.Clamp(text.Y, 0, 1) : 0.5;
                kept.Size = text.PlaySize;
                kept.Opacity = text.PlayOpacity;
                kept.Rotation = ClipCanvas.Angle(text.Rotation);
                clip.Texts.Add(kept);
            }
            foreach (var image in Images)
            {
                if (string.IsNullOrEmpty(image.Path) || !double.IsFinite(image.Start) || !double.IsFinite(image.Length))
                    continue;
                var start = Math.Clamp(image.Start, 0, total);
                var end = Math.Min(total, image.Start + image.Length);
                if (end - start < ClipRange.MinLength)
                    continue;
                var kept = image.Clone();
                kept.Start = start;
                kept.Length = end - start;
                kept.X = double.IsFinite(image.X) ? Math.Clamp(image.X, 0, 1) : 0.5;
                kept.Y = double.IsFinite(image.Y) ? Math.Clamp(image.Y, 0, 1) : 0.5;
                kept.Width = image.PlayWidth;
                kept.Opacity = image.PlayOpacity;
                kept.Rotation = ClipCanvas.Angle(image.Rotation);
                clip.Images.Add(kept);
            }
            clip.Sounds = Sounds.Select(s => s.Clamped(total)).OfType<ClipMusic>().ToList();
        }
        // Music only where it plays: from 0 into the clip, from a place in its file, for some time.
        clip.Music = Music?.Clamped(total);
        return clip;
    }

    /// <summary>
    /// The rest of the clip from <paramref name="seconds"/> into it on, for playing it from there: the part playing
    /// then starts where it is, the parts before it are left out, the fade in only where it is still running.
    /// </summary>
    public ClipEdit From(double seconds)
    {
        var rest = Clone();
        rest.Parts = [];
        rest.Texts = [];
        rest.Images = [];
        // The music keeps its place against the pictures: what has played of it is left out.
        rest.Music = Music?.After(seconds);
        rest.Sounds = Sounds.Where(s => s.Offset + s.PlayLength(double.MaxValue) > seconds).Select(s => s.After(seconds)).ToList();
        var skip = Math.Max(0, seconds);
        var starts = PartStarts();
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            var start = starts[i];
            var length = part.Length;
            // A part whose turn is over (or that is only still blending into the next one) is left out.
            if (start + length - Overlap(i) <= skip && i < Parts.Count - 1 || start + length <= skip)
                continue;
            rest.Parts.Add(start < skip ? part.Rest(skip - start) : part.Clone());
        }
        foreach (var text in Texts)
        {
            if (text.Start + text.Length <= skip)
                continue;
            var copy = text.Clone();
            copy.Start = Math.Max(0, text.Start - skip);
            copy.Length = text.Start + text.Length - skip - copy.Start;
            rest.Texts.Add(copy);
        }
        foreach (var image in Images)
        {
            if (image.Start + image.Length <= skip)
                continue;
            var copy = image.Clone();
            copy.Start = Math.Max(0, image.Start - skip);
            copy.Length = image.Start + image.Length - skip - copy.Start;
            rest.Images.Add(copy);
        }
        rest.FadeIn = Math.Max(0, Fade(FadeIn) - skip);
        return rest;
    }

    /// <summary>
    /// Where in the video the clip is at <paramref name="seconds"/> into it, for the playhead on the timeline; in a
    /// pause, where the pause stands.
    /// </summary>
    public double SourceTimeAt(double seconds)
    {
        var at = 0.0;
        foreach (var part in Parts)
        {
            var length = part.Length;
            if (seconds <= at + length)
                return part.Start + Math.Max(0, seconds - at) * part.PlaySpeed;
            at += length;
        }
        return Parts.Count > 0 ? Parts[^1].End : 0;
    }

    /// <summary>How far into the clip the video at <paramref name="sourceTime"/> plays (the next part's start when it is cut out).</summary>
    public double ClipTimeAt(double sourceTime)
    {
        var at = 0.0;
        foreach (var part in Parts)
        {
            if (sourceTime < part.Start)
                return at;
            if (sourceTime <= part.End)
                return at + (sourceTime - part.Start) / part.PlaySpeed;
            at += part.Length;
        }
        return at;
    }
}
