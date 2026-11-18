using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed partial class AskIsoLabelViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly IsoFileSystem _filesystem;

    public AskIsoLabelViewModel(string path, Preset preset, ILocalizer loc, int index = 0, int total = 1)
    {
        _loc = loc;
        _filesystem = preset.Archive.IsoFileSystem;
        FileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        Position = total > 1 ? loc.Format("pages.file_of", index + 1, total) : "";
        var name = Directory.Exists(path) ? FileName : FormatRegistry.NameWithoutExtension(path);
        _label = string.IsNullOrEmpty(preset.Archive.IsoVolumeLabel)
            ? IsoVolumeLabels.FromName(name, _filesystem) : preset.Archive.IsoVolumeLabel;
    }

    public string FileName { get; }
    public string Position { get; }
    public string FileSystem => _filesystem == IsoFileSystem.Joliet ? "ISO 9660 + Joliet" : "UDF 2.01";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Error), nameof(IsValid), nameof(Counter))] private string _label;
    public string Counter => IsoLabelText.Counter(Label, _filesystem);
    public string Hint => IsoLabelText.Hint(_filesystem, _loc);
    public string LengthError => _loc.Format("error.iso_label_length", _filesystem == IsoFileSystem.Joliet ? "Joliet" : "UDF 2.01");
    public string CharactersError => _loc["error.iso_label_characters"];
    public string? Error => IsoLabelText.Error(Label, _filesystem, _loc);
    public bool IsValid => Error is null;
}
