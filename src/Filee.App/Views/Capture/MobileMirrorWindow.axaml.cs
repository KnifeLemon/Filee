using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Services.Capture;

namespace Filee.App.Views.Capture;

public partial class MobileMirrorWindow : Window
{
    private readonly MobileMirrorService? _service;
    private readonly AndroidMirrorEngine _androidEngine;
    private readonly AirPlayMirrorEngine? _airPlayEngine;
    private string? _selectedUsbSerial;

    public MobileMirrorWindow()
    {
        InitializeComponent();
        _androidEngine = new AndroidMirrorEngine();
    }

    public MobileMirrorWindow(MobileMirrorService service, AirPlayMirrorEngine? airPlayEngine = null) : this()
    {
        _service = service;
        _airPlayEngine = airPlayEngine ?? new AirPlayMirrorEngine(service);

        _service.FrameReceived += OnFrameReceived;
        _service.StateChanged += OnStateChanged;

        if (_service.LastJpegFrame != null)
        {
            OnFrameReceived(_service.LastJpegFrame);
        }

        // Start AirPlay listener by default
        _airPlayEngine.Start();
        if (AirPlayDeviceNameText != null)
        {
            AirPlayDeviceNameText.Text = _airPlayEngine.ServerName;
        }

        // Scan for USB Android device on start
        _ = ScanUsbDevicesAsync();
    }

    public MobileMirrorWindow(MobileMirrorService service, Filee.App.Services.Connect.FileeConnectService? connectService)
        : this(service)
    {
    }

    private void OnPlatformChanged(object? sender, RoutedEventArgs e)
    {
        var isIos = IosRadio?.IsChecked == true;
        if (IosPanel != null)
            IosPanel.IsVisible = isIos;
        if (AndroidPanel != null)
            AndroidPanel.IsVisible = !isIos;
    }

    private async void OnScanUsbClick(object? sender, RoutedEventArgs e)
    {
        await ScanUsbDevicesAsync();
    }

    private async Task ScanUsbDevicesAsync()
    {
        if (UsbDeviceStatusText == null || StartUsbMirrorBtn == null) return;

        UsbDeviceStatusText.Text = "Scanning USB devices...";
        var devices = await _androidEngine.GetConnectedDevicesAsync();

        if (devices.Count > 0)
        {
            var dev = devices[0];
            _selectedUsbSerial = dev.Serial;
            UsbDeviceStatusText.Text = $"Found: {dev.DisplayName}";
            StartUsbMirrorBtn.IsEnabled = true;
        }
        else
        {
            _selectedUsbSerial = null;
            UsbDeviceStatusText.Text = "No USB phone detected. Turn on USB Debugging.";
            StartUsbMirrorBtn.IsEnabled = false;
        }
    }

    private void OnStartUsbMirrorClick(object? sender, RoutedEventArgs e)
    {
        if (_service != null)
        {
            _androidEngine.StartScreenMirror(_selectedUsbSerial, _service);
        }
    }

    private async void OnPairWirelessClick(object? sender, RoutedEventArgs e)
    {
        var ipPort = WirelessIpBox?.Text?.Trim() ?? "";
        var code = WirelessCodeBox?.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(ipPort) || string.IsNullOrWhiteSpace(code))
        {
            if (WirelessStatusText != null)
                WirelessStatusText.Text = "Please enter both IP:Port and Pairing Code.";
            return;
        }

        if (WirelessStatusText != null)
            WirelessStatusText.Text = "Pairing with phone...";

        var (success, msg) = await _androidEngine.PairWirelessDeviceAsync(ipPort, code);
        if (success)
        {
            if (WirelessStatusText != null)
                WirelessStatusText.Text = "Paired! Connecting wireless screen stream...";

            // Connect
            await _androidEngine.ConnectWirelessDeviceAsync(ipPort);
            if (_service != null)
            {
                _androidEngine.StartScreenMirror(ipPort, _service);
            }
        }
        else
        {
            if (WirelessStatusText != null)
                WirelessStatusText.Text = $"Pairing failed: {msg}";
        }
    }

    private void OnOpenWindowsCastClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:projecting-to-this-pc") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        _androidEngine.StopScreenMirror();
        _service?.OnDisconnected();
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
                DisconnectBtn.IsVisible = true;
            }
            catch { }
        });
    }

    private void OnStateChanged(bool isStreaming)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WaitingPanel.IsVisible = !isStreaming;
            DisconnectBtn.IsVisible = isStreaming;
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
        var screen = Screens.Primary;
        if (screen != null)
        {
            Position = new Avalonia.PixelPoint(
                screen.WorkingArea.Right - (int)Width - 20,
                screen.WorkingArea.Y + (screen.WorkingArea.Height - (int)Height) / 2);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _androidEngine.StopScreenMirror();
        _airPlayEngine?.Stop();
        base.OnClosing(e);
    }
}
