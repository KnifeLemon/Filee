// Property lists (.plist, issue #1256): the XML and the binary kind read into the data tree and converted to the data
// formats and to text, and files that are damaged or built to hurt the reader. The samples were written by Python's
// plistlib, an independent implementation of both kinds; the damaged ones are assembled here byte by byte.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Data;

namespace Filee.Engines.Tests;

public class PlistTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The same dictionary as <see cref="SampleXml"/>, as plistlib wrote it in the binary kind (400 bytes).</summary>
    private const string SampleBinaryBase64 =
        "YnBsaXN0MDDfEBABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZHh8gIScoKVVDb3VudFREYXRhVERhdGVYRGlzYWJsZWRVRW1wdHlX" +
        "RW5hYmxlZFRIdWdlVUxhcmdlVExpc3RWTWVkaXVtVE5hbWVYTmVnYXRpdmVWTmVzdGVkVVJhdGlvVVNtYWxsV1VuaWNvZGUQKkgA" +
        "AQJoZWxsbzNBxQqAOoAAAAhQCRQAAAAAAAAAAIAAAAAAAAAFEwAAAAEqBfIApBobHB1RYRABI0AEAAAAAAAAoRYSAAERcFVGaWxl" +
        "ZRP/////////+dIiIyQmVGRlZXBRa9ElG1F4UXYjP+AAAAAAAAAQyGkARwByAPwA3wBlACBl5Wcsip4ACAArADEANgA7AEQASgBS" +
        "AFcAXQBiAGkAbgB3AH4AhACKAJIAlACdAKYApwCoAKkAugDDAMgAygDMANUA1wDcAOIA6wDwAPUA9wD6APwA/gEHAQkAAAAAAAAC" +
        "AQAAAAAAAAAqAAAAAAAAAAAAAAAAAAABHA==";

    /// <summary>An NSKeyedArchiver-style file: its <c>$top</c> refers to <c>$objects</c> by UID.</summary>
    private const string KeyedBinaryBase64 =
        "YnBsaXN0MDDTAQIDBAUIWSRhcmNoaXZlclgkb2JqZWN0c1QkdG9wXxAPTlNLZXllZEFyY2hpdmVyogYHVSRudWxsVWhlbGxv0QkKVHJvb3SAAQgPGSInOTxCSEtQAAAAAAAAAQEAAAAAAAAACwAAAAAAAAAAAAAAAAAAAFI=";

    private const string SampleXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>Count</key>
            	<integer>42</integer>
            	<key>Data</key>
            	<data>
            	AAECaGVsbG8=
            	</data>
            	<key>Date</key>
            	<date>2023-05-17T12:30:45Z</date>
            	<key>Disabled</key>
            	<false/>
            	<key>Empty</key>
            	<string></string>
            	<key>Enabled</key>
            	<true/>
            	<key>Huge</key>
            	<integer>9223372036854775813</integer>
            	<key>Large</key>
            	<integer>5000000000</integer>
            	<key>List</key>
            	<array>
            		<string>a</string>
            		<integer>1</integer>
            		<real>2.5</real>
            		<array>
            			<true/>
            		</array>
            	</array>
            	<key>Medium</key>
            	<integer>70000</integer>
            	<key>Name</key>
            	<string>Filee</string>
            	<key>Negative</key>
            	<integer>-7</integer>
            	<key>Nested</key>
            	<dict>
            		<key>deep</key>
            		<dict>
            			<key>x</key>
            			<integer>1</integer>
            		</dict>
            		<key>k</key>
            		<string>v</string>
            	</dict>
            	<key>Ratio</key>
            	<real>0.5</real>
            	<key>Small</key>
            	<integer>200</integer>
            	<key>Unicode</key>
            	<string>Grüße 日本語</string>
            </dict>
            </plist>
            """;

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private string Sample(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] BinarySample => Convert.FromBase64String(SampleBinaryBase64);

    private static byte[] XmlSample => Encoding.UTF8.GetBytes(SampleXml.Replace("\r\n", "\n"));

    [Fact]
    public async Task The_binary_and_the_xml_kind_of_a_property_list_give_the_same_json()
    {
        var binary = await File.ReadAllTextAsync(await ConvertAsync(Sample("Info.plist", BinarySample), "json"), TestContext.Current.CancellationToken);
        var xml = await File.ReadAllTextAsync(await ConvertAsync(Sample("Info.plist", XmlSample), "json"), TestContext.Current.CancellationToken);

        Assert.Equal(xml, binary);
        Assert.Contains("\"Name\": \"Filee\"", binary);
        Assert.Contains("\"Unicode\": \"Grüße 日本語\"", binary);
        Assert.Contains("\"Date\": \"2023-05-17T12:30:45Z\"", binary);
        Assert.Contains("\"Data\": \"AAECaGVsbG8=\"", binary);
        Assert.Contains("\"Negative\": -7", binary);
        Assert.Contains("\"Large\": 5000000000", binary);
        Assert.Contains("\"Huge\": 9223372036854775813", binary); // wider than 64 bits signed: kept exactly
        Assert.Contains("\"Ratio\": 0.5", binary);
        Assert.Contains("\"Enabled\": true", binary);
        Assert.Contains("\"Disabled\": false", binary);
        Assert.Contains("\"Empty\": \"\"", binary);
    }

    [Fact]
    public void Values_keep_their_types_and_the_keys_their_order()
    {
        var tree = Assert.IsType<DataMap>(PlistData.Read(BinarySample));

        Assert.Equal(["Count", "Data", "Date", "Disabled", "Empty", "Enabled", "Huge", "Large", "List", "Medium", "Name", "Negative", "Nested", "Ratio", "Small", "Unicode"],
            tree.Keys);
        Assert.Equal(42L, tree["Count"]);
        Assert.Equal(200L, tree["Small"]);
        Assert.Equal(70000L, tree["Medium"]);
        Assert.Equal(new System.Numerics.BigInteger(long.MaxValue) + 6, tree["Huge"]);
        Assert.Equal(new DataDate("2023-05-17T12:30:45Z"), tree["Date"]);
        var list = Assert.IsType<List<object?>>(tree["List"]);
        Assert.Equal(["a", 1L, 2.5], list.Take(3));
        Assert.Equal(true, Assert.IsType<List<object?>>(list[3])[0]);
        var nested = Assert.IsType<DataMap>(tree["Nested"]);
        Assert.Equal(1L, Assert.IsType<DataMap>(nested["deep"])["x"]);
    }

    [Fact]
    public void A_uid_of_a_keyed_archive_is_written_the_way_plutil_writes_it()
    {
        var tree = Assert.IsType<DataMap>(PlistData.Read(Convert.FromBase64String(KeyedBinaryBase64)));

        Assert.Equal("NSKeyedArchiver", tree["$archiver"]);
        Assert.Equal(["$null", "hello"], Assert.IsType<List<object?>>(tree["$objects"]));
        var root = Assert.IsType<DataMap>(Assert.IsType<DataMap>(tree["$top"])["root"]);
        Assert.Equal(1L, root[PlistData.UidKey]);
    }

    [Fact]
    public async Task A_property_list_becomes_a_text_listing_with_the_other_data_formats_beside_it()
    {
        var path = Sample("Settings.plist", BinarySample);

        var text = await File.ReadAllBytesAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, text[..3]); // UTF-8 with a BOM, like other TXT files
        var listing = Encoding.UTF8.GetString(text, 3, text.Length - 3);
        Assert.Contains("Name: Filee", listing);
        Assert.Contains("Unicode: Grüße 日本語", listing);

        var yaml = await File.ReadAllTextAsync(await ConvertAsync(path, "yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("Name: Filee", yaml);
        var toml = await File.ReadAllTextAsync(await ConvertAsync(path, "toml"), TestContext.Current.CancellationToken);
        Assert.Contains("Date = 2023-05-17T12:30:45Z", toml); // a real TOML date
        var xml = await File.ReadAllTextAsync(await ConvertAsync(path, "xml"), TestContext.Current.CancellationToken);
        Assert.Contains("<Name>Filee</Name>", xml);
    }

    [Fact]
    public async Task A_list_of_dictionaries_becomes_a_table()
    {
        var plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><array>
              <dict><key>app</key><string>Mail</string><key>build</key><integer>12</integer></dict>
              <dict><key>app</key><string>Maps</string><key>build</key><integer>7</integer></dict>
            </array></plist>
            """;

        var csv = await File.ReadAllTextAsync(await ConvertAsync(Sample("apps.plist", Encoding.UTF8.GetBytes(plist)), "csv"), TestContext.Current.CancellationToken);

        Assert.Contains("Mail,12", csv);
        Assert.Contains("Maps,7", csv);
    }

    [Fact]
    public void Property_lists_are_read_only_and_reach_text_in_one_step()
    {
        var planner = fx.Catalog.CreatePlanner();

        Assert.Equal("data", Assert.Single(planner.Plan("plist", "txt")!.Steps).Converter.Id);
        Assert.Equal("data", Assert.Single(planner.Plan("plist", "json")!.Steps).Converter.Id);
        Assert.Null(planner.Plan("json", "plist"));
    }

    [Fact]
    public async Task A_damaged_property_list_fails_with_a_message_and_leaves_no_output()
    {
        var path = Sample("broken.plist", BinarySample[..200]);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "json" });

        Assert.NotEqual(JobState.Completed, job.State);
        Assert.Empty(job.Outputs);
        Assert.Contains("damaged", string.Join(" ", job.Files.Select(f => f.ErrorDetail)));
    }

    // ───────────────────────── XML property lists ─────────────────────────

    private static object? Xml(string body, string head = "") =>
        PlistData.Read(Encoding.UTF8.GetBytes($"""<?xml version="1.0" encoding="UTF-8"?>{head}<plist version="1.0">{body}</plist>"""));

    [Fact]
    public void The_dtd_that_names_apples_site_is_ignored_and_never_fetched()
    {
        // The sample has the DOCTYPE every plist has. A DTD that defines entities is not expanded either.
        Assert.NotNull(PlistData.Read(XmlSample));
        var bomb = """<!DOCTYPE plist [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>""";

        Assert.Throws<InvalidDataException>(() => Xml("<string>&b;</string>", bomb));
    }

    [Theory]
    [InlineData("<dict><key>a</key></dict>", "without a value")]
    [InlineData("<dict><string>a</string><string>b</string></dict>", "where a <key> is expected")]
    [InlineData("<widget/>", "Unknown property list element <widget>")]
    [InlineData("<integer>twelve</integer>", "is not an <integer>")]
    [InlineData("<real>1.2.3</real>", "is not a <real>")]
    [InlineData("<string>a</string><string>b</string>", "exactly one value")]
    [InlineData("", "exactly one value")]
    public void An_xml_property_list_that_does_not_follow_the_format_is_refused(string body, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => Xml(body)).Message);

    [Fact]
    public void Xml_that_is_no_property_list_and_text_that_is_no_xml_are_refused()
    {
        Assert.Contains("no <plist> root", Assert.Throws<InvalidDataException>(() => PlistData.Read("<root/>"u8.ToArray())).Message);
        Assert.Contains("neither binary", Assert.Throws<InvalidDataException>(() => PlistData.Read("just some text"u8.ToArray())).Message);
        Assert.Throws<InvalidDataException>(() => PlistData.Read([]));
    }

    [Fact]
    public void Reals_integers_and_dates_of_xml_plists_are_read_the_way_apple_writes_them()
    {
        var tree = Assert.IsType<List<object?>>(Xml("<array><real>nan</real><real>+infinity</real><real>-infinity</real><real>1e3</real>"
            + "<integer> -12 </integer><integer>123456789012345678901234567890</integer><date>2001-01-01T00:00:00Z</date><data>\n AAEC\n AwQF \n</data></array>"));

        Assert.True(double.IsNaN((double)tree[0]!));
        Assert.Equal(double.PositiveInfinity, tree[1]);
        Assert.Equal(double.NegativeInfinity, tree[2]);
        Assert.Equal(1000.0, tree[3]);
        Assert.Equal(-12L, tree[4]);
        Assert.Equal(System.Numerics.BigInteger.Parse("123456789012345678901234567890"), tree[5]);
        Assert.Equal(new DataDate("2001-01-01T00:00:00Z"), tree[6]);
        Assert.Equal("AAECAwQF", tree[7]);
    }

    [Fact]
    public void An_xml_property_list_nested_too_deeply_is_refused()
    {
        var depth = DataTree.MaxDepth + 50;

        var error = Assert.Throws<InvalidDataException>(() => Xml(string.Concat(Enumerable.Repeat("<array>", depth)) + string.Concat(Enumerable.Repeat("</array>", depth))));

        Assert.Contains("nested too deeply", error.Message);
    }

    // ───────────────────────── Damaged or hostile binary property lists ─────────────────────────

    /// <summary>Marker byte, then the number of items (for items fewer than 15).</summary>
    private static byte[] Marker(int type, int count) => [(byte)((type << 4) | count)];

    private static byte[] Array(params int[] items) => [.. Marker(0xA, items.Length), .. items.Select(i => (byte)i)];

    private static byte[] Dict(int[] keys, int[] values) =>
        [.. Marker(0xD, keys.Length), .. keys.Select(i => (byte)i), .. values.Select(i => (byte)i)];

    private static byte[] Text(string text) => [.. Marker(0x5, text.Length), .. Encoding.ASCII.GetBytes(text)];

    /// <summary>
    /// A bplist00 file from raw objects: header, objects, a table of 2-byte offsets and the trailer. Everything the
    /// trailer says can be overridden, to build files that lie.
    /// </summary>
    private static byte[] Crafted(IReadOnlyList<byte[]> objects, int top = 0, int? objectCount = null, int? tableOffset = null, int[]? offsets = null,
        string version = "00", int referenceSize = 1)
    {
        var file = new List<byte>(Encoding.ASCII.GetBytes("bplist" + version));
        var positions = new List<int>();
        foreach (var item in objects)
        {
            positions.Add(file.Count);
            file.AddRange(item);
        }
        var table = file.Count;
        foreach (var position in offsets ?? positions.ToArray())
        {
            file.Add((byte)(position >> 8));
            file.Add((byte)position);
        }
        var trailer = new byte[32];
        trailer[6] = 2; // bytes per offset
        trailer[7] = (byte)referenceSize; // bytes per reference
        BitConverter.GetBytes((long)(objectCount ?? objects.Count)).Reverse().ToArray().CopyTo(trailer, 8);
        BitConverter.GetBytes((long)top).Reverse().ToArray().CopyTo(trailer, 16);
        BitConverter.GetBytes((long)(tableOffset ?? table)).Reverse().ToArray().CopyTo(trailer, 24);
        file.AddRange(trailer);
        return [.. file];
    }

    [Fact]
    public void A_crafted_file_without_tricks_reads()
    {
        var tree = PlistData.Read(Crafted([Dict([1], [2]), Text("key"), Text("value")]));

        Assert.Equal("value", Assert.IsType<DataMap>(tree)["key"]);
    }

    public static TheoryData<string, byte[], string> DamagedFiles => new()
    {
        { "a value that contains itself", Crafted([Array(0)]), "contains itself" },
        { "two values that contain each other", Crafted([Array(1), Array(0)]), "contains itself" },
        { "a reference to nothing", Crafted([Array(9)]), "does not exist" },
        { "a top object that does not exist", Crafted([Text("a")], top: 4), "top object" },
        { "no objects", Crafted([Text("a")], objectCount: 0), "offset table" },
        { "more objects than the file can hold", Crafted([Text("a")], objectCount: 5000), "offset table" },
        { "an offset table past the end", Crafted([Text("a")], tableOffset: 5000), "offset table" },
        { "an offset table inside the header", Crafted([Text("a")], tableOffset: 3), "offset table" },
        { "an object offset in the header", Crafted([Text("a")], offsets: [2]), "outside the object area" },
        { "an object offset in the offset table", Crafted([Text("a")], offsets: [500]), "outside the object area" },
        { "a dictionary key that is no text", Crafted([Dict([1], [1]), new byte[] { 0x10, 0x01 }]), "key is not text" },
        { "an unknown object type", Crafted([[0xE0]]), "unknown type" },
        { "an unknown simple value", Crafted([[0x05]]), "unknown marker" },
        { "a string longer than the file", Crafted([[0x5F, 0x12, 0x7F, 0xFF, 0xFF, 0xFF]]), "longer than the file" },
        { "a string length too large for its place", Crafted([[0x5F, 0x13, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]]), "too large" },
        { "a string length that is no integer", Crafted([[0x5F, 0x50]]), "length is not an integer" },
        { "a string cut off by the end of the objects", Crafted([[0x5A, 0x61]]), "cut off" },
        { "an array with more items than bytes", Crafted([[0xAF, 0x10, 0x20]]), "cut off" },
        { "an integer of 32 kilobytes", Crafted([[0x1F, 0, 0]]), "invalid size" },
        { "a real number of 3 bytes", Crafted([[0x21, 0, 0, 0]]), "invalid size" },
        { "a date that is not 8 bytes", Crafted([[0x32, 0, 0]]), "not 8 bytes" },
        { "an unknown version", Crafted([Text("a")], version: "15"), "version" },
        { "nothing but the header", Convert.FromBase64String("YnBsaXN0MDA="), "too short" },
    };

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void A_damaged_binary_property_list_is_refused_with_a_reason(string what, byte[] file, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => PlistData.Read(file));

        Assert.True(error.Message.Contains(message, StringComparison.Ordinal), $"{what}: {error.Message}");
    }

    [Fact]
    public void Every_cut_of_a_real_binary_property_list_is_refused_or_read_but_never_crashes()
    {
        var file = BinarySample;
        var tried = 0;
        for (var length = 0; length < file.Length; length++)
        {
            try
            {
                PlistData.Read(file[..length]);
            }
            catch (InvalidDataException)
            {
            }
            tried++;
        }
        Assert.Equal(file.Length, tried);
    }

    [Fact]
    public void Every_changed_byte_of_a_real_binary_property_list_is_refused_or_read_but_never_crashes()
    {
        var original = BinarySample;
        foreach (var value in new byte[] { 0x00, 0xFF, 0x0F, 0xAF, 0x7F })
        {
            for (var at = 0; at < original.Length; at++)
            {
                var file = (byte[])original.Clone();
                file[at] = value;
                try
                {
                    PlistData.Read(file);
                }
                catch (InvalidDataException)
                {
                }
            }
        }
    }

    [Fact]
    public void Shared_values_that_would_expand_into_billions_are_refused_quickly()
    {
        // Object 0 is a string, object k an array of ten references to object k - 1: the top one is 10^9 values in 100 bytes.
        List<byte[]> objects = [Text("x")];
        for (var level = 1; level <= 9; level++)
            objects.Add(Array(Enumerable.Repeat(level - 1, 10).ToArray()));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<InvalidDataException>(() => PlistData.Read(Crafted(objects, top: 9)));

        Assert.Contains("too many values", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void Shared_values_that_stay_small_are_fine()
    {
        // The same dictionary used 12 times, which Apple's own writer stores once.
        var tree = PlistData.Read(Crafted([Array([.. Enumerable.Repeat(1, 12)]), Dict([2], [3]), Text("k"), Text("v")]));

        var list = Assert.IsType<List<object?>>(tree);
        Assert.Equal(12, list.Count);
        Assert.All(list, item => Assert.Equal("v", Assert.IsType<DataMap>(item)["k"]));
    }

    [Fact]
    public void A_binary_property_list_nested_too_deeply_is_refused()
    {
        // 300 arrays inside each other, each pointing to the next with a 2-byte reference.
        List<byte[]> chain = [];
        for (var i = 0; i < DataTree.MaxDepth + 44; i++)
            chain.Add([0xA1, (byte)((i + 1) >> 8), (byte)(i + 1)]);
        chain.Add(Text("end"));

        var error = Assert.Throws<InvalidDataException>(() => PlistData.Read(Crafted(chain, referenceSize: 2)));

        Assert.Contains("nested too deeply", error.Message);
    }

    [Fact]
    public void Odd_but_valid_objects_are_read()
    {
        // null, false, true, a UTF-8 string (0x7), a UTF-16 string (0x6), a 16-byte integer, a 4-byte real and a date.
        var sixteen = new byte[] { 0x14, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE };
        var date = new byte[] { 0x33, 0x41, 0xB0, 0xE4, 0x00, 0x00, 0x00, 0x00, 0x00 }; // 2^26-ish seconds after 2001
        var tree = Assert.IsType<List<object?>>(PlistData.Read(Crafted(
        [
            Array(1, 2, 3, 4, 5, 6, 7, 8),
            [0x00], [0x08], [0x09], [0x73, (byte)'h', (byte)'i', (byte)'!'], [0x62, 0x00, 0x48, 0x65, 0xE5], sixteen,
            [0x22, 0x3F, 0xC0, 0x00, 0x00], date,
        ])));

        Assert.Null(tree[0]);
        Assert.Equal(false, tree[1]);
        Assert.Equal(true, tree[2]);
        Assert.Equal("hi!", tree[3]);
        Assert.Equal("H\u65E5", tree[4]); // H and 日
        Assert.Equal(-2L, tree[5]);
        Assert.Equal(1.5, tree[6]);
        Assert.IsType<DataDate>(tree[7]);
    }
}
