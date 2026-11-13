// Missing filesystem values preserve old behavior and explicit UDF values survive the real preset JSON path.

using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class Udf1443Tests
{
    [Fact]
    public void Legacy_presets_default_to_joliet_and_udf_is_serialized_explicitly()
    {
        var old = JsonSerializer.Deserialize("{\"targetFormat\":\"iso\"}", FileeJsonContext.Default.Preset)!;
        Assert.Equal(IsoFileSystem.Joliet, old.Archive.IsoFileSystem);
        old.Archive.IsoFileSystem = IsoFileSystem.Udf201;
        var saved = JsonSerializer.Serialize(old, FileeJsonContext.Default.Preset);
        var reloaded = JsonSerializer.Deserialize(saved, FileeJsonContext.Default.Preset)!;
        Assert.Equal(IsoFileSystem.Udf201, reloaded.Archive.IsoFileSystem);
    }
}
