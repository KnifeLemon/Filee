// The editor's clip (ClipEdit.Editor) as one FFmpeg filter graph: every part read from its own video (a part played
// backwards in short chunks, so it never holds more than a few seconds of pictures), sped up, slowed down or held as a
// still, placed in the frame (fitted with bars, cropped, or over a blurred copy of itself; zoomed in where its focus
// says), joined by cuts or transitions, with texts over the picture, and the sound cleaned, evened out and mixed with
// the music. The same graph writes the clip and plays it in the editor (smaller, without the texts, which the editor
// draws itself so they can be dragged).

using System.Globalization;
using System.Text;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>A video an editor clip uses: its file and what FFmpeg says of it.</summary>
internal sealed record EditorSource(string Path, MediaInfo Info)
{
    /// <summary>Its picture's size as shown (rotation and pixel aspect applied).</summary>
    public (int Width, int Height) Size => Info.Video is { } video ? MediaEncoding.ClipSize(video, 0) : (16, 16);
}

internal static partial class MediaEncoding
{
    /// <summary>A part played backwards is read in chunks this long, each turned round on its own.</summary>
    internal const double ReverseChunk = 2;

    /// <summary>Seconds of fade at a "sound only" transition, at most.</summary>
    private const double SoundTransition = 0.3;

    /// <summary>
    /// The graph that plays <paramref name="edit"/> (an editor clip, already clamped): its picture
    /// <paramref name="width"/> × <paramref name="height"/> at <paramref name="fps"/> in <c>[v]</c>, its sound 48 kHz
    /// stereo in <c>[a]</c>.
    /// </summary>
    /// <param name="sourceOf">The video of a part (null: the window's video).</param>
    /// <param name="video">False for the sound alone.</param>
    /// <param name="audio">The parts' own sound is used (else they are silent).</param>
    /// <param name="music">The clip's music is mixed in.</param>
    /// <param name="textFolder">Where the texts' files are written (null: the texts are left out).</param>
    /// <param name="sound">False for the pictures alone.</param>
    /// <param name="layers">
    /// The texts and pictures the editor drew (<see cref="ClipText.Rendered"/>, <see cref="ClipImage.Rendered"/>) are
    /// laid over the video (false for the preview, which draws them itself).
    /// </param>
    internal static ClipGraph EditorGraphOf(ClipEdit edit, Func<string?, EditorSource> sourceOf, int width, int height, string fps,
        string pixelFormat = "yuv420p", bool video = true, bool audio = true, bool music = true, string? textFolder = null, bool sound = true,
        bool layers = false)
    {
        var songs = sound && music ? edit.AllSounds.ToList() : [];
        var hasSound = sound && (audio || songs.Count > 0);
        if (!video && !hasSound)
            throw new ArgumentException("A clip's graph needs pictures or sound.", nameof(video));
        if (!edit.Parts.Any(p => !p.Blank))
            throw new InvalidOperationException("The clip has no part of a video.");

        var inputs = new List<string>();
        var graph = new StringBuilder();
        var parts = edit.Parts;
        var many = parts.Count(p => !p.Blank) > 6;

        int Input(string path, double start, double? length)
        {
            var index = inputs.Count(a => a == "-i");
            if (many)
                inputs.AddRange(["-threads", "2"]);
            inputs.AddRange(["-ss", ClipRange.Ffmpeg(Math.Max(0, start))]);
            if (length is { } seconds)
                inputs.AddRange(["-t", ClipRange.Ffmpeg(Math.Max(0.05, seconds))]);
            inputs.AddRange(["-i", path]);
            return index;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var length = Seconds(part.Length);
            if (part.Blank)
            {
                if (video)
                    graph.Append($"color=c=black:s={width}x{height}:r={fps}:d={length},format={pixelFormat},setsar=1,settb=AVTB[p{i}v];");
                if (hasSound)
                    graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={length}[p{i}a];");
                continue;
            }

            var source = sourceOf(part.Source);
            var picture = video ? Picture(source, part, edit.Fit, width, height) : default;
            var ownSound = hasSound && audio && source.Info.Audio is not null && part.PlayVolume > 0 && !part.IsStill;

            // The inputs: one for the part, or one per chunk of a part played backwards, last chunk first.
            var chunks = new List<int>();
            if (part.IsStill)
                chunks.Add(Input(source.Path, part.Start, 1));
            else if (part.Reverse)
            {
                var count = Math.Max(1, (int)Math.Ceiling(part.SourceLength / ReverseChunk - 1e-9));
                for (var c = count - 1; c >= 0; c--)
                {
                    var from = part.Start + c * ReverseChunk;
                    chunks.Add(Input(source.Path, from, Math.Min(ReverseChunk, part.End - from)));
                }
            }
            else
                chunks.Add(Input(source.Path, part.Start, part.SourceLength));

            if (video)
            {
                // Read, cropped and scaled to what the frame shows of it, backwards per chunk.
                var read = $"setpts=PTS-STARTPTS,{picture.Scale}" + (part.Reverse && !part.IsStill ? ",reverse,setpts=PTS-STARTPTS" : "");
                if (part.IsStill)
                    graph.Append($"[{chunks[0]}:V:0]trim=end_frame=1,{read},fps={fps},tpad=stop_mode=clone:stop_duration={length}[p{i}r];");
                else if (chunks.Count == 1)
                    graph.Append($"[{chunks[0]}:V:0]{read}[p{i}r];");
                else
                {
                    for (var c = 0; c < chunks.Count; c++)
                        graph.Append($"[{chunks[c]}:V:0]{read}[p{i}c{c}];");
                    for (var c = 0; c < chunks.Count; c++)
                        graph.Append($"[p{i}c{c}]");
                    graph.Append($"concat=n={chunks.Count}:v=1:a=0[p{i}r];");
                }
                var timed = Speed(graph, $"p{i}r", $"p{i}t", part, video: true);
                graph.Append($"[{timed}]fps={fps}[p{i}f];");
                var placed = Frame(graph, $"p{i}f", $"p{i}p", picture, edit.Fit, width, height, fps);
                graph.Append($"[{placed}]format={pixelFormat},setsar=1,tpad=stop_mode=clone:stop_duration=1,trim=duration={length},setpts=PTS-STARTPTS,settb=AVTB[p{i}v];");
            }

            if (hasSound)
            {
                if (!ownSound)
                {
                    graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={length}[p{i}a];");
                    continue;
                }
                var read = "asetpts=PTS-STARTPTS" + (part.Reverse ? ",areverse,asetpts=PTS-STARTPTS" : "");
                if (chunks.Count == 1)
                    graph.Append($"[{chunks[0]}:a:0]{read}[p{i}ar];");
                else
                {
                    for (var c = 0; c < chunks.Count; c++)
                        graph.Append($"[{chunks[c]}:a:0]{read}[p{i}ac{c}];");
                    for (var c = 0; c < chunks.Count; c++)
                        graph.Append($"[p{i}ac{c}]");
                    graph.Append($"concat=n={chunks.Count}:v=0:a=1[p{i}ar];");
                }
                var timed = Speed(graph, $"p{i}ar", $"p{i}at", part, video: false);
                graph.Append($"[{timed}]");
                if (part.PlayVolume != 1)
                    graph.Append($"volume={Seconds(part.PlayVolume)},");
                graph.Append(ClipAudioFormat);
                // "Sound only" transitions: a short fade out at this end, in at the next part's start.
                var fadeIn = i > 0 && parts[i - 1].Transition == ClipTransitionKind.Sound
                    ? Math.Min(SoundTransition, Math.Min(ClipEdit.TransitionLength(parts[i - 1]), part.Length / 2)) : 0;
                var fadeOut = part.Transition == ClipTransitionKind.Sound && i + 1 < parts.Count
                    ? Math.Min(SoundTransition, Math.Min(ClipEdit.TransitionLength(part), part.Length / 2)) : 0;
                graph.Append($",apad,atrim=duration={length},asetpts=PTS-STARTPTS");
                if (fadeIn > 0 || fadeOut > 0)
                    graph.Append(',').Append(Fades("afade", fadeIn, fadeOut, part.Length));
                graph.Append($"[p{i}a];");
            }
        }

        // Joined: parts after a cut by concat, groups after a crossfade or a fade through black by xfade.
        var groups = new List<(int First, int Last)>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0 && edit.Overlap(i - 1) == 0)
                groups[^1] = (groups[^1].First, i);
            else
                groups.Add((i, i));
        }
        var starts = edit.PartStarts();
        string Join(string kind)
        {
            var labels = new List<(string Label, double Length)>();
            for (var g = 0; g < groups.Count; g++)
            {
                var (first, last) = groups[g];
                var groupLength = parts.Skip(first).Take(last - first + 1).Sum(p => p.Length);
                if (first == last)
                {
                    labels.Add(($"p{first}{kind}", groupLength));
                    continue;
                }
                for (var i = first; i <= last; i++)
                    graph.Append($"[p{i}{kind}]");
                // xfade wants both sides in the same time base.
                graph.Append(kind == "v" ? $"concat=n={last - first + 1}:v=1:a=0,settb=AVTB" : $"concat=n={last - first + 1}:v=0:a=1").Append($"[g{g}{kind}];");
                labels.Add(($"g{g}{kind}", groupLength));
            }
            var current = labels[0].Label;
            for (var g = 1; g < labels.Count; g++)
            {
                var before = groups[g - 1].Last;
                var overlap = edit.Overlap(before);
                var into = $"x{g}{kind}";
                if (kind == "v")
                {
                    var transition = parts[before].Transition == ClipTransitionKind.Black ? "fadeblack" : "fade";
                    graph.Append($"[{current}][{labels[g].Label}]xfade=transition={transition}:duration={Seconds(overlap)}:offset={Seconds(starts[before + 1])}[{into}];");
                }
                else
                    graph.Append($"[{current}][{labels[g].Label}]acrossfade=d={Seconds(overlap)}:c1=tri:c2=tri[{into}];");
                current = into;
            }
            return current;
        }

        var total = edit.Length;
        var fadeInAll = Math.Min(ClipEdit.Fade(edit.FadeIn), total / 2);
        var fadeOutAll = Math.Min(ClipEdit.Fade(edit.FadeOut), total / 2);
        var outputs = new List<string>();
        if (video)
        {
            var joined = Join("v");
            if (layers)
                joined = Layers(graph, inputs, edit, joined, width, height, fps, total);
            // Texts the editor didn't draw (an edit made elsewhere) are drawn by FFmpeg.
            var drawn = edit.Texts.Where(t => !layers || t.Rendered is null).ToList();
            var texts = textFolder is null ? "" : Texts(drawn, width, height, textFolder);
            outputs.Add($"[{joined}]{texts}{Fades("fade", fadeInAll, fadeOutAll, total)}[v]");
        }
        if (hasSound)
        {
            var voice = Join("a");
            var clean = new List<string>();
            if (edit.ReduceNoise)
                clean.Add("afftdn=nr=12:nf=-40:tn=1");
            if (edit.LiftSpeech)
                clean.Add("acompressor=threshold=0.089:ratio=3:attack=5:release=150:makeup=2");
            outputs.Add($"[{voice}]{(clean.Count > 0 ? string.Join(',', clean) : "anull")}[voice]");
            var mixed = "voice";
            if (songs.Count > 0)
            {
                // Each sound: as long as it plays, at its volume, faded at its ends, delayed to its place; then all together.
                for (var k = 0; k < songs.Count; k++)
                {
                    var song = songs[k];
                    var length = song.PlayLength(total);
                    var fade = song.Fade ? Math.Min(ClipMusic.FadeSeconds, length / 2) : 0;
                    var delay = (long)Math.Round(song.Offset * 1000);
                    var musicInput = inputs.Count(a => a == "-i");
                    inputs.AddRange(["-ss", ClipRange.Ffmpeg(song.From), "-i", song.Path]);
                    var chain = $"[{musicInput}:a:0]atrim=duration={Seconds(length)},asetpts=PTS-STARTPTS";
                    if (song.PlayVolume != 1)
                        chain += $",volume={Seconds(song.PlayVolume)}";
                    if (fade > 0)
                        chain += "," + Fades("afade", fade, fade, length);
                    if (delay > 0)
                        chain += $",adelay={delay}:all=1";
                    outputs.Add($"{chain},{ClipAudioFormat}[m{k}]");
                }
                outputs.Add(songs.Count == 1
                    ? "[m0]anull[m]"
                    : string.Concat(Enumerable.Range(0, songs.Count).Select(k => $"[m{k}]")) + $"amix=inputs={songs.Count}:duration=longest:dropout_transition=0:normalize=0[m]");
                if (edit.DuckMusic)
                {
                    // The music gives way while the video's own sound plays.
                    outputs.Add("[voice]asplit=2[vo][sc]");
                    outputs.Add("[m][sc]sidechaincompress=threshold=0.03:ratio=6:attack=30:release=500[md]");
                    outputs.Add("[vo][md]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[mix]");
                }
                else
                    outputs.Add("[voice][m]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[mix]");
                mixed = "mix";
            }
            var level = edit.LevelLoudness ? "loudnorm=I=-14:TP=-1.5:LRA=11," + ClipAudioFormat + "," : "";
            outputs.Add($"[{mixed}]{level}{Fades("afade", fadeInAll, fadeOutAll, total)}[a]");
        }
        graph.Append(string.Join(';', outputs));
        return new ClipGraph(inputs, graph.ToString(), hasSound, total, video);
    }

    /// <summary>What the frame shows of a part's picture: the crop and scale that read it, and where it goes.</summary>
    /// <param name="Scale">Crop (to what is shown) and scale (to its size in the frame).</param>
    /// <param name="Width">Its size in the frame.</param>
    /// <param name="X">Where it goes in the frame (its top-left corner; unturned).</param>
    /// <param name="Rotation">Degrees it is turned clockwise about its middle (0: not turned, then nothing is cut off it).</param>
    internal readonly record struct EditorPicture(string Scale, int Width, int Height, int X, int Y, double Rotation = 0);

    /// <summary>The crop and scale of <paramref name="part"/>'s picture for a frame of <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static EditorPicture Picture(EditorSource source, ClipPart part, ClipFit fit, int width, int height)
    {
        var (sourceWidth, sourceHeight) = source.Size;
        var place = ClipCanvas.Place(sourceWidth, sourceHeight, width, height, fit, part.PlayZoom, part.FocusX, part.FocusY);
        var scale = place.Width / sourceWidth;
        if (part.PlayRotation != 0)
        {
            // Turned: scaled whole (corners may come into the frame), turned and laid on at its middle later.
            var turnedWidth = Even(place.Width);
            var turnedHeight = Even(place.Height);
            return new EditorPicture($"scale={turnedWidth}:{turnedHeight}:flags=bicubic,setsar=1", turnedWidth, turnedHeight,
                (int)Math.Round(place.X + place.Width / 2 - turnedWidth / 2.0), (int)Math.Round(place.Y + place.Height / 2 - turnedHeight / 2.0),
                part.PlayRotation);
        }
        // What of the placed picture is inside the frame (it may stick out on any side), and that part of the video.
        var visibleLeft = Math.Clamp(place.X, 0, width);
        var visibleTop = Math.Clamp(place.Y, 0, height);
        var visibleRight = Math.Clamp(place.X + place.Width, 0, width);
        var visibleBottom = Math.Clamp(place.Y + place.Height, 0, height);
        var shownX = visibleLeft - place.X;
        var shownY = visibleTop - place.Y;
        var shownWidth = Math.Max(2, visibleRight - visibleLeft);
        var shownHeight = Math.Max(2, visibleBottom - visibleTop);
        var outWidth = Math.Min(Even(shownWidth), width);
        var outHeight = Math.Min(Even(shownHeight), height);
        var x = Math.Clamp((int)Math.Round(visibleLeft), 0, width - outWidth);
        var y = Math.Clamp((int)Math.Round(visibleTop), 0, height - outHeight);
        var filters = new List<string>();
        if (shownWidth < place.Width - 0.5 || shownHeight < place.Height - 0.5)
        {
            // The video's own pixels (before rotation FFmpeg has applied, so in its shown orientation).
            var cropWidth = Math.Clamp(shownWidth / scale, 2, sourceWidth);
            var cropHeight = Math.Clamp(shownHeight / scale, 2, sourceHeight);
            var cropX = Math.Clamp(shownX / scale, 0, sourceWidth - cropWidth);
            var cropY = Math.Clamp(shownY / scale, 0, sourceHeight - cropHeight);
            filters.Add($"crop={Seconds(cropWidth)}:{Seconds(cropHeight)}:{Seconds(cropX)}:{Seconds(cropY)}");
        }
        filters.Add($"scale={outWidth}:{outHeight}:flags=bicubic,setsar=1");
        return new EditorPicture(string.Join(',', filters), outWidth, outHeight, x, y);
    }

    /// <summary>The steps of a part's speed (one, or a smooth change in steps), from <paramref name="from"/> into a new label.</summary>
    private static string Speed(StringBuilder graph, string from, string to, ClipPart part, bool video)
    {
        if (part.IsStill)
            return from;
        var steps = part.Steps();
        if (steps.Count == 1)
        {
            var speed = steps[0].Speed;
            if (Math.Abs(speed - 1) < 1e-9)
                return from;
            if (video)
                graph.Append($"[{from}]setpts=PTS/{Seconds(speed)}[{to}];");
            else
                graph.Append($"[{from}]{string.Join(',', Tempo(speed).Select(t => $"atempo={Seconds(t)}"))}[{to}];");
            return to;
        }
        graph.Append($"[{from}]{(video ? "split" : "asplit")}={steps.Count}");
        for (var k = 0; k < steps.Count; k++)
            graph.Append($"[{to}s{k}]");
        graph.Append(';');
        for (var k = 0; k < steps.Count; k++)
        {
            var (start, end, speed) = steps[k];
            if (video)
                graph.Append($"[{to}s{k}]trim=start={Seconds(start)}:end={Seconds(end)},setpts=(PTS-STARTPTS)/{Seconds(speed)}[{to}q{k}];");
            else
                graph.Append($"[{to}s{k}]atrim=start={Seconds(start)}:end={Seconds(end)},asetpts=PTS-STARTPTS,"
                    + string.Join(',', Tempo(speed).Select(t => $"atempo={Seconds(t)}").DefaultIfEmpty("anull")) + $"[{to}q{k}];");
        }
        for (var k = 0; k < steps.Count; k++)
            graph.Append($"[{to}q{k}]");
        graph.Append($"concat=n={steps.Count}:v={(video ? 1 : 0)}:a={(video ? 0 : 1)}[{to}];");
        return to;
    }

    /// <summary>The scaled picture put into the frame: on black, or on a blurred copy of itself.</summary>
    private static string Frame(StringBuilder graph, string from, string to, EditorPicture picture, ClipFit fit, int width, int height, string fps)
    {
        if (picture.Rotation != 0)
            return Turned(graph, from, to, picture, fit, width, height, fps);
        if (picture.Width == width && picture.Height == height)
            return from;
        if (fit != ClipFit.Blur)
        {
            graph.Append($"[{from}]pad={width}:{height}:{picture.X}:{picture.Y}:black[{to}];");
            return to;
        }
        // The blurred copy: small, cropped to fill the frame, blurred, scaled up (cheap and soft).
        var smallWidth = Even(width / 8.0);
        var smallHeight = Even(height / 8.0);
        var cover = ClipCanvas.Place(picture.Width, picture.Height, smallWidth, smallHeight, ClipFit.Crop);
        graph.Append($"[{from}]split=2[{to}f][{to}b];");
        graph.Append($"[{to}b]scale={Even(cover.Width)}:{Even(cover.Height)},crop={smallWidth}:{smallHeight},boxblur=6:2,scale={width}:{height},setsar=1[{to}g];");
        graph.Append($"[{to}g][{to}f]overlay={picture.X}:{picture.Y}:shortest=1[{to}];");
        return to;
    }

    /// <summary>A turned picture laid on black or on a blurred copy of itself, its middle where it was placed.</summary>
    private static string Turned(StringBuilder graph, string from, string to, EditorPicture picture, ClipFit fit, int width, int height, string fps)
    {
        var radians = Seconds(Math.Abs(picture.Rotation) * Math.PI / 180);
        var angle = picture.Rotation < 0 ? $"-{radians}" : radians;
        var middleX = Seconds(picture.X + picture.Width / 2.0);
        var middleY = Seconds(picture.Y + picture.Height / 2.0);
        if (fit == ClipFit.Blur)
        {
            var smallWidth = Even(width / 8.0);
            var smallHeight = Even(height / 8.0);
            var cover = ClipCanvas.Place(picture.Width, picture.Height, smallWidth, smallHeight, ClipFit.Crop);
            graph.Append($"[{from}]split=2[{to}f][{to}b];");
            graph.Append($"[{to}b]scale={Even(cover.Width)}:{Even(cover.Height)},crop={smallWidth}:{smallHeight},boxblur=6:2,scale={width}:{height},setsar=1[{to}g];");
        }
        else
        {
            graph.Append($"[{from}]null[{to}f];");
            graph.Append($"color=c=black:s={width}x{height}:r={fps},setsar=1[{to}g];");
        }
        graph.Append($"[{to}f]format=rgba,rotate={angle}:ow=rotw({radians}):oh=roth({radians}):c=none[{to}r];");
        graph.Append($"[{to}g][{to}r]overlay=x={middleX}-overlay_w/2:y={middleY}-overlay_h/2:shortest=1[{to}];");
        return to;
    }

    /// <summary>
    /// The texts and pictures the editor drew, each a transparent picture of the frame's size, laid over
    /// <paramref name="from"/> while they show (texts fading in and out). Returns the label of the result.
    /// </summary>
    private static string Layers(StringBuilder graph, List<string> inputs, ClipEdit edit, string from, int width, int height, string fps, double total)
    {
        var layers = edit.Images.Where(i => i.Rendered is not null).Select(i => (Path: i.Rendered!, i.Start, i.Length, Fade: 0.0))
            .Concat(edit.Texts.Where(t => t.Rendered is not null).Select(t => (Path: t.Rendered!, t.Start, t.Length, Fade: Math.Min(ClipText.FadeSeconds, t.Length / 3))))
            .ToList();
        var current = from;
        for (var n = 0; n < layers.Count; n++)
        {
            var (path, start, length, fade) = layers[n];
            var end = start + length;
            var index = inputs.Count(a => a == "-i");
            inputs.AddRange(["-loop", "1", "-framerate", fps, "-t", Seconds(total + 1), "-i", path]);
            var fades = fade > 0
                ? $",fade=t=in:st={Seconds(start)}:d={Seconds(fade)}:alpha=1,fade=t=out:st={Seconds(end - fade)}:d={Seconds(fade)}:alpha=1"
                : "";
            graph.Append($"[{index}:v]scale={width}:{height},format=rgba,setsar=1{fades}[l{n}];");
            graph.Append($"[{current}][l{n}]overlay=0:0:enable='between(t,{Seconds(start)},{Seconds(end)})':eof_action=pass[lv{n}];");
            current = $"lv{n}";
        }
        return current;
    }

    /// <summary>The texts drawn over the picture (drawtext, each in its own file), as filters ending in a comma.</summary>
    private static string Texts(IReadOnlyList<ClipText> texts, int width, int height, string folder)
    {
        if (texts.Count == 0)
            return "";
        Directory.CreateDirectory(folder);
        var bold = EditorFont();
        var filters = new StringBuilder();
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            var file = Path.Combine(folder, $"clip-text-{i}.txt");
            File.WriteAllText(file, text.Text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            var size = Math.Max(8, (int)Math.Round(text.PlaySize * height));
            var start = text.Start;
            var end = text.Start + text.Length;
            var fade = Math.Min(ClipText.FadeSeconds, text.Length / 3);
            var color = text.PlayColor;
            filters.Append("drawtext=");
            if (bold is not null)
                filters.Append($"fontfile={FilterPath(bold)}:");
            filters.Append($"textfile={FilterPath(file)}:expansion=none:fontsize={size}:text_align=C:line_spacing={-(int)Math.Round(size * 0.28)}");
            filters.Append($":x={Seconds(text.X * width)}-text_w/2:y={Seconds(text.Y * height)}-text_h/2");
            switch (text.Style)
            {
                case ClipTextStyle.Title:
                    filters.Append($":fontcolor={FfColor(color)}:borderw={Math.Max(1, (int)Math.Round(size * 0.07))}:bordercolor=black@0.55");
                    filters.Append($":shadowx={Math.Max(1, size / 30)}:shadowy={Math.Max(1, size / 30)}:shadowcolor=black@0.35");
                    break;
                case ClipTextStyle.Caption:
                    filters.Append($":fontcolor={FfColor(color)}:box=1:boxcolor=black@0.55:boxborderw={(int)Math.Round(size * 0.35)}");
                    break;
                default:
                    filters.Append($":fontcolor={(Light(color) ? "0x111111" : "white")}:box=1:boxcolor={FfColor(color)}:boxborderw={(int)Math.Round(size * 0.4)}");
                    break;
            }
            filters.Append($":enable='between(t,{Seconds(start)},{Seconds(end)})'");
            if (fade > 0)
                filters.Append($":alpha='if(lt(t,{Seconds(start + fade)}),(t-{Seconds(start)})/{Seconds(fade)},if(gt(t,{Seconds(end - fade)}),({Seconds(end)}-t)/{Seconds(fade)},1))'");
            filters.Append(',');
        }
        return filters.ToString();
    }

    /// <summary>A bold font for the texts on this system, or null (FFmpeg's own choice).</summary>
    internal static string? EditorFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        [
            Path.Combine(fonts, "segoeuib.ttf"), Path.Combine(fonts, "arialbd.ttf"),
            "/System/Library/Fonts/Supplemental/Arial Bold.ttf", "/Library/Fonts/Arial Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        ];
        return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(path));
    }

    /// <summary>
    /// A file path as a filter option in <c>-filter_complex</c>: forward slashes, and the characters the graph and the
    /// option parser read escaped for both.
    /// </summary>
    internal static string FilterPath(string path)
    {
        var text = new StringBuilder();
        foreach (var c in path.Replace('\\', '/'))
        {
            switch (c)
            {
                case ':':
                    text.Append(@"\\:");
                    break;
                case '\'':
                    text.Append(@"\\\'");
                    break;
                case ',' or ';' or '[' or ']':
                    text.Append('\\').Append(c);
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
        return text.ToString();
    }

    private static string FfColor(string color) => "0x" + color.TrimStart('#');

    /// <summary>True for a light colour (dark letters read better on it).</summary>
    private static bool Light(string color) =>
        ClipCanvas.ParseColor(color) is { } c && 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160;
}
