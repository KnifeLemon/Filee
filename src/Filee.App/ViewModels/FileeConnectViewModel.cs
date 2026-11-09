using System.Collections.ObjectModel;
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

    public string Id { get; }
    public string Text { get; }
    public string From { get; }
    public long Timestamp { get; }
    public string TimeText { get; }
    public bool IsFromPc { get; }
    public string SenderLabel { get; }

    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private string _copyButtonText = "Copy";

    public ConnectMessageItemViewModel(ConnectMessage message, Action<string> copyAction, string? mobileDeviceName)
    {
        _copyAction = copyAction;
        Id = message.Id;
        Text = message.Text;
        From = message.From;
        Timestamp = message.Timestamp;
        IsFromPc = message.From.Equals("pc", StringComparison.OrdinalIgnoreCase);
        SenderLabel = IsFromPc ? "This PC" : (mobileDeviceName ?? "Phone");
        TimeText = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).LocalDateTime.ToString("t");
    }

    [RelayCommand]
    private void Copy()
    {
        _copyAction(Text);
        IsCopied = true;
        CopyButtonText = "Copied!";
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsCopied = false;
                CopyButtonText = "Copy";
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
        Action<ConnectFileItemViewModel> preview)
    {
        Id = meta.Id;
        Name = meta.Name;
        RelativePath = meta.RelativePath;
        Size = meta.Size;
        SizeText = FormatSize(meta.Size);
        Sender = meta.Sender;
        SenderText = meta.Sender.Equals("mobile", StringComparison.OrdinalIgnoreCase) ? "Phone" : "PC";
        MimeType = meta.MimeType;
        IsFolder = false;

        var ext = Path.GetExtension(meta.Name).ToLowerInvariant();
        IconText = meta.MimeType.StartsWith("image/") ? "🖼️" : ext == ".pdf" ? "📄" : "📎";
        CanPreview = meta.MimeType.StartsWith("image/") || meta.MimeType == "application/pdf" || meta.MimeType.StartsWith("text/");

        OpenOrFolderCommand = new RelayCommand(() => preview(this));
        DeleteCommand = new RelayCommand(() => delete(this));
        PreviewCommand = new RelayCommand(() => preview(this));
    }

    public ConnectFileItemViewModel(
        string folderName,
        string folderPath,
        int itemCount,
        Action<ConnectFileItemViewModel> openFolder,
        Action<ConnectFileItemViewModel> delete)
    {
        Id = folderPath;
        Name = folderName;
        RelativePath = folderPath;
        Size = 0;
        SizeText = $"{itemCount} item{(itemCount == 1 ? "" : "s")}";
        Sender = "";
        SenderText = "Folder";
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

    public FileeConnectService ConnectService => _connect;
    public Bitmap? QrBitmap => _connect.QrBitmap;
    public string? MobileUrl => _connect.MobileUrl;
    public string? LocalhostUrl => _connect.LocalhostUrl;
    public Avalonia.Media.IBrush StatusColorBrush => IsMobileConnected
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22C55E"))
        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EAB308"));

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isMobileConnected;
    [ObservableProperty] private string _connectedDeviceTitle = "Waiting for mobile…";
    [ObservableProperty] private string _storageUsageText = "0 B / 2 GB";
    [ObservableProperty] private string _itemCountText = "0 items";
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private bool _isInSubfolder;

    public ObservableCollection<ConnectMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<ConnectFileItemViewModel> Files { get; } = [];

    public event Action? RequestClose;
    public event Action<string>? RequestFilePreview;

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

        IsMobileConnected = _connect.IsMobileConnected;
        UpdateDeviceTitle(_connect.ConnectedDeviceName);

        // Load existing history
        foreach (var msg in _connect.MessageHistory)
        {
            Messages.Add(new ConnectMessageItemViewModel(msg, CopyToClipboard, _connect.ConnectedDeviceName));
        }

        RefreshFiles();
    }

    private void UpdateDeviceTitle(string? deviceName)
    {
        ConnectedDeviceTitle = IsMobileConnected
            ? $"{deviceName ?? "Mobile"} Connected"
            : "Waiting for connection…";
    }

    private void OnMobileConnected(string deviceName)
    {
        IsMobileConnected = true;
        UpdateDeviceTitle(deviceName);
        OnPropertyChanged(nameof(StatusColorBrush));
    }

    private void OnMobileDisconnected()
    {
        IsMobileConnected = false;
        UpdateDeviceTitle(null);
        OnPropertyChanged(nameof(StatusColorBrush));
    }

    private void OnMessageReceived(ConnectMessage msg)
    {
        Messages.Add(new ConnectMessageItemViewModel(msg, CopyToClipboard, _connect.ConnectedDeviceName));
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
            if (_clipboard is not null)
            {
                text = await _clipboard.TryGetTextAsync();
            }

            if (string.IsNullOrEmpty(text))
            {
                text = _platform.ReadClipboardText();
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
            if (_clipboard is not null)
            {
                _ = _clipboard.SetTextAsync(text);
            }
            _platform.SetClipboardText(text);
        }
        catch { }
    }

    [RelayCommand]
    private void RefreshFiles()
    {
        Files.Clear();
        var all = _connect.FolderManager.GetFiles();
        var usage = _connect.FolderManager.GetStorageUsage();
        StorageUsageText = $"{ConnectFileItemViewModel.FormatSize(usage.Used)} / {ConnectFileItemViewModel.FormatSize(usage.Limit)}";

        var prefix = string.IsNullOrEmpty(CurrentPath) ? "" : $"{CurrentPath}/";
        IsInSubfolder = !string.IsNullOrEmpty(CurrentPath);

        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var direct = new List<SharedFileMetadata>();

        foreach (var file in all)
        {
            if (!file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = file.RelativePath[prefix.Length..];
            var slash = remainder.IndexOf('/');
            if (slash >= 0)
            {
                var dirName = remainder[..slash];
                dirs[dirName] = dirs.GetValueOrDefault(dirName, 0) + 1;
            }
            else
            {
                direct.Add(file);
            }
        }

        foreach (var (folderName, count) in dirs.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folderPath = string.IsNullOrEmpty(CurrentPath) ? folderName : $"{CurrentPath}/{folderName}";
            Files.Add(new ConnectFileItemViewModel(folderName, folderPath, count, OpenFolder, DeleteItem));
        }

        foreach (var file in direct.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(new ConnectFileItemViewModel(file, OpenFolder, DeleteItem, PreviewFile));
        }

        var total = Files.Count;
        ItemCountText = $"{total} item{(total == 1 ? "" : "s")}";
    }

    private void OpenFolder(ConnectFileItemViewModel item)
    {
        if (item.IsFolder)
        {
            CurrentPath = item.RelativePath;
            RefreshFiles();
        }
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var lastSlash = CurrentPath.LastIndexOf('/');
        CurrentPath = lastSlash >= 0 ? CurrentPath[..lastSlash] : "";
        RefreshFiles();
    }

    [RelayCommand]
    private void NavigateRoot()
    {
        CurrentPath = "";
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
    }

    private void PreviewFile(ConnectFileItemViewModel item)
    {
        var file = _connect.FolderManager.GetFile(item.Id);
        if (file is not null && File.Exists(file.DiskPath))
        {
            RequestFilePreview?.Invoke(file.DiskPath);
        }
    }

    public async Task AddLocalFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var rel = string.IsNullOrEmpty(CurrentPath) ? Path.GetFileName(path) : $"{CurrentPath}/{Path.GetFileName(path)}";
                await _connect.FolderManager.AddLocalFileAsync(path, rel);
            }
            else if (Directory.Exists(path))
            {
                var dirName = Path.GetFileName(path);
                var baseRel = string.IsNullOrEmpty(CurrentPath) ? dirName : $"{CurrentPath}/{dirName}";
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var fileRel = Path.GetRelativePath(path, file).Replace('\\', '/');
                    await _connect.FolderManager.AddLocalFileAsync(file, $"{baseRel}/{fileRel}");
                }
            }
        }
        RefreshFiles();
    }

    [RelayCommand]
    private void OpenLocalFolder()
    {
        var path = _connect.FolderManager.StorageDirectory;
        if (Directory.Exists(path))
        {
            _platform.RevealInFileManager(path);
        }
    }

    [RelayCommand]
    private async Task CopyMobileUrlAsync()
    {
        if (!string.IsNullOrEmpty(MobileUrl))
        {
            if (_clipboard is not null)
            {
                await _clipboard.SetTextAsync(MobileUrl);
            }
            _platform.SetClipboardText(MobileUrl);
        }
    }

    [RelayCommand]
    private void OpenLocalhost()
    {
        if (!string.IsNullOrEmpty(LocalhostUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalhostUrl) { UseShellExecute = true });
            }
            catch { }
        }
    }

    [RelayCommand]
    private async Task CopyLocalhostUrlAsync()
    {
        if (!string.IsNullOrEmpty(LocalhostUrl))
        {
            if (_clipboard is not null)
            {
                await _clipboard.SetTextAsync(LocalhostUrl);
            }
            _platform.SetClipboardText(LocalhostUrl);
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        await _connect.StopSessionAsync();
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        _connect.MobileConnected -= OnMobileConnected;
        _connect.MobileDisconnected -= OnMobileDisconnected;
        _connect.MessageReceived -= OnMessageReceived;
        _connect.FilesChanged -= OnFilesChanged;
        _connect.SessionEnded -= OnSessionEnded;
    }
}
