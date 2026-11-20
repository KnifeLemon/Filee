// JB2, DjVu's bilevel picture coding (DjVu v3 specification, "JB2"): the mask of a page (Sjbz) or a dictionary of
// shapes several pages share (Djbz). A stream is a list of records coded with the ZP-coder: new shapes coded pixel by
// pixel (a 10-pixel context), shapes coded as refinements of a library shape (an 11-pixel context over both), copies of
// library shapes, and where each one goes (relative to the shape before it or to the start of its line).
//
// Numbers are coded in a binary tree of contexts that grows as values are seen (the "number coder"): its cells are
// counted against DjvuLimits.MaxNumberCells, the shapes, blits and their pixels against the other JB2 limits.
//
// Bitmaps are kept as the specification draws them: one byte per pixel, row 0 at the bottom.

using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>One shape: its pixels (1 = black), row 0 at the bottom, and the box of its black pixels.</summary>
internal sealed class Jb2Shape(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bits { get; } = new byte[(long)width * height];

    /// <summary>The black pixels' box (left, bottom, right, top, inclusive); right &lt; left when the shape is empty.</summary>
    public (int Left, int Bottom, int Right, int Top) Box { get; private set; }

    public byte this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height ? Bits[y * Width + x] : (byte)0;

    public void ComputeBox()
    {
        int left = Width, right = -1, bottom = Height, top = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                if (Bits[row + x] == 0)
                    continue;
                if (x < left)
                    left = x;
                if (x > right)
                    right = x;
                if (y < bottom)
                    bottom = y;
                top = y;
            }
        }
        // An empty shape gets the box the specification's bounding box search ends with.
        Box = right < 0 ? (0, 0, -1, -1) : (left, bottom, right, top);
    }
}

/// <summary>A placed shape: its index and where its bottom-left pixel goes (row 0 at the bottom of the page).</summary>
internal readonly record struct Jb2Blit(int Shape, int Left, int Bottom);

/// <summary>Shapes a JB2 dictionary (Djbz) holds, inherited ones first.</summary>
internal sealed class Jb2Dictionary(List<Jb2Shape> shapes)
{
    public IReadOnlyList<Jb2Shape> Shapes { get; } = shapes;
}

/// <summary>A decoded JB2 page mask.</summary>
internal sealed class Jb2Image(int width, int height, List<Jb2Shape> shapes, List<Jb2Blit> blits)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyList<Jb2Shape> Shapes { get; } = shapes;
    public IReadOnlyList<Jb2Blit> Blits { get; } = blits;
}

/// <summary>Decodes JB2 streams.</summary>
internal sealed class Jb2Decoder
{
    private const int StartOfData = 0;
    private const int NewMark = 1;
    private const int NewMarkLibraryOnly = 2;
    private const int NewMarkImageOnly = 3;
    private const int MatchedRefine = 4;
    private const int MatchedRefineLibraryOnly = 5;
    private const int MatchedRefineImageOnly = 6;
    private const int MatchedCopy = 7;
    private const int NonMarkData = 8;
    private const int RequiredDictOrReset = 9;
    private const int PreservedComment = 10;
    private const int EndOfData = 11;

    private const int BigPositive = 262142;
    private const int BigNegative = -262143;

    private readonly ZpDecoder _zp;
    private readonly DjvuLimits _limits;
    private readonly Jb2Dictionary? _inherited;
    private readonly CancellationToken _cancellationToken;

    // The number coder's tree: a bit context per cell and its two children (0 = none yet). Cell 0 is unused.
    private byte[] _bits = new byte[1024];
    private int[] _left = new int[1024];
    private int[] _right = new int[1024];
    private int _cells = 1;

    private int _recordType, _matchIndex, _commentLength, _commentByte;
    private int _absLocX, _absLocY, _absSizeX, _absSizeY, _imageSize, _inheritedCount;
    private int _relLocXCurrent, _relLocXLast, _relLocYCurrent, _relLocYLast, _relSizeX, _relSizeY;
    private byte _refinementFlag, _offsetType;
    private readonly byte[] _direct = new byte[1024];
    private readonly byte[] _cross = new byte[2048];

    private bool _started;
    private bool _usesInherited;
    private int _columns, _rows;
    private int _lastLeft, _lastRight, _lastBottom, _lastRowLeft, _lastRowBottom;
    private readonly int[] _shortList = new int[3];
    private int _shortListPosition;

    private readonly List<Jb2Shape> _shapes = [];
    private readonly List<int> _library = []; // library index → shape index
    private readonly List<Jb2Blit> _blits = [];
    private long _shapePixels;

    private Jb2Decoder(byte[] data, Jb2Dictionary? inherited, DjvuLimits limits, CancellationToken cancellationToken)
    {
        _zp = new ZpDecoder(data);
        _inherited = inherited;
        _limits = limits;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Decodes a page mask (Sjbz); <paramref name="dictionary"/> is the shared dictionary it may ask for, or null.
    /// </summary>
    public static Jb2Image DecodeImage(byte[] data, Jb2Dictionary? dictionary, DjvuLimits limits, CancellationToken cancellationToken = default)
    {
        var decoder = new Jb2Decoder(data, dictionary, limits, cancellationToken);
        decoder.Run(image: true);
        return new Jb2Image(decoder._columns, decoder._rows, decoder._shapes, decoder._blits);
    }

    /// <summary>Decodes a shared dictionary (Djbz); <paramref name="inherited"/> is a dictionary it may build on.</summary>
    public static Jb2Dictionary DecodeDictionary(byte[] data, Jb2Dictionary? inherited, DjvuLimits limits, CancellationToken cancellationToken = default)
    {
        var decoder = new Jb2Decoder(data, inherited, limits, cancellationToken);
        decoder.Run(image: false);
        return new Jb2Dictionary(decoder._shapes);
    }

    private void Run(bool image)
    {
        var records = 0;
        while (true)
        {
            if ((++records & 0x3FF) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            var type = DecodeNumber(StartOfData, EndOfData, ref _recordType);
            if (type == EndOfData)
                break;
            if (!image && type is NewMark or NewMarkImageOnly or MatchedRefine or MatchedRefineImageOnly or MatchedCopy or NonMarkData)
                throw new InvalidDataException("A JB2 dictionary holds a record only a page may hold.");
            if (type is not (StartOfData or RequiredDictOrReset or PreservedComment) && !_started)
                throw new InvalidDataException("A JB2 stream places shapes before its start record.");
            switch (type)
            {
                case StartOfData:
                    Start(image);
                    break;
                case NewMark:
                    AddShape(DecodeDirect(DecodeAbsoluteSize()), library: true, placed: true);
                    break;
                case NewMarkLibraryOnly:
                    AddShape(DecodeDirect(DecodeAbsoluteSize()), library: true, placed: false);
                    break;
                case NewMarkImageOnly:
                    AddShape(DecodeDirect(DecodeAbsoluteSize()), library: false, placed: true);
                    break;
                case MatchedRefine:
                    AddShape(DecodeRefinement(), library: true, placed: true);
                    break;
                case MatchedRefineLibraryOnly:
                    AddShape(DecodeRefinement(), library: true, placed: false);
                    break;
                case MatchedRefineImageOnly:
                    AddShape(DecodeRefinement(), library: false, placed: true);
                    break;
                case MatchedCopy:
                    {
                        var match = DecodeMatch();
                        var shapeIndex = _library[match];
                        var box = _shapes[shapeIndex].Box;
                        var (left, bottom) = DecodeRelativeLocation(box.Top - box.Bottom + 1, box.Right - box.Left + 1);
                        AddBlit(shapeIndex, left - box.Left, bottom - box.Bottom);
                        break;
                    }
                case NonMarkData:
                    {
                        var shape = DecodeDirect(DecodeAbsoluteSize());
                        var index = Keep(shape, library: false);
                        var left = DecodeNumber(1, _columns, ref _absLocX);
                        var top = DecodeNumber(1, _rows, ref _absLocY);
                        AddBlit(index, left - 1, top - shape.Height);
                        break;
                    }
                case RequiredDictOrReset:
                    if (!_started)
                        RequireDictionary();
                    else
                        ResetNumbers();
                    break;
                case PreservedComment:
                    {
                        var length = DecodeNumber(0, BigPositive, ref _commentLength);
                        for (var i = 0; i < length; i++)
                            DecodeNumber(0, 255, ref _commentByte);
                        break;
                    }
                default:
                    throw new InvalidDataException("A JB2 stream has a record of unknown type.");
            }
        }
        if (!_started)
            throw new InvalidDataException("A JB2 stream has no start record.");
    }

    private void Start(bool image)
    {
        if (_started)
            throw new InvalidDataException("A JB2 stream has two start records.");
        var columns = DecodeNumber(0, BigPositive, ref _imageSize);
        var rows = DecodeNumber(0, BigPositive, ref _imageSize);
        if (image)
        {
            if (columns == 0 || rows == 0)
                throw new InvalidDataException("A JB2 page mask has no size.");
            if ((long)columns * rows > _limits.MaxPagePixels)
                throw DocumentLimits.TooLarge($"A DjVu page mask has more than {DocumentLimits.Count(_limits.MaxPagePixels)} pixels.");
            _columns = columns;
            _rows = rows;
            _lastLeft = 1 + columns;
            _lastRowBottom = rows;
        }
        else
        {
            if (columns != 0 || rows != 0)
                throw new InvalidDataException("A JB2 dictionary has a page size.");
            _lastLeft = 1;
            _lastRowBottom = 0;
        }
        _lastRowLeft = 0;
        _lastRight = 0;
        FillShortList(_lastRowBottom);
        _started = true;
        _zp.Decode(ref _refinementFlag);
        // The library starts with the inherited dictionary's shapes (all of them were library shapes there).
        if (_usesInherited && _inherited is not null)
        {
            foreach (var shape in _inherited.Shapes)
            {
                _library.Add(_shapes.Count);
                _shapes.Add(shape);
            }
        }
    }

    private void RequireDictionary()
    {
        var count = DecodeNumber(0, BigPositive, ref _inheritedCount);
        if (count == 0)
            return;
        if (_inherited is null)
            throw new InvalidDataException("A DjVu page needs a shared shape dictionary its file doesn't include.");
        if (_inherited.Shapes.Count != count)
            throw new InvalidDataException("A DjVu page's shared shape dictionary has another number of shapes than it expects.");
        _usesInherited = true;
    }

    private void ResetNumbers()
    {
        _recordType = _matchIndex = _commentLength = _commentByte = 0;
        _absLocX = _absLocY = _absSizeX = _absSizeY = _imageSize = _inheritedCount = 0;
        _relLocXCurrent = _relLocXLast = _relLocYCurrent = _relLocYLast = _relSizeX = _relSizeY = 0;
        Array.Clear(_bits, 0, _cells);
        Array.Clear(_left, 0, _cells);
        Array.Clear(_right, 0, _cells);
        _cells = 1;
    }

    private int DecodeMatch()
    {
        if (_library.Count == 0)
            throw new InvalidDataException("A JB2 stream refers to a library shape before it has any.");
        return DecodeNumber(0, _library.Count - 1, ref _matchIndex);
    }

    private (int Width, int Height) DecodeAbsoluteSize()
    {
        var width = DecodeNumber(0, BigPositive, ref _absSizeX);
        var height = DecodeNumber(0, BigPositive, ref _absSizeY);
        return CheckSize(width, height);
    }

    private (int Width, int Height) CheckSize(int width, int height)
    {
        if (width is < 0 or > ushort.MaxValue || height is < 0 or > ushort.MaxValue)
            throw new InvalidDataException("A JB2 shape has an impossible size.");
        _shapePixels += (long)width * height;
        if (_shapePixels > _limits.MaxShapePixels)
            throw DocumentLimits.TooLarge($"The shapes of a DjVu mask have more than {DocumentLimits.Count(_limits.MaxShapePixels)} pixels.");
        if (_shapes.Count >= _limits.MaxShapes)
            throw DocumentLimits.Exceeded($"A DjVu mask has more than {DocumentLimits.Count(_limits.MaxShapes)} shapes.");
        return (width, height);
    }

    /// <summary>A new shape coded pixel by pixel, top row first.</summary>
    private Jb2Shape DecodeDirect((int Width, int Height) size)
    {
        var shape = new Jb2Shape(size.Width, size.Height);
        var bits = shape.Bits;
        var w = size.Width;
        for (var y = size.Height - 1; y >= 0; y--)
        {
            if ((y & 0x3F) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                var context =
                    (shape[x - 1, y + 2] << 9) | (shape[x, y + 2] << 8) | (shape[x + 1, y + 2] << 7) |
                    (shape[x - 2, y + 1] << 6) | (shape[x - 1, y + 1] << 5) | (shape[x, y + 1] << 4) |
                    (shape[x + 1, y + 1] << 3) | (shape[x + 2, y + 1] << 2) |
                    (shape[x - 2, y] << 1) | shape[x - 1, y];
                bits[y * w + x] = (byte)_zp.Decode(ref _direct[context]);
            }
        }
        return shape;
    }

    /// <summary>A shape coded as a refinement of a library shape (its size relative to the library shape's box).</summary>
    private Jb2Shape DecodeRefinement()
    {
        var match = DecodeMatch();
        var reference = _shapes[_library[match]];
        var box = reference.Box;
        var dx = DecodeNumber(BigNegative, BigPositive, ref _relSizeX);
        var dy = DecodeNumber(BigNegative, BigPositive, ref _relSizeY);
        var (w, h) = CheckSize(box.Right - box.Left + 1 + dx, box.Top - box.Bottom + 1 + dy);
        var shape = new Jb2Shape(w, h);
        // Aligns the centres of the new shape and the reference's box.
        var xd2c = (w / 2 - w + 1) - ((box.Right - box.Left + 1) / 2 - box.Right);
        var yd2c = (h / 2 - h + 1) - ((box.Top - box.Bottom + 1) / 2 - box.Top);
        if (xd2c is < -15 or > 15 || yd2c is < -15 or > 15)
            throw new InvalidDataException("A JB2 refinement is too far from its library shape.");
        var bits = shape.Bits;
        for (var y = h - 1; y >= 0; y--)
        {
            if ((y & 0x3F) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            var cy = y + yd2c;
            for (var x = 0; x < w; x++)
            {
                var cx = x + xd2c;
                var context =
                    (shape[x - 1, y + 1] << 10) | (shape[x, y + 1] << 9) | (shape[x + 1, y + 1] << 8) |
                    (shape[x - 1, y] << 7) |
                    (reference[cx, cy + 1] << 6) |
                    (reference[cx - 1, cy] << 5) | (reference[cx, cy] << 4) | (reference[cx + 1, cy] << 3) |
                    (reference[cx - 1, cy - 1] << 2) | (reference[cx, cy - 1] << 1) | reference[cx + 1, cy - 1];
                bits[y * w + x] = (byte)_zp.Decode(ref _cross[context]);
            }
        }
        return shape;
    }

    private int Keep(Jb2Shape shape, bool library)
    {
        shape.ComputeBox();
        var index = _shapes.Count;
        _shapes.Add(shape);
        if (library)
            _library.Add(index);
        return index;
    }

    private void AddShape(Jb2Shape shape, bool library, bool placed)
    {
        var index = Keep(shape, library);
        if (!placed)
            return;
        var (left, bottom) = DecodeRelativeLocation(shape.Height, shape.Width);
        AddBlit(index, left, bottom);
    }

    private void AddBlit(int shape, int left, int bottom)
    {
        if (_blits.Count >= _limits.MaxBlits)
            throw DocumentLimits.Exceeded($"A DjVu mask places more than {DocumentLimits.Count(_limits.MaxBlits)} shapes.");
        _blits.Add(new Jb2Blit(shape, left, bottom));
    }

    /// <summary>Where a shape goes: after the one before it on its line, or at the start of a new line.</summary>
    private (int Left, int Bottom) DecodeRelativeLocation(int rows, int columns)
    {
        int left, bottom;
        if (_zp.Decode(ref _offsetType) != 0)
        {
            var dx = DecodeNumber(BigNegative, BigPositive, ref _relLocXLast);
            var dy = DecodeNumber(BigNegative, BigPositive, ref _relLocYLast);
            left = _lastRowLeft + dx;
            var top = _lastRowBottom + dy;
            bottom = top - rows + 1;
            _lastLeft = _lastRowLeft = left;
            _lastRight = left + columns - 1;
            _lastBottom = _lastRowBottom = bottom;
            FillShortList(bottom);
        }
        else
        {
            var dx = DecodeNumber(BigNegative, BigPositive, ref _relLocXCurrent);
            var dy = DecodeNumber(BigNegative, BigPositive, ref _relLocYCurrent);
            left = _lastRight + dx;
            bottom = _lastBottom + dy;
            _lastLeft = left;
            _lastRight = left + columns - 1;
            _lastBottom = UpdateShortList(bottom);
        }
        return (left - 1, bottom - 1);
    }

    private void FillShortList(int value)
    {
        _shortList[0] = _shortList[1] = _shortList[2] = value;
        _shortListPosition = 0;
    }

    /// <summary>Adds a bottom to the last three and returns their median.</summary>
    private int UpdateShortList(int value)
    {
        if (++_shortListPosition == 3)
            _shortListPosition = 0;
        var s = _shortList;
        s[_shortListPosition] = value;
        return s[0] >= s[1]
            ? (s[0] > s[2] ? (s[1] >= s[2] ? s[1] : s[2]) : s[0])
            : (s[0] < s[2] ? (s[1] >= s[2] ? s[2] : s[1]) : s[0]);
    }

    /// <summary>A number in [<paramref name="low"/>, <paramref name="high"/>] from the tree rooted at <paramref name="root"/>.</summary>
    private int DecodeNumber(int low, int high, ref int root)
    {
        var negative = false;
        var cutoff = 0;
        var phase = 1;
        var range = -1;
        if (root == 0)
            root = NewCell();
        var cell = root;
        while (true)
        {
            var decision = low >= cutoff || (high >= cutoff && _zp.Decode(ref _bits[cell]) != 0);
            switch (phase)
            {
                case 1:
                    negative = !decision;
                    if (negative)
                    {
                        var temp = -low - 1;
                        low = -high - 1;
                        high = temp;
                    }
                    phase = 2;
                    cutoff = 1;
                    break;
                case 2:
                    if (!decision)
                    {
                        phase = 3;
                        range = (cutoff + 1) / 2;
                        if (range == 1)
                            cutoff = 0;
                        else
                            cutoff -= range / 2;
                    }
                    else
                    {
                        cutoff += cutoff + 1;
                    }
                    break;
                default:
                    range /= 2;
                    if (range != 1)
                        cutoff += decision ? range / 2 : -(range / 2);
                    else if (!decision)
                        cutoff--;
                    break;
            }
            if (range == 1)
                break;
            var next = decision ? _right[cell] : _left[cell];
            if (next == 0)
            {
                next = NewCell();
                if (decision)
                    _right[cell] = next;
                else
                    _left[cell] = next;
            }
            cell = next;
        }
        return negative ? -cutoff - 1 : cutoff;
    }

    private int NewCell()
    {
        if (_cells >= _limits.MaxNumberCells)
            throw DocumentLimits.Exceeded("A DjVu mask codes more numbers than Filee reads.");
        if (_cells == _bits.Length)
        {
            var size = Math.Min((long)_bits.Length * 2, _limits.MaxNumberCells);
            Array.Resize(ref _bits, (int)size);
            Array.Resize(ref _left, (int)size);
            Array.Resize(ref _right, (int)size);
        }
        return _cells++;
    }
}
