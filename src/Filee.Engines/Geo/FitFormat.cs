// Garmin FIT activity records. The file and record limits are checked before decoding; fields outside the activity
// profile (including developer fields) are consumed without adding them to the output.

using System.Buffers.Binary;
using System.Globalization;
using Filee.Core.Conversion;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Geo;

internal sealed record FitRecord(DateTime? Time, double? Latitude, double? Longitude, double? Elevation,
    int? HeartRate, int? Cadence, int? Power, double? Speed, double? Distance);

internal sealed record FitSession(DateTime? StartTime, int? Sport)
{
    public string? SportName => Sport is { } sport ? FitFormat.SportName(sport) : null;
}

internal sealed class FitActivity
{
    public List<FitRecord> Records { get; } = [];
    public List<FitSession> Sessions { get; } = [];
}

internal static class FitFormat
{
    internal const long MaxFileBytes = 64L << 20;
    internal const int MaxRecords = 2_000_000;
    private const long FitEpochOffset = 631065600;

    private static readonly string?[] FileNames = Names(255,
        (1, "device"), (2, "settings"), (3, "sport"), (4, "activity"), (5, "workout"),
        (6, "course"), (7, "schedules"), (9, "weight"), (10, "totals"), (11, "goals"),
        (14, "blood_pressure"), (15, "monitoring_a"), (20, "activity_summary"),
        (28, "monitoring_daily"), (32, "monitoring_b"), (34, "segment"), (35, "segment_list"),
        (40, "exd_configuration"), (247, "mfg_range_min"), (254, "mfg_range_max"));

    private static readonly string?[] SportNames = Names(255,
        (0, "generic"), (1, "running"), (2, "cycling"), (3, "transition"), (4, "fitness_equipment"),
        (5, "swimming"), (6, "basketball"), (7, "soccer"), (8, "tennis"), (9, "american_football"),
        (10, "training"), (11, "walking"), (12, "cross_country_skiing"), (13, "alpine_skiing"),
        (14, "snowboarding"), (15, "rowing"), (16, "mountaineering"), (17, "hiking"), (18, "multisport"),
        (19, "paddling"), (20, "flying"), (21, "e_biking"), (22, "motorcycling"), (23, "boating"),
        (24, "driving"), (25, "golf"), (26, "hang_gliding"), (27, "horseback_riding"), (28, "hunting"),
        (29, "fishing"), (30, "inline_skating"), (31, "rock_climbing"), (32, "sailing"), (33, "ice_skating"),
        (34, "sky_diving"), (35, "snowshoeing"), (36, "snowmobiling"), (37, "stand_up_paddleboarding"),
        (38, "surfing"), (39, "wakeboarding"), (40, "water_skiing"), (41, "kayaking"), (42, "rafting"),
        (43, "windsurfing"), (44, "kitesurfing"), (45, "tactical"), (46, "jumpmaster"), (47, "boxing"),
        (48, "floor_climbing"), (49, "baseball"), (53, "diving"), (56, "shooting"), (58, "winter_sport"),
        (59, "grinding"), (62, "hiit"), (63, "video_gaming"), (64, "racket"), (65, "wheelchair_push_walk"),
        (66, "wheelchair_push_run"), (67, "meditation"), (68, "para_sport"), (69, "disc_golf"),
        (70, "team_sport"), (71, "cricket"), (72, "rugby"), (73, "hockey"), (74, "lacrosse"),
        (75, "volleyball"), (76, "water_tubing"), (77, "wakesurfing"), (78, "water_sport"), (79, "archery"),
        (80, "mixed_martial_arts"), (81, "motor_sports"), (82, "snorkeling"), (83, "dance"),
        (84, "jump_rope"), (85, "pool_apnea"), (86, "mobility"), (87, "geocaching"), (88, "canoeing"),
        (254, "all"));

    private static string?[] Names(int count, params (int Value, string Name)[] entries)
    {
        var names = new string?[count];
        foreach (var (value, name) in entries)
            names[value] = name;
        return names;
    }

    internal static string SportName(int sport) =>
        sport >= 0 && sport < SportNames.Length && SportNames[sport] is { } name
            ? name : sport.ToString(CultureInfo.InvariantCulture);

    /// <summary>One activity becomes one point or one track; records without positions stay in the CSV only.</summary>
    public static GeoDataset ToDataset(FitActivity activity, string path)
    {
        var positioned = activity.Records.Where(record => record.Latitude is not null && record.Longitude is not null).ToList();
        if (positioned.Count == 0)
            throw new ConversionRefusedException("error.fit_no_positions");

        var properties = new List<KeyValuePair<string, object?>>();
        var sports = activity.Sessions.Select(session => session.SportName).OfType<string>().Distinct().ToArray();
        if (sports.Length > 0)
            properties.Add(new("type", string.Join(", ", sports)));
        DateTime? start = positioned.Count == 1 ? positioned[0].Time
            : activity.Sessions.Select(session => session.StartTime).Where(time => time is not null).Order().FirstOrDefault()
                ?? activity.Records.Select(record => record.Time).FirstOrDefault(time => time is not null);
        if (start is { } time)
            properties.Add(new("time", time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

        var points = positioned.Select(record => new Coord(record.Longitude!.Value, record.Latitude!.Value,
            record.Elevation ?? double.NaN)).ToArray();
        GeoGeometry geometry = points.Length == 1 ? new GeoPoint(points[0]) :
            new GeoLineString(points, positioned.Any(record => record.Time is not null)
                ? positioned.Select(record => record.Time).ToArray() : null);
        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path), SparseHeights = true };
        dataset.Features.Add(new GeoFeature(geometry, properties));
        return dataset;
    }

    /// <summary>Reads one complete, CRC-checked FIT activity in file order.</summary>
    public static FitActivity Read(string path, CancellationToken ct, long maxBytes = MaxFileBytes, int maxRecords = MaxRecords)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[14];
        var headerLength = stream.ReadAtLeast(header, 14, throwOnEndOfStream: false);
        if (headerLength < 12 || header[0] < 12 || !header[8..12].SequenceEqual(".FIT"u8))
            throw new ConversionRefusedException("error.fit_not_fit");
        if (stream.Length > maxBytes)
            throw new ConversionRefusedException("error.fit_too_large", "64 MB");
        var size = header[0];
        if (size >= 14 && headerLength >= 14 && BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) is var headerCrc
            && headerCrc != 0 && headerCrc != FitCrc(header[..12]))
            throw Damaged("header CRC");

        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var end = (long)size + dataLength;
        if (end + 2 > stream.Length)
            throw Damaged("cut short");
        stream.Position = 0;
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (FitCrc(bytes.AsSpan(0, (int)end)) != BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)end, 2)))
            throw Damaged("CRC");
        if (end + 2 < bytes.Length)
            throw bytes.Length - end - 2 >= 12 && bytes.AsSpan((int)end + 10, 4).SequenceEqual(".FIT"u8)
                ? new ConversionRefusedException("error.fit_chained") : Damaged("extra bytes");

        var activity = new FitActivity();
        var definitions = new Definition?[16];
        int position = size;
        var limit = (int)end;
        uint? lastTime = null;
        int? fileType = null;
        var fileIdSeen = false;
        while (position < limit)
        {
            ct.ThrowIfCancellationRequested();
            var recordHeader = bytes[position++];
            var compressed = (recordHeader & 0x80) != 0;
            var local = compressed ? (recordHeader >> 5) & 3 : recordHeader & 15;
            if (!compressed && (recordHeader & 0x40) != 0)
            {
                Require(5);
                position++; // reserved
                var architecture = bytes[position++];
                if (architecture is not (0 or 1))
                    throw Damaged("architecture");
                var bigEndian = architecture == 1;
                var message = Read16(bytes.AsSpan(position, 2), bigEndian);
                position += 2;
                var fieldCount = bytes[position++];
                Require(fieldCount * 3);
                var fields = new Field[fieldCount];
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = new Field(bytes[position], bytes[position + 1], bytes[position + 2]);
                    position += 3;
                }
                var developerBytes = 0;
                if ((recordHeader & 0x20) != 0)
                {
                    Require(1);
                    var count = bytes[position++];
                    Require(count * 3);
                    for (var i = 0; i < count; i++)
                    {
                        developerBytes += bytes[position + 1];
                        position += 3;
                    }
                }
                definitions[local] = new Definition(message, bigEndian, fields, developerBytes);
                continue;
            }

            var definition = definitions[local] ?? throw Damaged($"undefined local message {local}");
            uint? time = null;
            if (compressed && lastTime is { } previous)
            {
                var offset = (uint)(recordHeader & 31);
                var reconstructed = (previous & ~31u) + offset + (offset < (previous & 31) ? 32u : 0u);
                if (reconstructed >= 0x10000000)
                    time = reconstructed;
            }
            double? lat = null, lon = null, height = null, enhancedHeight = null;
            double? speed = null, enhancedSpeed = null, distance = null;
            int? heartRate = null, cadence = null, power = null, sport = null;
            uint? startTime = null;
            foreach (var field in definition.Fields)
            {
                Require(field.Size);
                var value = ReadNumber(bytes.AsSpan(position, field.Size), field.Type, definition.BigEndian);
                position += field.Size;
                if (value is null)
                    continue;
                if (field.Number == 253 && value is >= 0x10000000 and < 0xFFFFFFFF)
                {
                    time = (uint)value.Value;
                    lastTime = time;
                }
                switch (definition.Message, field.Number)
                {
                    case (0, 0) when !fileIdSeen && value <= 254: fileType = (int)value.Value; break;
                    case (18, 2) when value is >= 0x10000000 and < 0xFFFFFFFF: startTime = (uint)value.Value; break;
                    case (18, 5) when value <= 254: sport = (int)value.Value; break;
                    case (20, 0): lat = value * 180 / 2147483648.0; break;
                    case (20, 1): lon = value * 180 / 2147483648.0; break;
                    case (20, 2): height = value / 5 - 500; break;
                    case (20, 3): heartRate = (int)value.Value; break;
                    case (20, 4): cadence = (int)value.Value; break;
                    case (20, 5): distance = value / 100; break;
                    case (20, 6): speed = value / 1000; break;
                    case (20, 7): power = (int)value.Value; break;
                    case (20, 73): enhancedSpeed = value / 1000; break;
                    case (20, 78): enhancedHeight = value / 5 - 500; break;
                }
            }
            Require(definition.DeveloperBytes);
            position += definition.DeveloperBytes;
            if (time is { } absolute)
                lastTime = absolute;
            switch (definition.Message)
            {
                case 0: fileIdSeen = true; break;
                case 18: activity.Sessions.Add(new FitSession(ToDate(startTime), sport)); break;
                case 20:
                    if (activity.Records.Count >= maxRecords)
                        throw new ConversionRefusedException("error.fit_too_large", "2000000 records");
                    if (lat is < -90 or > 90 || lon is < -180 or > 180 || lat is null || lon is null)
                        lat = lon = null;
                    activity.Records.Add(new FitRecord(ToDate(time), lat, lon, enhancedHeight ?? height,
                        heartRate, cadence, power, enhancedSpeed ?? speed, distance));
                    break;
            }
        }
        if (!fileIdSeen || fileType is null)
            throw new ConversionRefusedException("error.fit_no_file_id");
        if (fileType != 4)
            throw new ConversionRefusedException("error.fit_not_activity",
                fileType >= 0 && fileType < FileNames.Length && FileNames[fileType.Value] is { } name
                    ? name : $"type {fileType}");
        return activity;

        void Require(int count)
        {
            if (count > limit - position)
                throw Damaged("cut short");
        }
    }

    private static DateTime? ToDate(uint? seconds) =>
        seconds is { } value ? DateTime.UnixEpoch.AddSeconds(value + FitEpochOffset) : null;

    private static ushort Read16(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);

    private static double? ReadNumber(ReadOnlySpan<byte> bytes, byte baseType, bool bigEndian)
    {
        var type = baseType & 0x1F;
        var width = type switch
        {
            0 or 1 or 2 or 7 or 10 or 13 => 1,
            3 or 4 or 11 => 2,
            5 or 6 or 8 or 12 => 4,
            9 or 14 or 15 or 16 => 8,
            _ => 0,
        };
        if (width == 0 || bytes.Length < width || bytes.Length % width != 0 || type == 7)
            return null;
        bytes = bytes[..width];
        ulong raw = width switch
        {
            1 => bytes[0],
            2 => Read16(bytes, bigEndian),
            4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        };
        if (type is 8 or 9)
            return raw == (width == 4 ? uint.MaxValue : ulong.MaxValue) ? null
                : type == 8 ? BitConverter.Int32BitsToSingle((int)raw) : BitConverter.Int64BitsToDouble((long)raw);
        if (type is 10 or 11 or 12 or 16 && raw == 0)
            return null;
        if (type is 1 or 3 or 5 or 14)
        {
            var signed = width switch
            {
                1 => (sbyte)raw,
                2 => (short)raw,
                4 => (int)raw,
                _ => (long)raw,
            };
            return signed == (width switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => long.MaxValue })
                ? null : signed;
        }
        return raw == (width switch { 1 => byte.MaxValue, 2 => ushort.MaxValue, 4 => uint.MaxValue, _ => ulong.MaxValue })
            && type is not (10 or 11 or 12 or 16) ? null : raw;
    }

    /// <summary>Writes one nonempty record per CSV row, keeping file order and empty sensor cells.</summary>
    public static void WriteCsv(FitActivity activity, string path)
    {
        var rows = new List<List<string>>
        {
            new() { "time", "longitude", "latitude", "elevation", "heart_rate", "cadence", "power", "speed", "distance" },
        };
        foreach (var record in activity.Records)
        {
            if (record.Time is null && record.Latitude is null && record.Longitude is null
                && record.HeartRate is null && record.Cadence is null && record.Power is null
                && record.Speed is null && record.Distance is null)
                continue;
            rows.Add([
                record.Time?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "",
                record.Longitude is { } lon ? GeoText.Degrees(lon) : "",
                record.Latitude is { } lat ? GeoText.Degrees(lat) : "",
                record.Elevation is { } height ? GeoText.Height(height) : "",
                record.HeartRate?.ToString(CultureInfo.InvariantCulture) ?? "",
                record.Cadence?.ToString(CultureInfo.InvariantCulture) ?? "",
                record.Power?.ToString(CultureInfo.InvariantCulture) ?? "",
                record.Speed is { } speed ? GeoText.Number(speed) : "",
                record.Distance is { } distance ? GeoText.Number(distance) : "",
            ]);
        }
        if (rows.Count == 1)
            throw new ConversionRefusedException("error.fit_no_records");
        CsvFormat.WriteRows(rows, path);
    }

    private static ConversionRefusedException Damaged(string detail) => new("error.fit_damaged", detail);

    private sealed record Definition(int Message, bool BigEndian, Field[] Fields, int DeveloperBytes);
    private readonly record struct Field(byte Number, byte Size, byte Type);

    /// <summary>FIT's 16-entry nibble CRC over the complete supplied span.</summary>
    internal static ushort FitCrc(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<ushort> table = [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
            0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc = (ushort)((crc >> 4) ^ table[(crc ^ value) & 0xF]);
            crc = (ushort)((crc >> 4) ^ table[(crc ^ (value >> 4)) & 0xF]);
        }
        return crc;
    }
}
