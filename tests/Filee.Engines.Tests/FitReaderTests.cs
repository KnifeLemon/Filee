using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class FitReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static DateTime Time(uint value) => DateTime.UnixEpoch.AddSeconds(value + 631065600L);

    private FitActivity Read(FitBuilder builder, int headerSize = 14, bool headerCrc = true) =>
        FitFormat.Read(FitBuilder.Save(fx.NewFolder(), builder.Build(headerSize, headerCrc)), CancellationToken.None);

    private void Refused(byte[] bytes, string key, string? detail = null)
    {
        var path = FitBuilder.Save(fx.NewFolder(), bytes);
        var error = Assert.Throws<ConversionRefusedException>(() => FitFormat.Read(path, CancellationToken.None));
        Assert.Equal(key, error.ErrorKey);
        Assert.Equal(detail, error.Detail);
    }

    [Fact]
    public void Garmin_header_crc_matches_published_vector()
    {
        Assert.Equal(0x8596, FitFormat.FitCrc(Convert.FromHexString("0E104308780609002E464954")));
    }

    [Fact]
    public void Record_values_enhanced_precedence_big_endian_and_sessions_are_read_in_file_order()
    {
        var first = new[] { FitBuilder.U32(253, 0x1000003B), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, 3000), FitBuilder.U8(3, 72),
            FitBuilder.U8(4, 88), FitBuilder.U32(5, 12345), FitBuilder.U16(6, 4321), FitBuilder.U16(7, 210),
            FitBuilder.U32(73, 5678), FitBuilder.U32(78, 4000) };
        var next = first.Select(f => f.Number == 253 ? FitBuilder.U32(253, 0x10000040) : f).ToArray();
        var activity = Read(new FitBuilder().FileId().Define(0, 20, fields: first).Message(0, first)
            .Session(0x10000035, 1).Session(0x10000030, 2)
            .Message(0, next));

        Assert.Equal(2, activity.Records.Count);
        var record = activity.Records[0];
        Assert.Equal(Time(0x1000003B), record.Time);
        Assert.Equal(45, record.Latitude);
        Assert.Equal(90, record.Longitude);
        Assert.Equal(300, record.Elevation); // enhanced 4000 / 5 - 500, not ordinary 3000 / 5 - 500
        Assert.Equal(72, record.HeartRate);
        Assert.Equal(88, record.Cadence);
        Assert.Equal(210, record.Power);
        Assert.Equal(5.678, record.Speed);
        Assert.Equal(123.45, record.Distance);
        Assert.Equal(Time(0x10000040), activity.Records[1].Time);
        Assert.Equal([Time(0x10000035), Time(0x10000030)], activity.Sessions.Select(s => s.StartTime));
        Assert.Equal(["running", "cycling"], activity.Sessions.Select(s => s.SportName));
        Assert.Equal("200", FitFormat.SportName(200));
        Assert.Equal("canoeing", FitFormat.SportName(88));

        var big = new[] { FitBuilder.U32(253, 0x1000003B, big: true), FitBuilder.S32(0, 0x20000000, big: true),
            FitBuilder.S32(1, 0x40000000, big: true), FitBuilder.U32(73, 5678, big: true) };
        var bigActivity = Read(new FitBuilder().FileId().Define(0, 20, big: true, fields: big).Message(0, big));
        Assert.Equal(record.Time, bigActivity.Records[0].Time);
        Assert.Equal(record.Latitude, bigActivity.Records[0].Latitude);
        Assert.Equal(record.Longitude, bigActivity.Records[0].Longitude);
        Assert.Equal(record.Speed, bigActivity.Records[0].Speed);
    }

    [Fact]
    public void Missing_invalid_and_out_of_range_values_do_not_remove_other_record_data()
    {
        var fields = new[] { FitBuilder.U32(253, 0x1000003B), FitBuilder.S32(0, int.MaxValue),
            FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, ushort.MaxValue),
            FitBuilder.U8(3, byte.MaxValue), FitBuilder.U8(4, 80) };
        var outside = fields.Select(f => f.Number == 0 ? FitBuilder.S32(0, 0x41000000) : f).ToArray();
        var activity = Read(new FitBuilder().FileId().Define(0, 20, fields: fields).Message(0, fields).Message(0, outside));
        Assert.Equal(2, activity.Records.Count);
        Assert.All(activity.Records, record =>
        {
            Assert.Null(record.Latitude);
            Assert.Null(record.Longitude);
            Assert.Null(record.Elevation);
            Assert.Null(record.HeartRate);
            Assert.Equal(80, record.Cadence);
            Assert.Equal(Time(0x1000003B), record.Time);
        });
    }

    [Fact]
    public void Compressed_timestamps_roll_over_and_consume_all_defined_bytes()
    {
        var full = new[] { FitBuilder.U32(253, 0x1000003B), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000) };
        var position = new[] { FitBuilder.S32(0, 0x30000000), FitBuilder.S32(1, 0x40000000) };
        var changed = new[] { FitBuilder.U32(253, 0x10000055), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000) };
        var activity = Read(new FitBuilder().FileId()
            .Define(0, 20, fields: full).Message(0, full)
            .Define(1, 20, fields: position)
            .Compressed(1, 0x1D, position).Compressed(1, 0x05, position)
            .Message(0, full).Compressed(0, 0x06, changed)
            .Compressed(1, 0x17, position));
        Assert.Equal(new uint[] { 0x1000003B, 0x1000003D, 0x10000045, 0x1000003B, 0x10000055, 0x10000057 }
            .Select(value => (DateTime?)Time(value)).ToArray(), activity.Records.Select(r => r.Time).ToArray());
        Assert.Equal([45.0, 67.5, 67.5, 45.0, 45.0, 67.5], activity.Records.Select(r => r.Latitude));
    }

    [Fact]
    public void Twelve_byte_zero_crc_and_developer_messages_leave_records_unchanged()
    {
        var fields = new[] { FitBuilder.U32(253, 0x1000003B), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000) };
        foreach (var (size, crc) in new[] { (12, false), (14, false), (14, true) })
        {
            var record = Read(new FitBuilder().FileId()
                .Define(3, 207, fields: [FitBuilder.U8(0, 1)]).Message(3, [FitBuilder.U8(0, 1)])
                .Define(3, 206, fields: [FitBuilder.U8(0, 1)]).Message(3, [FitBuilder.U8(0, 1)])
                .Define(3, 132, fields: [FitBuilder.U8(3, 99)]).Message(3, [FitBuilder.U8(3, 99)])
                .Define(0, 20, developer: [new(1, [9, 8])], fields: fields)
                .Message(0, fields, new FitBuilder.DeveloperField(1, [9, 8])), size, crc).Records.Single();
            Assert.Equal(Time(0x1000003B), record.Time);
            Assert.Equal(45, record.Latitude);
            Assert.Null(record.HeartRate);
        }
    }

    [Fact]
    public void Corruptions_are_refused_with_the_first_matching_key_and_detail()
    {
        var valid = new FitBuilder().FileId().Build();
        var cases = new (byte[] Bytes, string Key, string? Detail)[]
        {
            ([], "error.fit_not_fit", null),
            (Encoding.ASCII.GetBytes("SIMPLE  = FIT"), "error.fit_not_fit", null),
            (FitBuilder.FlipHeaderCrc(valid), "error.fit_damaged", "header CRC"),
            (FitBuilder.CutShort(valid), "error.fit_damaged", "cut short"),
            (valid[..12], "error.fit_damaged", "cut short"),
            (FitBuilder.FlipFileCrc(valid), "error.fit_damaged", "CRC"),
            (FitBuilder.ExtraBytes(valid), "error.fit_damaged", "extra bytes"),
            (FitBuilder.Chained(valid), "error.fit_chained", null),
            (FitBuilder.InvalidArchitecture(), "error.fit_damaged", "architecture"),
            (FitBuilder.UndefinedLocal(), "error.fit_damaged", "undefined local message 3"),
            (new FitBuilder().Build(), "error.fit_no_file_id", null),
            (new FitBuilder().FileId(255).Build(), "error.fit_no_file_id", null),
            (FitBuilder.RawData(0x40), "error.fit_damaged", "cut short"),
            (FitBuilder.RawData(0x40, 0, 0, 20, 0, 1, 0, 4, 0x86, 0), "error.fit_damaged", "cut short"),
        };
        foreach (var (bytes, key, detail) in cases)
            Refused(bytes, key, detail);
    }

    [Fact]
    public void Huge_declared_data_size_is_refused_before_allocating_it()
    {
        var bytes = new FitBuilder().FileId().Build(headerCrc: false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x7FFFFFFF);
        var path = FitBuilder.Save(fx.NewFolder(), bytes);
        // Warm the parser before measuring allocations in the deliberately short input.
        _ = Read(new FitBuilder().FileId());
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<ConversionRefusedException>(() => FitFormat.Read(path, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
        Assert.Equal("error.fit_damaged", error.ErrorKey);
        Assert.Equal("cut short", error.Detail);
    }

    [Fact]
    public void Configurable_file_and_record_limits_refuse_only_excess()
    {
        var fields = new[] { FitBuilder.U32(253, 0x1000003B), new FitBuilder.FitField(90, 0x02, new byte[100]) };
        var builder = new FitBuilder().FileId().Define(0, 20, fields: fields);
        for (var i = 0; i < 11; i++)
            builder.Message(0, fields);
        var path = FitBuilder.Save(fx.NewFolder(), builder.Build());
        Assert.Equal(11, FitFormat.Read(path, CancellationToken.None, maxBytes: 2000, maxRecords: 11).Records.Count);
        var records = Assert.Throws<ConversionRefusedException>(() => FitFormat.Read(path, CancellationToken.None, maxRecords: 10));
        Assert.Equal("error.fit_too_large", records.ErrorKey);
        Assert.Equal("2000000 records", records.Detail);
        var bytes = Assert.Throws<ConversionRefusedException>(() => FitFormat.Read(path, CancellationToken.None, maxBytes: 1000));
        Assert.Equal("error.fit_too_large", bytes.ErrorKey);
        Assert.Equal("64 MB", bytes.Detail);
    }

    [Theory]
    [InlineData(6, "course")]
    [InlineData(5, "workout")]
    [InlineData(2, "settings")]
    [InlineData(32, "monitoring_b")]
    [InlineData(200, "type 200")]
    public void Non_activity_file_types_are_refused_even_when_they_contain_records(byte type, string detail)
    {
        var fields = new[] { FitBuilder.U32(253, 0x1000003B) };
        var error = Assert.Throws<ConversionRefusedException>(() =>
            Read(new FitBuilder().FileId(type).Define(0, 20, fields: fields).Message(0, fields)));
        Assert.Equal("error.fit_not_activity", error.ErrorKey);
        Assert.Equal(detail, error.Detail);
    }

    [Fact]
    public void Fits_and_fixed_seed_random_inputs_only_produce_fit_refusals()
    {
        Refused(Encoding.ASCII.GetBytes("SIMPLE  =                    T"), "error.fit_not_fit");
        var random = new Random(451);
        for (var i = 0; i < 15; i++)
        {
            var bytes = new byte[random.Next(0, 3001)];
            random.NextBytes(bytes);
            var error = Assert.Throws<ConversionRefusedException>(() =>
                FitFormat.Read(FitBuilder.Save(fx.NewFolder(), bytes), CancellationToken.None));
            Assert.StartsWith("error.fit_", error.ErrorKey);
        }
        for (var i = 0; i < 15; i++)
        {
            var bytes = new byte[Math.Max(14, random.Next(0, 3001))];
            random.NextBytes(bytes);
            bytes[0] = 14;
            ".FIT"u8.CopyTo(bytes.AsSpan(8));
            var error = Assert.Throws<ConversionRefusedException>(() =>
                FitFormat.Read(FitBuilder.Save(fx.NewFolder(), bytes), CancellationToken.None));
            Assert.StartsWith("error.fit_", error.ErrorKey);
        }
    }

    [Fact]
    public void Relative_timestamps_never_create_an_absolute_time_or_replace_its_base()
    {
        var fields = new[] { FitBuilder.U32(253, 0x0FFFFFFF) };
        var absolute = new[] { FitBuilder.U32(253, 0x1000003B) };
        var empty = Array.Empty<FitBuilder.FitField>();
        var activity = Read(new FitBuilder().FileId()
            .Define(0, 20, fields: fields).Message(0, fields)
            .Define(1, 20, fields: empty).Compressed(1, 0, empty).Compressed(1, 1, empty)
            .Message(0, absolute).Message(0, fields).Compressed(1, 0x1D, empty));
        Assert.Equal([null, null, null, Time(0x1000003B), null, Time(0x1000003D)],
            activity.Records.Select(r => r.Time));
    }

    [Fact]
    public void Csv_has_exact_header_converted_units_and_only_substantive_records()
    {
        var fields = new[] { FitBuilder.U32(253, 0x1000003B), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, 3000), FitBuilder.U8(3, 72),
            FitBuilder.U8(4, 88), FitBuilder.U16(7, 210), FitBuilder.U16(6, 4321),
            FitBuilder.U32(5, 12345) };
        var activity = Read(new FitBuilder().FileId().Define(0, 20, fields: fields).Message(0, fields));
        activity.Records.Add(new FitRecord(null, null, null, 17, null, null, null, null, null));
        activity.Records.Add(new FitRecord(null, null, null, null, null, null, null, null, null));
        activity.Records.Add(new FitRecord(null, null, null, 12, 60, null, null, null, null));
        var path = Path.Combine(fx.NewFolder(), "records.csv");
        FitFormat.WriteCsv(activity, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("time,longitude,latitude,elevation,heart_rate,cadence,power,speed,distance\r\n" +
            $"{Time(0x1000003B):yyyy-MM-ddTHH:mm:ssZ},90,45,100,72,88,210,4.321,123.45\r\n" +
            ",,,12,60,,,,\r\n", Encoding.UTF8.GetString(bytes[3..]));
        var noRows = Assert.Throws<ConversionRefusedException>(() =>
            FitFormat.WriteCsv(new FitActivity(), Path.Combine(fx.NewFolder(), "empty.csv")));
        Assert.Equal("error.fit_no_records", noRows.ErrorKey);
        Assert.Null(noRows.Detail);
    }
}
