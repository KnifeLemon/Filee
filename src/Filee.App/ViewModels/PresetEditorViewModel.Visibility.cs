// PresetEditorViewModel: which options the editor shows for the chosen target (the Show* properties),
// and whether the input is valid.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Ocr;

namespace Filee.App.ViewModels;

public sealed partial class PresetEditorViewModel
{
    private string TargetId => Target.Value;
    private FormatCategory? TargetCategory => FormatRegistry.FindById(TargetId)?.Category;
    private bool TargetIsImage => TargetId == BuiltInData.SameAsSource || TargetCategory == FormatCategory.Image;
    /// <summary>A video target, or "Cut a clip", whose exact cuts are encoded with the video options.</summary>
    private bool TargetIsVideo => TargetCategory == FormatCategory.Video || _original.IsClipTool;
    private bool TargetIsAudio => TargetCategory == FormatCategory.Audio;
    private bool TargetIsLossyMedia => TargetIsVideo
        || (TargetIsAudio && !LosslessAudioTargets.Contains(TargetId) && MediaCodecs.Effective(TargetId, _audioCodecValue) != AudioCodec.Alac);

    /// <summary>A tool (Crop) keeps the source as it is: none of the image options apply.</summary>
    public bool ShowImage => !IsTool && TargetIsImage && !IsLivePhoto;
    /// <summary>JPG and HEIC: a video can become a Live / Motion Photo instead of one still frame.</summary>
    public bool ShowLivePhotoChoice => !IsTool && LivePhotoKinds.Count > 1;
    /// <summary>A Live / Motion Photo is chosen and the target can be written as it: its clip options are shown.</summary>
    public bool IsLivePhoto => ShowLivePhotoChoice && _livePhotoValue != LivePhotoKind.None
        && MediaOptions.LivePhotoTargets(_livePhotoValue).Contains(TargetId);
    /// <summary>What the chosen kind writes and where it is expected to work.</summary>
    public string LivePhotoHint => _loc[_livePhotoValue == LivePhotoKind.Android ? "presets.live_photo_hint.android" : "presets.live_photo_hint.apple"];
    /// <summary>Images and PDFs (pictures placed on pages) can be turned and flipped.</summary>
    private bool CanTurn => TargetIsImage || TargetId == "pdf";
    /// <summary>Rotate and flip for a PDF target, which has no image card to show them in.</summary>
    public bool ShowTurnForPdf => TargetId == "pdf";
    public bool ShowQuality => !IsTool && (TargetId == BuiltInData.SameAsSource || LossyTargets.Contains(TargetId));
    public bool ShowTiff => TargetId is "tiff" or BuiltInData.SameAsSource;
    public bool ShowWebp => TargetId is "webp" or BuiltInData.SameAsSource;
    public bool ShowMultiPageTiff => TargetId is "tiff";
    /// <summary>FITS (astronomy) → TIFF: linear or stretched, at the file's own depth.</summary>
    public bool ShowFitsTiff => !IsTool && TargetId is "tiff";
    /// <summary>FITS → any other picture is stretched; a colour camera's raw frame can be debayered or kept mono.</summary>
    public bool ShowFitsDebayer => !IsTool && TargetCategory == FormatCategory.Image && TargetId != "tiff";
    public bool ShowIco => TargetId == "ico";
    /// <summary>PDF options (also used when a PDF source becomes images) mean nothing for media, archive, font, subtitle, data, e-mail, 3D and geospatial targets.</summary>
    public bool ShowPdf => !IsTool && !IsLivePhoto && TargetCategory is not (FormatCategory.Video or FormatCategory.Audio or FormatCategory.Archive or FormatCategory.Font
        or FormatCategory.Subtitle or FormatCategory.Data or FormatCategory.Model3D or FormatCategory.Geo) && TargetId != "eml";
    /// <summary>"Save as ZIP" is offered for geospatial targets, whose results are several files (a Shapefile) or travel zipped.</summary>
    public bool ShowZipResult => TargetCategory == FormatCategory.Geo;
    public bool ShowPdfBuild => TargetId == "pdf";

    /// <summary>Page size and margin of a PDF built from pictures; not with text recognition (<see cref="Recognizes"/>).</summary>
    public bool ShowPageSize => ShowPdfBuild && !Recognizes;
    public bool ShowPdfSplit => TargetId == "pdf" && !Recognizes;

    /// <summary>Compression is for PDF → PDF; Ghostscript and metadata only matter while it is on.</summary>
    public bool ShowPdfCompression => TargetId == "pdf" && !Recognizes;

    /// <summary>XLSX: what a PDF becomes (its ruled tables, PdfTablesConverter).</summary>
    public bool ShowPdfTablesNote => TargetId == "xlsx";
    public bool ShowPdfCompressionOptions => ShowPdfCompression && CompressionLevel.Value != PdfCompression.None;

    /// <summary>Text from scans goes into the formats Filee writes from a PDF's text itself (PdfOptions.OcrTargets).</summary>
    public bool ShowOcr => !IsTool && PdfOptions.OcrTargets.Contains(TargetId);

    /// <summary>PDF → a single-image format: the pages can be stacked into one tall image (TIFF, GIF, APNG and MNG keep pages).</summary>
    public bool ShowPdfCombine => TargetCategory == FormatCategory.Image
        && TargetId is not ("tiff" or "gif" or "apng" or "mng" or "ico" or "cur" or "icns" or "svg" or "pdf");
    public bool ShowDocument => TargetId == "pdf";
    /// <summary>Markdown can't hold pictures: they go into a folder next to it, or are left out.</summary>
    public bool ShowLeaveOutPictures => TargetId == "md";
    public bool ShowMedia => ShowMediaQuality || ShowMaxHeight || ShowRemoveAudio || ShowAudioChannels || ShowVideoCodec || ShowAudioCodec;
    /// <summary>Mono / stereo for every audio target but AMR, which is mono by definition.</summary>
    public bool ShowAudioChannels => TargetIsAudio && TargetId != "amr";
    /// <summary>Sample rate and sample format: the PCM of WAV and W64 takes any.</summary>
    public bool ShowWav => TargetId is "wav" or "w64";
    /// <summary>MP4, MKV, MOV and WebM can be written with HEVC or AV1 besides their usual codec.</summary>
    public bool ShowVideoCodec => TargetIsVideo && VideoCodecs.Count > 1;
    /// <summary>Hardware encoding applies to H.264, HEVC and AV1 only (not VP9, MPEG-2, …).</summary>
    public bool ShowHardwareEncoding => TargetIsVideo && MediaCodecs.TakesHardwareEncoding(TargetId, _videoCodecValue);
    /// <summary>M4A / M4B can hold Apple Lossless besides AAC.</summary>
    public bool ShowAudioCodec => AudioCodecs.Count > 1;
    public bool ShowMediaQuality => TargetIsLossyMedia;
    public bool ShowMaxHeight => TargetIsVideo;
    public bool ShowAudioBitrate => TargetIsLossyMedia;
    public bool ShowRemoveAudio => TargetIsVideo;
    public bool ShowArchive => TargetCategory == FormatCategory.Archive;
    public bool ShowArchiveOptions => ShowArchive && TargetId != FormatRegistry.Folder;
    /// <summary>"Extract" has nothing to set; a line says what it does instead.</summary>
    public bool ShowExtractNote => TargetId == FormatRegistry.Folder;
    /// <summary>"Save every frame" of a video: PNG and JPG (format ids, <see cref="MediaOptions.FrameTargets"/>).</summary>
    public bool ShowSaveEveryFrame => !IsTool && !IsLivePhoto && MediaOptions.FrameTargets.Contains(TargetId);
    /// <summary>The slideshow options, for a preset that makes a video of pictures (MP4).</summary>
    public bool ShowSlideshow => _isSlideshow && MediaOptions.JoinTargets.Contains(TargetId) && TargetIsVideo;
    /// <summary>The video of a Live / Motion Photo: MP4 and MOV (<see cref="MediaOptions.MotionPhotoTargets"/>).</summary>
    public bool ShowExtractMotionPhoto => !IsTool && MediaOptions.MotionPhotoTargets.Contains(TargetId);
    /// <summary>
    /// Playback speed: for the formats an animation stays animated in (an animated GIF, WebP, APNG or MNG, or a video
    /// made into a GIF or APNG). Other targets get one frame.
    /// </summary>
    public bool ShowAnimationSpeed => TargetId is "gif" or "webp" or "apng" or "mng" or BuiltInData.SameAsSource;
    /// <summary>
    /// Tracing options: raster images reach SVG (and DXF / DWG through it) only by tracing. For other sources (a
    /// drawing, a CAD file) the options are ignored.
    /// </summary>
    public bool ShowTrace => TargetId is "svg" or "svgz" or "dxf" or "dwg";

    /// <summary>The AutoCAD version, for presets that write DWG or DXF.</summary>
    public bool ShowCad => !IsTool && TargetId is "dxf" or "dwg";
    public bool ShowTraceColors => TraceMode.Value == TracingMode.Color;
    public bool ShowPercent => Resize.Value == ResizeMode.Percent;
    public bool ShowLongEdge => Resize.Value == ResizeMode.LongEdge;
    public bool ShowWidth => Resize.Value == ResizeMode.Width;
    public bool ShowMargin => PageSize.Value != PdfPageSize.FitImage;
    public bool ShowSubfolder => Location.Value == OutputLocation.Subfolder;
    public bool ShowCustomFolder => Location.Value == OutputLocation.CustomFolder;

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? _loc["presets.name_required"] : null;
    public bool IsValid => NameError is null;
}
