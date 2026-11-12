using Avalonia.Media.Imaging;

namespace Filee.App.Services.Capture;

public sealed class MobileMirrorService
{
    private readonly object _lock = new();
    private byte[]? _lastJpegFrame;
    private bool _isStreaming;
    private int _frameCount;
    private DateTime _lastFrameTime = DateTime.UtcNow;

    public bool IsStreaming => _isStreaming;
    public byte[]? LastJpegFrame => _lastJpegFrame;
    public int FrameCount => _frameCount;

    public event Action<byte[]>? FrameReceived;
    public event Action<bool>? StateChanged;

    public void OnConnected()
    {
        lock (_lock)
        {
            _isStreaming = true;
            _frameCount = 0;
            _lastFrameTime = DateTime.UtcNow;
        }
        StateChanged?.Invoke(true);
    }

    public void OnFrame(byte[] jpegBytes)
    {
        lock (_lock)
        {
            _isStreaming = true;
            _lastJpegFrame = jpegBytes;
            _frameCount++;
            _lastFrameTime = DateTime.UtcNow;
        }

        FrameReceived?.Invoke(jpegBytes);
    }

    public void OnDisconnected()
    {
        lock (_lock)
        {
            _isStreaming = false;
        }
        StateChanged?.Invoke(false);
    }
}
