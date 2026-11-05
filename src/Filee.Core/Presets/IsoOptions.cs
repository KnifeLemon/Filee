// Options for folder ISO images, separate from archive compression.

using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>Filesystems supported by the managed ISO writer.</summary>
public enum IsoStandard
{
    /// <summary>ISO 9660 with Unicode names through Joliet; the default for existing presets.</summary>
    Iso9660Joliet,
    /// <summary>ISO 9660 with legacy uppercase ASCII names.</summary>
    Iso9660,
}

/// <summary>Options for ISO creation from folders.</summary>
public sealed class IsoOptions
{
    /// <summary>Filesystem to create; missing or unknown values use Joliet.</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<IsoStandard>))]
    public IsoStandard Standard { get; set; } = IsoStandard.Iso9660Joliet;

    /// <summary>Copies the options so editing a preset leaves its original unchanged.</summary>
    public IsoOptions Clone() => (IsoOptions)MemberwiseClone();
}
