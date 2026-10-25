// PresetEditorViewModel: Apply() writes the edited clone back into the original preset.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Ocr;

namespace Filee.App.ViewModels;

public sealed partial class PresetEditorViewModel
{
    /// <summary>Writes the edits into the original preset. Returns false if the input is invalid.</summary>
    public bool Apply()
    {
        if (!IsValid)
            return false;

        // Keep a built-in preset's translatable name unless the user actually renamed it.
        if (_original.NameKey is null || Name.Trim() != _loc.DisplayName(_original))
            _original.Name = Name.Trim();

        _original.TargetFormat = TargetId;
        var image = _original.Image;
        image.Quality = Math.Clamp(Quality, 1, 100);
        image.Resize = Resize.Value;
        // Only images and PDFs are turned; for any other target a rotation left over from an earlier target would make
        // every file fail ("can only turn images") with nothing on screen to turn it off.
        image.Rotation = CanTurn ? Rotation.Value : ImageRotation.None;
        image.Flip = CanTurn ? Flip.Value : ImageFlip.None;
        image.ResizePercent = Math.Clamp(ResizePercent, 1, 1000);
        image.LongEdge = Math.Clamp(LongEdge, 16, 20000);
        image.Width = Math.Clamp(Width, 16, 20000);
        image.Dpi = SetDpi ? Math.Clamp(Dpi, 36, 2400) : null;
        image.KeepMetadata = KeepMetadata;
        image.KeepColorProfile = KeepColorProfile;
        image.Grayscale = Grayscale;
        image.RemoveBackground = RemoveBackground;
        image.BackgroundTolerance = Math.Clamp(BackgroundTolerance, 0, 100);
        image.KeepEnclosedBackground = KeepEnclosedBackground;
        image.Background = string.IsNullOrWhiteSpace(Background) ? "#FFFFFF" : Background.Trim();
        image.TiffCompression = TiffCompression.Value;
        image.WebpLossless = WebpLossless;
        image.MultiPageTiff = MultiPageTiff;
        image.FitsTiff = FitsTiffMode.Value;
        image.FitsDebayer = FitsDebayer;
        image.IcoSizes = IcoSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v is > 0 and <= 256)
            .Distinct()
            .ToList();
        if (image.IcoSizes.Count == 0)
            image.IcoSizes = [256, 48, 32, 16];
        // Only where the option is shown: a speed chosen for GIF and left behind by a JPG target saves nothing.
        image.AnimationSpeed = ShowAnimationSpeed ? AnimationSpeed.Value : 1;

        var pdf = _original.Pdf;
        pdf.MergeIntoSingle = Merge;
        pdf.PageSize = PageSize.Value;
        pdf.MarginMm = Math.Clamp(MarginMm, 0, 100);
        pdf.RenderDpi = Math.Clamp(RenderDpi, 36, 1200);
        pdf.PageRange = PageRange.Trim();
        pdf.SplitPages = SplitPages;
        pdf.CombinePages = CombinePages;
        pdf.TrimPageMargins = TrimPageMargins;
        pdf.SplitTallImage = SplitTallImage;
        pdf.Compression = CompressionLevel.Value;
        pdf.CompressWithGhostscript = CompressWithGhostscript;
        // Only for a PDF target, where they are shown: a password left behind by it must not lock nothing.
        pdf.Watermark = ShowPdfBuild ? Watermark.Trim() : "";
        pdf.PageNumbers = ShowPdfBuild && PageNumbers;
        // PDF/A can't be encrypted, so the box is off while it is ticked.
        pdf.Password = ShowPdfBuild && !PdfA ? PdfPassword : "";
        pdf.PasswordLeftOut = false;
        // Only where the option is shown, as with SaveEveryFrame below.
        pdf.Ocr = Recognizes;
        pdf.OcrLanguage = OcrLanguage.Value;
        if (pdf.Ocr)
        {
            // Hidden while text is recognized, so nothing is kept behind it that the PDF wouldn't do (Recognizes).
            pdf.SplitPages = false;
            pdf.Compression = PdfCompression.None;
            pdf.CompressWithGhostscript = false;
            pdf.PageSize = PdfPageSize.FitImage;
            pdf.MarginMm = 0;
        }
        _original.Document.PdfA = PdfA;
        // Only where the option is shown, as with SaveEveryFrame below.
        _original.Document.LeaveOutPictures = ShowLeaveOutPictures && LeaveOutPictures;

        var media = _original.Media;
        media.Quality = MediaQuality.Value;
        media.MaxHeight = MaxHeight.Value;
        media.AudioBitrateKbps = AudioBitrate.Value;
        media.RemoveAudio = RemoveAudio;
        media.AudioChannels = AudioChannels.Value;
        media.AudioSampleRate = SampleRate.Value;
        media.WavFormat = WavFormat.Value;
        // A Live Photo's clip is a MOV (or an MP4 in a Motion Photo): H.264 or HEVC, whatever the photo's format.
        media.VideoCodec = IsLivePhoto
            ? (_videoCodecValue == VideoCodec.Hevc ? VideoCodec.Hevc : VideoCodec.Default)
            : MediaCodecs.Effective(TargetId, _videoCodecValue);
        media.AudioCodec = MediaCodecs.Effective(TargetId, _audioCodecValue);
        media.HardwareEncoding = HardwareEncoding;
        // Only where the option is shown: a box ticked for PNG and left behind by another target saves nothing.
        media.SaveEveryFrame = ShowSaveEveryFrame && SaveEveryFrame;
        media.FrameInterval = media.SaveEveryFrame ? Math.Clamp(FrameInterval, 1, 1000) : 1;
        media.ExtractMotionPhoto = ShowExtractMotionPhoto && ExtractMotionPhoto;
        if (ShowSlideshow)
        {
            media.SlideSeconds = double.IsFinite(SlideSeconds) ? Math.Clamp(SlideSeconds, MediaOptions.MinSlideSeconds, MediaOptions.MaxSlideSeconds) : 3;
            media.Crossfade = Crossfade;
        }
        media.LivePhoto = IsLivePhoto ? _livePhotoValue : LivePhotoKind.None;
        static double Seconds(double value, double fallback, double min, double max) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        media.LivePhotoStart = Seconds(LivePhotoStart, 0, 0, 24 * 3600);
        media.LivePhotoLength = Seconds(LivePhotoLength, 3, MediaOptions.MinLivePhotoLength, MediaOptions.MaxLivePhotoLength);
        media.LivePhotoCover = Seconds(LivePhotoCover, 1.5, 0, MediaOptions.MaxLivePhotoLength);
        _original.Archive.Level = ArchiveLevel.Value;
        _original.Archive.CombineIntoOne = CombineIntoOne;
        _original.Trace.Mode = TraceMode.Value;
        _original.Trace.Colors = Math.Clamp(TraceColors, TracingOptions.MinColors, TracingOptions.MaxColors);
        _original.Trace.Detail = Math.Clamp(TraceDetail, 0, 100);
        // Only where it is shown: a version chosen for DWG and left behind by a PNG target is not kept.
        _original.Cad.Version = ShowCad ? CadVersion.Value : default;

        var output = _original.Output;
        output.Location = Location.Value;
        output.SubfolderName = string.IsNullOrWhiteSpace(SubfolderName) ? "converted" : SubfolderName.Trim();
        // Only with a subfolder, where it is offered: a box ticked there must not stay on hidden behind another location.
        output.SubfolderOnlyForSeveralFiles = ShowSubfolder && SubfolderOnlyForSeveralFiles;
        output.CustomFolder = CustomFolder.Trim();
        output.FileNamePattern = string.IsNullOrWhiteSpace(Pattern) ? "{name}" : Pattern.Trim();
        output.Conflict = Conflict.Value;
        output.KeepDates = KeepDates;
        // Only for geospatial targets, where it is offered: a box ticked for Shapefile must not zip a later PNG.
        output.ZipResult = ShowZipResult && ZipResult;
        output.ReplaceOriginal = ReplaceOriginal;
        return true;
    }
}
