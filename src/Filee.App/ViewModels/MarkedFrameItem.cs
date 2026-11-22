// A marked frame in Cut a clip's list (#1205 step 3): its picture as marked, its time and number, and its note.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;

namespace Filee.App.ViewModels;

public sealed partial class MarkedFrameItem(MarkedCopy copy, string label, string note, string remove) : ObservableObject
{
    public MarkedCopy Copy { get; } = copy;

    /// <summary>"2:04.700 · frame 3,741".</summary>
    public string Label { get; } = label;

    /// <summary>The note typed with it, or the words for none.</summary>
    public string Note { get; } = note;

    /// <summary>A screen reader's name for the row: its time and number, and its note.</summary>
    public string SpokenName => $"{Label}, {Note}";

    /// <summary>A screen reader's name for its ✕: what it does, and to which frame.</summary>
    public string RemoveSpokenName { get; } = $"{remove}: {label}";

    /// <summary>The marked copy, small; null until read.</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>Its picture is being read.</summary>
    internal bool Reading { get; set; }
}
