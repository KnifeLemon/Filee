// Fits imported pages without changing shared content streams or flattening their structure.
using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

internal sealed class PdfMergePaperFitter(PdfDocument document, PdfMergePaperFitter.Limits settings, int appearanceLength)
{
    private readonly PdfDocument output = document;
    private readonly Limits limits = settings;
    internal sealed record Limits
    {
        public int ParentDepth { get; init; } = 64;
        public int ContentStreams { get; init; } = 100_000;
        public int AnnotationSlots { get; init; } = 100_000;
        public int AnnotationNumbers { get; init; } = 65_536;
        public int SourceNumbers { get; init; } = 1_000_000;
        public int InkStrokes { get; init; } = 4096;
        public int AppearanceStates { get; init; } = 256;
        public int Objects { get; init; } = 1_000_000;
        public int StreamBytes { get; init; } = 4_194_304;
        public int Items { get; init; } = 1_000_000;
        public int DaLength { get; init; } = 4096;
        public int DaBytes { get; init; } = 1_048_576;
        public Action<int>? BeforePage { get; init; }
    }

    internal readonly record struct Box(double X1, double Y1, double X2, double Y2)
    {
        internal double Width => X2 - X1;
        internal double Height => Y2 - Y1;
        internal bool Valid => Finite(X1, Y1, X2, Y2, Width, Height) && Width > 0 && Height > 0;
        internal Box Intersect(Box other) => new(Math.Max(X1, other.X1), Math.Max(Y1, other.Y1),
            Math.Min(X2, other.X2), Math.Min(Y2, other.Y2));
        internal double[] Values => [X1, Y1, X2, Y2];
    }

    internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal double[] Values => [A, B, C, D, E, F];
        internal (double X, double Y) Point(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        internal Box Bounds(Box box)
        {
            var points = new[] { Point(box.X1, box.Y1), Point(box.X2, box.Y1), Point(box.X1, box.Y2), Point(box.X2, box.Y2) };
            return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }
        internal Matrix Then(Matrix next) => new(
            next.A * A + next.C * B, next.B * A + next.D * B,
            next.A * C + next.C * D, next.B * C + next.D * D,
            next.A * E + next.C * F + next.E, next.B * E + next.D * F + next.F);
    }

    internal sealed record Geometry(Box Visible, int Rotate, double Unit, double Width, double Height);
    internal sealed record Transform(Geometry Source, double Width, double Height, double Scale, Matrix Matrix);

    internal static PdfItem? Value(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
    private static PdfDictionary? Dictionary(PdfItem? item) => Value(item) as PdfDictionary;
    private static PdfArray? Array(PdfItem? item) => Value(item) as PdfArray;
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static double Number(PdfItem? item) => Value(item) switch
    {
        PdfInteger n => n.Value,
        PdfReal n => n.Value,
        PdfIntegerObject n => n.Value,
        PdfRealObject n => n.Value,
        _ => double.NaN,
    };
    private static Box ReadBox(PdfItem? item)
    {
        if (Value(item) is PdfRectangle rect)
            return new(rect.X1, rect.Y1, rect.X2, rect.Y2);
        if (Array(item) is not { Elements.Count: 4 } array)
            return new(double.NaN, 0, 0, 0);
        return new(Number(array.Elements[0]), Number(array.Elements[1]), Number(array.Elements[2]), Number(array.Elements[3]));
    }
    private static ConversionRefusedException GeometryError() => new("error.merge_paper_geometry");
    private static ConversionRefusedException LimitError() => new("error.merge_paper_limit");

    internal Geometry Snapshot(PdfPage page)
    {
        PdfItem? media = null, crop = null, rotation = null;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        PdfDictionary? node = page;
        while (node is not null)
        {
            if (!visited.Add(node))
                throw GeometryError();
            if (visited.Count > limits.ParentDepth)
                throw LimitError();
            media ??= node.Elements["/MediaBox"];
            crop ??= node.Elements["/CropBox"];
            rotation ??= node.Elements["/Rotate"];
            node = Dictionary(node.Elements["/Parent"]);
        }
        var visible = ReadBox(media);
        if (!visible.Valid)
            throw GeometryError();
        if (crop is not null)
        {
            var box = ReadBox(crop);
            if (!Finite(box.Values))
                throw GeometryError();
            var intersection = visible.Intersect(box);
            if (box.Valid && intersection.Valid)
                visible = intersection;
        }
        var rotate = rotation is null ? 0 : Number(rotation);
        var unit = page.Elements.ContainsKey("/UserUnit") ? Number(page.Elements["/UserUnit"]) : 1;
        if (!double.IsFinite(rotate) || rotate % 90 != 0)
            rotate = 0;
        if (!double.IsFinite(unit) || unit <= 0)
            unit = 1;
        var normalized = (int)((rotate % 360 + 360) % 360);
        var width = (normalized is 90 or 270 ? visible.Height : visible.Width) * unit;
        var height = (normalized is 90 or 270 ? visible.Width : visible.Height) * unit;
        if (!Finite(width, height) || width <= 0 || height <= 0)
            throw GeometryError();
        return new(visible, normalized, unit, width, height);
    }

    internal static (double Width, double Height) Target(Geometry first, Core.Conversion.PdfMergePaper paper)
    {
        var (w, h) = paper.Paper switch
        {
            PdfSheetPaper.Letter => (612d, 792d),
            PdfSheetPaper.A3 => (841.89, 1190.55),
            PdfSheetPaper.SameAsFirstPage => (first.Width, first.Height),
            _ => (595.28, 841.89),
        };
        if (paper.Paper == PdfSheetPaper.SameAsFirstPage && paper.Orientation == PdfSheetOrientation.Auto)
            return (w, h);
        (w, h) = (Math.Min(w, h), Math.Max(w, h));
        var landscape = paper.Orientation == PdfSheetOrientation.Landscape ||
            paper.Orientation == PdfSheetOrientation.Auto &&
            Math.Min(h / first.Width, w / first.Height) > Math.Min(w / first.Width, h / first.Height);
        return landscape ? (h, w) : (w, h);
    }

    internal static Transform Fit(Geometry source, (double Width, double Height) target)
    {
        var s = Math.Min(target.Width / source.Width, target.Height / source.Height);
        var k = s * source.Unit;
        var dx = (target.Width - s * source.Width) / 2;
        var dy = (target.Height - s * source.Height) / 2;
        var v = source.Visible;
        var matrix = source.Rotate switch
        {
            90 => new Matrix(0, -k, k, 0, dx - k * v.Y1, dy + k * v.X2),
            180 => new Matrix(-k, 0, 0, -k, dx + k * v.X2, dy + k * v.Y2),
            270 => new Matrix(0, k, -k, 0, dx + k * v.Y2, dy - k * v.X1),
            _ => new Matrix(k, 0, 0, k, dx - k * v.X1, dy - k * v.Y1),
        };
        if (!Finite(s, k, dx, dy) || s <= 0 || k <= 0 || !Finite(matrix.Values) || !matrix.Bounds(v).Valid)
            throw GeometryError();
        return new(source, target.Width, target.Height, k, matrix);
    }

    private PdfArray Numbers(IEnumerable<double> numbers)
    {
        var array = new PdfArray(output);
        foreach (var n in numbers)
            array.Elements.Add(new PdfReal(n));
        return array;
    }
    private static readonly string DecimalFormat = "0." + new string('#', 340);
    internal static string NumberText(double number) => number.ToString(DecimalFormat, CultureInfo.InvariantCulture);
    private static string N(double number) => NumberText(number);
    private static string Cm(Matrix matrix) => string.Join(" ", matrix.Values.Select(N)) + " cm\n";
    private PdfDictionary Stream(string text)
    {
        var stream = new PdfDictionary(output);
        stream.CreateStream(Encoding.ASCII.GetBytes(text));
        return stream;
    }

    internal void Normalize(PdfPage original, PdfPage page, Transform transform)
    {
        var old = page.Elements["/Contents"];
        var array = Array(old);
        var count = array?.Elements.Count ?? (old is null ? 0 : 1);
        if (count > limits.ContentStreams)
            throw LimitError();
        var contents = new PdfArray(output);
        var v = transform.Source.Visible;
        var prefix = Stream("q\n" + Cm(transform.Matrix) + $"{N(v.X1)} {N(v.Y1)} {N(v.Width)} {N(v.Height)} re W n\n");
        var suffix = Stream("\nQ\n");
        if (array is not null)
        {
            foreach (var item in array.Elements)
                if (Dictionary(item)?.Stream is null)
                    throw GeometryError();
        }
        else if (old is not null && Dictionary(old)?.Stream is null)
            throw GeometryError();
        output.Internals.AddObject(prefix);
        output.Internals.AddObject(suffix);
        contents.Elements.Add(prefix.Reference!);
        if (array is not null)
            foreach (var item in array.Elements)
                contents.Elements.Add(item);
        else if (old is not null)
            contents.Elements.Add(old);
        contents.Elements.Add(suffix.Reference!);
        page.Elements["/Contents"] = contents;
        var target = new Box(0, 0, transform.Width, transform.Height);
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(target.X2, target.Y2));
        page.CropBox = page.MediaBox;
        page.Rotate = 0;
        page.Elements.SetInteger("/UserUnit", 1);
        foreach (var key in new[] { "/TrimBox", "/BleedBox", "/ArtBox" })
        {
            if (!original.Elements.ContainsKey(key))
                continue;
            var box = ReadBox(original.Elements[key]);
            var changed = box.Valid ? transform.Matrix.Bounds(box).Intersect(target) : default;
            if (changed.Valid)
                page.Elements[key] = Numbers(changed.Values);
            else
                page.Elements.Remove(key);
        }
    }

    internal static bool Destination(PdfArray destination, Transform transform)
    {
        var kind = ((PdfName)destination.Elements[1]).Value;
        double? Read(int index) => Value(destination.Elements[index]) is PdfNull ? null : Number(destination.Elements[index]);
        var m = transform.Matrix;
        var swapped = transform.Source.Rotate is 90 or 270;
        PdfItem Coordinate(double? value, bool x, bool inputX)
        {
            if (value is null)
                return PdfNull.Value;
            var n = x ? (inputX ? m.A : m.C) * value.Value + m.E : (inputX ? m.B : m.D) * value.Value + m.F;
            return new PdfReal(n);
        }
        if (kind == "/XYZ")
        {
            var x = Read(2);
            var y = Read(3);
            destination.Elements[2] = Coordinate(swapped ? y : x, true, !swapped);
            destination.Elements[3] = Coordinate(swapped ? x : y, false, swapped);
        }
        else if (kind == "/FitR")
        {
            var box = m.Bounds(new(Read(2) ?? double.NaN, Read(3) ?? double.NaN, Read(4) ?? double.NaN, Read(5) ?? double.NaN));
            if (!Finite(box.Values))
                return false;
            for (var i = 0; i < 4; i++)
                destination.Elements[i + 2] = new PdfReal(box.Values[i]);
        }
        else if (kind is "/FitH" or "/FitBH" or "/FitV" or "/FitBV")
        {
            var inputX = kind is "/FitV" or "/FitBV";
            destination.Elements[2] = Coordinate(Read(2), swapped ? !inputX : inputX, inputX);
            if (swapped)
                destination.Elements[1] = new PdfName(kind switch
                {
                    "/FitH" => "/FitV",
                    "/FitBH" => "/FitBV",
                    "/FitV" => "/FitH",
                    _ => "/FitBH",
                });
        }
        return destination.Elements.Skip(2).All(item => Value(item) is PdfNull || double.IsFinite(Number(item)));
    }

    private readonly HashSet<PdfDictionary> _sharedAnnotations = new(ReferenceEqualityComparer.Instance);
    private int _numbers, _objects, _items, _streamBytes, _daBytes;

    internal void CollectAnnotations(PdfDocument source)
    {
        var owners = new Dictionary<PdfDictionary, PdfPage>(ReferenceEqualityComparer.Instance);
        var slots = 0;
        foreach (var page in source.Pages)
        {
            if (Array(page.Elements["/Annots"]) is not { } annotations)
                continue;
            if (annotations.Elements.Count > limits.AnnotationSlots - slots)
                throw LimitError();
            slots += annotations.Elements.Count;
            foreach (var item in annotations.Elements)
            {
                if (Dictionary(item) is not { } annotation)
                    continue;
                if (owners.TryGetValue(annotation, out var owner) && !ReferenceEquals(owner, page))
                    _sharedAnnotations.Add(annotation);
                else
                    owners[annotation] = page;
            }
        }
    }

    // Stages only shallow mutable copies. No imported object is written before the whole annotation is valid
    // and its work budget is reserved. Rejected entries keep what Carry produced, but work is never refunded.
    private sealed class OptionalGeometryException : Exception;

    private sealed class Changes(PdfMergePaperFitter owner)
    {
        internal int Numbers, States;
        internal List<PdfDictionary> Streams { get; } = [];
        internal List<PdfDictionary> Dictionaries { get; } = [];

        internal void Check()
        {
            if (States > owner.limits.AppearanceStates)
                throw LimitError();
        }
        internal void Coordinates(int count)
        {
            if (count > owner.limits.AnnotationNumbers - Numbers || count > owner.limits.SourceNumbers - owner._numbers)
                throw LimitError();
            Numbers += count;
            owner._numbers += count;
        }
        internal void Reserve(int count)
        {
            if (count > owner.limits.Items - owner._items || owner._objects >= owner.limits.Objects)
                throw LimitError();
            owner._items += count;
            owner._objects++;
        }
        internal PdfArray Array(int count)
        {
            Reserve(count);
            return new(owner.output);
        }
        internal PdfArray NumbersArray(IEnumerable<double> values)
        {
            var numbers = values.ToArray();
            if (!Finite(numbers))
                throw new OptionalGeometryException();
            var result = Array(numbers.Length);
            foreach (var n in numbers)
                result.Elements.Add(new PdfReal(n));
            return result;
        }
        internal PdfDictionary Dictionary(PdfDictionary? original = null, int extra = 0)
        {
            var count = original?.Elements.Count ?? 0;
            Reserve(count + extra);
            var result = new PdfDictionary(owner.output);
            if (original is not null)
                foreach (var entry in original.Elements)
                    result.Elements[entry.Key] = entry.Value;
            Dictionaries.Add(result);
            return result;
        }
        internal void Commit()
        {
            foreach (var stream in Streams)
                owner.output.Internals.AddObject(stream);
            foreach (var dictionary in Dictionaries)
                foreach (var key in dictionary.Elements.Keys.ToArray())
                    if (dictionary.Elements[key] is PdfDictionary child && Streams.Contains(child))
                        dictionary.Elements[key] = child.Reference!;
        }
    }

    internal void Annotations(PdfPage original, PdfPage page, Transform transform)
    {
        if (Array(original.Elements["/Annots"]) is not { } source ||
            Array(page.Elements["/Annots"]) is not { } imported)
            return;
        // A page's Annots array itself can be shared. Do not replace any of its slots in place.
        var result = new Changes(this).Array(imported.Elements.Count);
        foreach (var item in imported.Elements)
            result.Elements.Add(item);
        var done = new Dictionary<PdfDictionary, PdfItem>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < source.Elements.Count && i < result.Elements.Count; i++)
        {
            if (Dictionary(source.Elements[i]) is not { } sourceAnnotation ||
                Dictionary(imported.Elements[i]) is not { } annotation)
                continue;
            if (done.TryGetValue(annotation, out var previous))
            {
                result.Elements[i] = previous;
                continue;
            }
            var widget = (Value(annotation.Elements["/Subtype"]) as PdfName)?.Value == "/Widget";
            var shared = _sharedAnnotations.Contains(sourceAnnotation);
            if (widget && shared)
                continue;
            try
            {
                var changes = new Changes(this);
                var copy = TransformAnnotation(annotation, page, transform, changes, widget);
                changes.Commit();
                if (!shared)
                {
                    foreach (var key in copy.Elements.Keys)
                        annotation.Elements[key] = copy.Elements[key];
                    done[annotation] = result.Elements[i];
                }
                else
                {
                    output.Internals.AddObject(copy);
                    result.Elements[i] = copy.Reference!;
                    done[annotation] = copy.Reference!;
                }
            }
            catch (OptionalGeometryException)
            {
                // Optional structure is carried unchanged; a damaged neighbor does not discard this page.
            }
        }
        if (done.Count > 0)
            page.Elements["/Annots"] = result;
    }

    private PdfDictionary TransformAnnotation(PdfDictionary annotation, PdfPage page, Transform transform, Changes changes, bool widget)
    {
        changes.Coordinates(4);
        var rect = ReadBox(annotation.Elements["/Rect"]);
        var changedRect = transform.Matrix.Bounds(rect);
        if (!rect.Valid || !changedRect.Valid)
            throw new OptionalGeometryException();
        var copy = changes.Dictionary(annotation, 2);
        copy.Elements["/Rect"] = changes.NumbersArray(changedRect.Values);
        copy.Elements["/P"] = page.Reference!;
        foreach (var key in new[] { "/QuadPoints", "/Vertices", "/L" })
        {
            if (!annotation.Elements.ContainsKey(key))
                continue;
            if (Array(annotation.Elements[key]) is not { } coordinates ||
                key == "/QuadPoints" && coordinates.Elements.Count % 8 != 0 ||
                key == "/L" && coordinates.Elements.Count != 4)
                throw new OptionalGeometryException();
            copy.Elements[key] = Points(coordinates, transform.Matrix, changes);
        }
        if (annotation.Elements.ContainsKey("/InkList"))
        {
            if (Array(annotation.Elements["/InkList"]) is not { } ink)
                throw new OptionalGeometryException();
            if (ink.Elements.Count > limits.InkStrokes)
                throw LimitError();
            var strokes = changes.Array(ink.Elements.Count);
            foreach (var item in ink.Elements)
            {
                if (Array(item) is not { } stroke)
                    throw new OptionalGeometryException();
                strokes.Elements.Add(Points(stroke, transform.Matrix, changes));
            }
            copy.Elements["/InkList"] = strokes;
        }
        if (annotation.Elements.ContainsKey("/AP"))
        {
            if (Dictionary(annotation.Elements["/AP"]) is not { } ap || ap.Stream is not null)
                throw new OptionalGeometryException();
            var appearances = changes.Dictionary(ap);
            foreach (var key in new[] { "/N", "/R", "/D" })
            {
                if (!ap.Elements.ContainsKey(key))
                    continue;
                if (Dictionary(ap.Elements[key]) is not { } appearance)
                    throw new OptionalGeometryException();
                if (appearance.Stream is not null)
                    appearances.Elements[key] = Appearance(appearance, rect, changedRect, transform.Matrix, changes);
                else
                {
                    if (appearance.Elements.Count > limits.AppearanceStates - changes.States)
                        throw LimitError();
                    var states = changes.Dictionary(appearance);
                    foreach (var state in appearance.Elements)
                    {
                        if (Dictionary(state.Value) is not { Stream: not null } stream)
                            throw new OptionalGeometryException();
                        states.Elements[state.Key] = Appearance(stream, rect, changedRect, transform.Matrix, changes);
                    }
                    appearances.Elements[key] = states;
                }
            }
            copy.Elements["/AP"] = appearances;
        }
        if (widget)
        {
            if (annotation.Elements.ContainsKey("/MK"))
            {
                if (Dictionary(annotation.Elements["/MK"]) is not { } mk)
                    throw new OptionalGeometryException();
                var rotation = mk.Elements.ContainsKey("/R") ? Number(mk.Elements["/R"]) : 0;
                if (!double.IsFinite(rotation) || rotation % 1 != 0)
                    throw new OptionalGeometryException();
                var changed = changes.Dictionary(mk, 1);
                changed.Elements.SetInteger("/R", (int)(((rotation % 360 - transform.Source.Rotate) % 360 + 360) % 360));
                copy.Elements["/MK"] = changed;
            }
            else if (transform.Source.Rotate != 0)
            {
                var mk = changes.Dictionary(extra: 1);
                mk.Elements.SetInteger("/R", 360 - transform.Source.Rotate);
                copy.Elements["/MK"] = mk;
            }
        }
        if (widget || (Value(annotation.Elements["/Subtype"]) as PdfName)?.Value == "/FreeText")
        {
            PdfString? da = null;
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfDictionary? node = annotation;
            while (node is not null)
            {
                if (!visited.Add(node))
                    throw new OptionalGeometryException();
                if (visited.Count > limits.ParentDepth)
                    throw LimitError();
                if (Value(node.Elements["/DA"]) is PdfString text)
                {
                    da = text;
                    break;
                }
                node = widget ? Dictionary(node.Elements["/Parent"]) : null;
            }
            if (da is not null)
            {
                if (da.Value.Length > limits.DaLength)
                    throw LimitError();
                var encoded = da.Encoding == PdfStringEncoding.Unicode || da.Value.Any(c => c > 255)
                    ? 2 + Encoding.BigEndianUnicode.GetByteCount(da.Value) : da.Value.Length;
                if (encoded > appearanceLength)
                    throw LimitError();
                PdfString scaled;
                try { scaled = PdfMergeStructure.ScaleAppearance(da, transform.Scale); }
                catch (InvalidDataException) { throw new OptionalGeometryException(); }
                var bytes = scaled.Encoding == PdfStringEncoding.Unicode || scaled.Value.Any(c => c > 255)
                    ? 2 + Encoding.BigEndianUnicode.GetByteCount(scaled.Value) : scaled.Value.Length;
                if (bytes > limits.DaBytes - _daBytes)
                    throw LimitError();
                _daBytes += bytes;
                copy.Elements["/DA"] = scaled;
            }
        }
        return copy;
    }

    private PdfArray Points(PdfArray array, Matrix matrix, Changes changes)
    {
        if (array.Elements.Count % 2 != 0)
            throw new OptionalGeometryException();
        changes.Coordinates(array.Elements.Count);
        var result = changes.Array(array.Elements.Count);
        for (var i = 0; i < array.Elements.Count; i += 2)
        {
            var (x, y) = matrix.Point(Number(array.Elements[i]), Number(array.Elements[i + 1]));
            if (!Finite(x, y))
                throw new OptionalGeometryException();
            result.Elements.Add(new PdfReal(x));
            result.Elements.Add(new PdfReal(y));
        }
        return result;
    }

    private PdfDictionary Appearance(PdfDictionary original, Box rect, Box changedRect, Matrix transform, Changes changes)
    {
        changes.States++;
        changes.Check();
        changes.Coordinates(original.Elements.ContainsKey("/Matrix") ? 10 : 4);
        var bbox = ReadBox(original.Elements["/BBox"]);
        var matrix = new Matrix(1, 0, 0, 1, 0, 0);
        if (original.Elements.ContainsKey("/Matrix"))
        {
            if (Array(original.Elements["/Matrix"]) is not { Elements.Count: 6 } values)
                throw new OptionalGeometryException();
            matrix = new(Number(values.Elements[0]), Number(values.Elements[1]), Number(values.Elements[2]),
                Number(values.Elements[3]), Number(values.Elements[4]), Number(values.Elements[5]));
        }
        var bounds = matrix.Bounds(bbox);
        if (!bbox.Valid || !Finite(matrix.Values) || !bounds.Valid)
            throw new OptionalGeometryException();
        var x = rect.Width / bounds.Width;
        var y = rect.Height / bounds.Height;
        var fit = new Matrix(x, 0, 0, y, rect.X1 - x * bounds.X1, rect.Y1 - y * bounds.Y1);
        var placed = fit.Then(transform);
        if (!Finite(placed.Values) || !Finite(x, y) || x <= 0 || y <= 0)
            throw new OptionalGeometryException();
        var wrapper = changes.Dictionary(extra: 6);
        wrapper.Elements["/Type"] = new PdfName("/XObject");
        wrapper.Elements["/Subtype"] = new PdfName("/Form");
        wrapper.Elements["/BBox"] = changes.NumbersArray(changedRect.Values);
        wrapper.Elements["/Matrix"] = changes.NumbersArray([1, 0, 0, 1, 0, 0]);
        var resources = changes.Dictionary(extra: 1);
        var forms = changes.Dictionary(extra: 1);
        if (original.Reference is null)
            throw new OptionalGeometryException();
        forms.Elements["/Original"] = original.Reference;
        resources.Elements["/XObject"] = forms;
        wrapper.Elements["/Resources"] = resources;
        var text = "q\n" + Cm(placed) + "/Original Do\nQ\n";
        var bytes = Encoding.ASCII.GetByteCount(text);
        if (bytes > limits.StreamBytes - _streamBytes)
            throw LimitError();
        _streamBytes += bytes;
        wrapper.CreateStream(Encoding.ASCII.GetBytes(text));
        changes.Streams.Add(wrapper);
        return wrapper;
    }
}
