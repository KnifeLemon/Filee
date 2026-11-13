using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Services.Capture;
using Filee.App.Services.Connect;

namespace Filee.App.Views.Capture;

public partial class MobileMirrorWindow : Window
{
    private readonly MobileMirrorService? _service;

    public MobileMirrorWindow()
    {
        InitializeComponent();
        LoadQrAndNetworkInfo();
    }

    public MobileMirrorWindow(MobileMirrorService service) : this()
    {
        _service = service;
        _service.FrameReceived += OnFrameReceived;
        _service.StateChanged += OnStateChanged;

        if (_service.LastJpegFrame != null)
        {
            OnFrameReceived(_service.LastJpegFrame);
        }
    }

    private void LoadQrAndNetworkInfo()
    {
        try
        {
            var lanIp = FileeConnectService.GetLocalLanIp().ToString();
            var port = 3000;
            var url = $"http://{lanIp}:{port}";
            LanUrlText.Text = url;

            using var generator = new QRCoder.QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new QRCoder.PngByteQRCode(data);
            var bytes = qrCode.GetGraphic(12);
            using var ms = new MemoryStream(bytes);
            QrImage.Source = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to generate QR for mirror: {ex.Message}");
        }
    }

    private void OnPlatformChanged(object? sender, RoutedEventArgs e)
    {
        var isIos = IosRadio?.IsChecked == true;
        if (IosStepsPanel != null)
            IosStepsPanel.IsVisible = isIos;
        if (AndroidStepsPanel != null)
            AndroidStepsPanel.IsVisible = !isIos;
    }

    private void OnFrameReceived(byte[] jpegBytes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var stream = new MemoryStream(jpegBytes);
                var bmp = new Bitmap(stream);
                FrameImage.Source = bmp;
                WaitingPanel.IsVisible = false;
            }
            catch
            {
            }
        });
    }

    private void OnStateChanged(bool isStreaming)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WaitingPanel.IsVisible = !isStreaming;
            if (!isStreaming)
            {
                FrameImage.Source = null;
            }
        });
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnDockRightClick(object? sender, RoutedEventArgs e)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var bounds = screen.WorkingArea;
            Position = new Avalonia.PixelPoint(bounds.Right - (int)Width - 20, bounds.Y + (bounds.Height - (int)Height) / 2);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_service != null)
        {
            _service.FrameReceived -= OnFrameReceived;
            _service.StateChanged -= OnStateChanged;
        }
        base.OnClosing(e);
    }
}
