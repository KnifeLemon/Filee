using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services.Connect;
using Filee.Core.Localization;
using Filee.Core.Platform;

namespace Filee.App.ViewModels;

public sealed partial class ConnectMessageItemViewModel : ObservableObject
{
    private readonly Action<string> _copyAction;
    private readonly ILocalizer _loc;

    public string Id { get; }
    public string Text { get; }
    public string From { get; }
    public long Timestamp { get; }
    public string TimeText { get; }
    public bool IsFromPc { get; }
    public string SenderLabel { get; }

    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private string _copyButtonText;

    public ConnectMessageItemViewModel(ConnectMessage message, Action<string> copyAction, string? mobileDeviceName, ILocalizer loc)
    {
        _copyAction = copyAction;
        _loc = loc;
        Id = message.Id;
        Text = message.Text;
        From = message.From;
        Timestamp = message.Timestamp;
        IsFromPc = message.From.Equals("pc", StringComparison.OrdinalIgnoreCase);
        SenderLabel = IsFromPc ? loc["connect.sender_pc"] : (mobileDeviceName ?? loc["connect.sender_phone"]);
        TimeText = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).LocalDateTime.ToString("t");
        _copyButtonText = loc["connect.copy"];
    }

    [RelayCommand]
    private void Copy()
    {
        _copyAction(Text);
        IsCopied = true;
        CopyButtonText = _loc["connect.copied"];
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsCopied = false;
                CopyButtonText = _loc["connect.copy"];
            });
        });
    }
}

public sealed partial class ConnectFileItemViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public long Size { get; }
    public string SizeText { get; }
    public string Sender { get; }
    public string SenderText { get; }
    public string MimeType { get; }
    public bool IsFolder { get; }
    public string IconText { get; }
    public bool CanPreview { get; }

    public IRelayCommand OpenOrFolderCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand PreviewCommand { get; }

    public ConnectFileItemViewModel(
        SharedFileMetadata meta,
        Action<ConnectFileItemViewModel> openFolder,
        Action<ConnectFileItemViewModel> delete,
        Action<ConnectFileItemViewModel> preview,
        ILocalizer loc)
    {
        Id = meta.Id;
        Name = meta.Name;
        RelativePath = meta.RelativePath;
        Size = meta.Size;
        SizeText = FormatSize(meta.Size);
        Sender = meta.Sender;
        SenderText = meta.Sender.Equals("mobile", StringComparison.OrdinalIgnoreCase) ? loc["connect.sender_phone"] : loc["connect.sender_pc"];
        MimeType = meta.MimeType;
        IsFolder = false;

        IconText = meta.Icon switch
        {
            "image" => "🖼️",
            "pdf" => "📄",
            "audio" => "🎵",
            "video" => "🎬",
            "text" => "📝",
            _ => "📎"
        };
        CanPreview = FileExecutionSafety.IsSafeToOpenDirectly(meta.Name);

        OpenOrFolderCommand = new RelayCommand(() => preview(this));
        DeleteCommand = new RelayCommand(() => delete(this));
        PreviewCommand = new RelayCommand(() => preview(this));
    }

    public ConnectFileItemViewModel(
        string folderName,
        string folderPath,
        int itemCount,
        Action<ConnectFileItemViewModel> openFolder,
        Action<ConnectFileItemViewModel> delete,
        ILocalizer loc)
    {
        Id = folderPath;
        Name = folderName;
        RelativePath = folderPath;
        Size = 0;
        SizeText = loc.Format("connect.items_count", itemCount);
        Sender = "";
        SenderText = "";
        MimeType = "inode/directory";
        IsFolder = true;
        IconText = "📁";
        CanPreview = false;

        OpenOrFolderCommand = new RelayCommand(() => openFolder(this));
        DeleteCommand = new RelayCommand(() => delete(this));
        PreviewCommand = new RelayCommand(() => { });
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }
}

public sealed partial class FileeConnectViewModel : ObservableObject, IDisposable
{
    private readonly FileeConnectService _connect;
    private readonly IPlatformServices _platform;
    private readonly ILocalizer _loc;
    private IClipboard? _clipboard;

    public void SetClipboard(IClipboard? clipboard) => _clipboard = clipboard;
    public IPlatformServices Platform => _platform;
    public ILocalizer Localizer => _loc;

    public FileeConnectService ConnectService => _connect;
    public Bitmap? QrBitmap => _connect.QrBitmap;
    public string? MobileUrl => _connect.MobileUrl;
    public string? ManualUrl => _connect.ManualUrl;
    public Avalonia.Media.IBrush StatusColorBrush => IsMobileConnected
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22C55E"))
        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EAB308"));

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isMobileConnected;
    [ObservableProperty] private bool _isQrOverlayVisible;
    [ObservableProperty] private string _connectedDeviceTitle = "";
    [ObservableProperty] private string _storageUsageText = "0 B / 2 GB";
    [ObservableProperty] private string _itemCountText = "0 items";
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private bool _isInSubfolder;

    [ObservableProperty] private string _pinCodeText = "";
    [ObservableProperty] private string _pinRefreshHintText = "";
    [ObservableProperty] private string _wifiLabelText = "";
    [ObservableProperty] private string _firewallHintText = "";

    public ObservableCollection<ConnectMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<ConnectFileItemViewModel> Files { get; } = [];
    public ObservableCollection<PairedDevice> PairedDevices { get; } = [];

    public event Action? RequestClose;

    public FileeConnectViewModel(FileeConnectService connect, IPlatformServices platform, ILocalizer loc, IClipboard? clipboard = null)
    {
        _connect = connect;
        _platform = platform;
        _loc = loc;
        _clipboard = clipboard;

        _connect.MobileConnected += OnMobileConnected;
        _connect.MobileDisconnected += OnMobileDisconnected;
        _connect.MessageReceived += OnMessageReceived;
        _connect.FilesChanged += OnFilesChanged;
        _connect.SessionEnded += OnSessionEnded;
        _connect.PinRefreshed += UpdatePinDisplay;
        _connect.PinTicked += OnPinTicked;
        _connect.PairedDevicesChanged += UpdatePairedDevices;

        _connect.SetStringLocalizer(k => _loc[k]);

        IsMobileConnected = _connect.IsMobileConnected;
        UpdateDeviceTitle(_connect.ConnectedDeviceName);
        UpdatePinDisplay();
        UpdatePairedDevices();

        WifiLabelText = _loc.Format("connect.wifi_label", GetNetworkLabel());
        FirewallHintText = OperatingSystem.IsWindows()
            ? _loc["connect.firewall_hint"]
            : _loc["connect.firewall_hint_generic"];

        foreach (var msg in _connect.MessageHistory)
        {
            Messages.Add(new ConnectMessageItemViewModel(msg, CopyToClipboard, _connect.ConnectedDeviceName, _loc));
        }

        RefreshFiles();
    }

    private void OnPinTicked()
    {
        PinRefreshHintText = _loc.Format("connect.pin_refresh_hint", _connect.PinSecondsRemaining);
    }

    private void UpdatePinDisplay()
    {
        var pin = _connect.CurrentPin;
        PinCodeText = pin.Length == 6 ? $"{pin[..3]} {pin[3..]}" : pin;
        PinRefreshHintText = _loc.Format("connect.pin_refresh_hint", _connect.PinSecondsRemaining);
    }

    private void UpdatePairedDevices()
    {
        PairedDevices.Clear();
        foreach (var d in _connect.PairedDevices)
        {
            PairedDevices.Add(d);
        }
    }

    private void UpdateDeviceTitle(string? deviceName)
    {
        ConnectedDeviceTitle = IsMobileConnected
            ? _loc.Format("connect.connected_badge", deviceName ?? _loc["connect.sender_phone"])
            : _loc["connect.waiting_device"];
    }

    private void OnMobileConnected(string deviceName)
    {
        IsMobileConnected = true;
        IsQrOverlayVisible = false;
        UpdateDeviceTitle(deviceName);
        UpdatePairedDevices();
        OnPropertyChanged(nameof(StatusColorBrush));
    }

    private void OnMobileDisconnected()
    {
        IsMobileConnected = _connect.IsMobileConnected;
        UpdateDeviceTitle(_connect.ConnectedDeviceName);
        UpdatePairedDevices();
        OnPropertyChanged(nameof(StatusColorBrush));
    }

    private void OnMessageReceived(ConnectMessage msg)
    {
        Messages.Add(new ConnectMessageItemViewModel(msg, CopyToClipboard, _connect.ConnectedDeviceName, _loc));
    }

    private void OnFilesChanged()
    {
        RefreshFiles();
    }

    private void OnSessionEnded(string reason)
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Send()
    {
        var text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        _connect.SendTextMessage(text);
        InputText = "";
    }

    [RelayCommand]
    private async Task PasteClipboardAsync()
    {
        try
        {
            string? text = null;
            var cb = GetClipboard();
            if (cb is not null)
            {
                text = await cb.TryGetTextAsync();
            }

            if (!string.IsNullOrEmpty(text))
            {
                InputText = string.IsNullOrEmpty(InputText) ? text : $"{InputText}\n{text}";
            }
        }
        catch { }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var cb = GetClipboard();
            if (cb is not null)
            {
                _ = cb.SetTextAsync(text);
            }
        }
        catch { }
    }

    private IClipboard? GetClipboard()
    {
        return _clipboard ?? (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
    }

    [RelayCommand]
    private void RefreshFiles()
    {
        Files.Clear();
        var allFiles = _connect.FolderManager.GetFiles();
        var currentPrefix = string.IsNullOrEmpty(CurrentPath) ? "" : CurrentPath.TrimEnd('/') + "/";

        var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var directFiles = new List<SharedFileMetadata>();

        foreach (var file in allFiles)
        {
            var rel = file.RelativePath;
            if (!string.IsNullOrEmpty(currentPrefix))
            {
                if (!rel.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                rel = rel[currentPrefix.Length..];
            }

            var slashIdx = rel.IndexOf('/');
            if (slashIdx >= 0)
            {
                var topFolder = rel[..slashIdx];
                folderCounts[topFolder] = folderCounts.GetValueOrDefault(topFolder) + 1;
            }
            else
            {
                directFiles.Add(file);
            }
        }

        foreach (var (folder, count) in folderCounts.OrderBy(k => k.Key))
        {
            var folderRelPath = string.IsNullOrEmpty(currentPrefix) ? folder : $"{currentPrefix}{folder}";
            Files.Add(new ConnectFileItemViewModel(folder, folderRelPath, count, OpenSubfolder, DeleteItem, _loc));
        }

        foreach (var file in directFiles.OrderBy(f => f.Name))
        {
            Files.Add(new ConnectFileItemViewModel(file, OpenSubfolder, DeleteItem, PreviewFile, _loc));
        }

        var usage = _connect.FolderManager.GetUsage();
        StorageUsageText = $"{ConnectFileItemViewModel.FormatSize(usage.Used)} / {ConnectFileItemViewModel.FormatSize(usage.Limit)}";
        ItemCountText = _loc.Format("connect.items_count", Files.Count);
    }

    private void OpenSubfolder(ConnectFileItemViewModel item)
    {
        if (item.IsFolder)
        {
            CurrentPath = item.RelativePath;
            IsInSubfolder = true;
            RefreshFiles();
        }
    }

    [RelayCommand]
    private void NavigateRoot()
    {
        CurrentPath = "";
        IsInSubfolder = false;
        RefreshFiles();
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(CurrentPath))
            return;

        var lastSlash = CurrentPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            CurrentPath = CurrentPath[..lastSlash];
            IsInSubfolder = true;
        }
        else
        {
            CurrentPath = "";
            IsInSubfolder = false;
        }
        RefreshFiles();
    }

    private void DeleteItem(ConnectFileItemViewModel item)
    {
        if (item.IsFolder)
        {
            _connect.FolderManager.DeleteFolder(item.RelativePath);
        }
        else
        {
            _connect.FolderManager.DeleteFile(item.Id);
        }
        RefreshFiles();
        _ = _connect.BroadcastAsync("{\"type\":\"files-changed\"}");
    }

    private void PreviewFile(ConnectFileItemViewModel item)
    {
        var fullPath = _connect.FolderManager.GetFullPath(item.Id);
        if (FileExecutionSafety.IsSafeToOpenDirectly(fullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch
            {
                _platform.RevealInFileManager(fullPath);
            }
        }
        else
        {
            _platform.RevealInFileManager(fullPath);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        _platform.RevealInFileManager(_connect.FolderManager.StorageDirectory);
    }

    [RelayCommand]
    private void ToggleQrOverlay()
    {
        IsQrOverlayVisible = !IsQrOverlayVisible;
        if (IsQrOverlayVisible)
        {
            _connect.GenerateNewPairingSecret();
            OnPropertyChanged(nameof(QrBitmap));
            OnPropertyChanged(nameof(MobileUrl));
        }
    }

    [RelayCommand]
    private void CloseQrOverlay()
    {
        IsQrOverlayVisible = false;
    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync(string deviceId)
    {
        await _connect.DisconnectDeviceAsync(deviceId);
    }

    [RelayCommand]
    private async Task CopyMobileUrlAsync()
    {
        if (!string.IsNullOrEmpty(MobileUrl))
        {
            var cb = GetClipboard();
            if (cb is not null)
            {
                await cb.SetTextAsync(MobileUrl);
            }
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        await _connect.StopSessionAsync();
        RequestClose?.Invoke();
    }

    private string GetNetworkLabel()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            {
                return nic.Name;
            }
        }
        return _loc["connect.caption"];
    }

    public void Dispose()
    {
        _connect.MobileConnected -= OnMobileConnected;
        _connect.MobileDisconnected -= OnMobileDisconnected;
        _connect.MessageReceived -= OnMessageReceived;
        _connect.FilesChanged -= OnFilesChanged;
        _connect.SessionEnded -= OnSessionEnded;
        _connect.PinRefreshed -= UpdatePinDisplay;
        _connect.PinTicked -= OnPinTicked;
        _connect.PairedDevicesChanged -= UpdatePairedDevices;
    }
}
