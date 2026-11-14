using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Filee.App.Services.Capture;

/// <summary>
/// Captures Windows system audio (speaker/headphone loopback) using WASAPI Loopback.
/// Writes standard PCM/IEEE-float WAV audio to disk synchronized with recording.
/// </summary>
public sealed class WasapiLoopbackRecorder : IDisposable
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private readonly object _lock = new();
    private Thread? _captureThread;
    private volatile bool _isRecording;
    private FileStream? _fileStream;
    private BinaryWriter? _writer;
    private string? _filePath;
    private long _bytesWritten;
    private WaveFormatEx _format;

    public bool IsRecording => _isRecording;
    public string? OutputPath => _filePath;

    public bool Start(string wavPath)
    {
        if (!IsSupported)
            return false;

        lock (_lock)
        {
            if (_isRecording)
                return false;

            try
            {
                var dir = Path.GetDirectoryName(wavPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _filePath = wavPath;
                _fileStream = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new BinaryWriter(_fileStream, Encoding.ASCII);
                _bytesWritten = 0;

                // Write temporary 44-byte WAV header (finalized in Stop)
                WriteWavHeaderPlaceholder(_writer);

                _isRecording = true;
                _captureThread = new Thread(CaptureLoop)
                {
                    Name = "WasapiLoopbackCapture",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThread.Start();
                return true;
            }
            catch
            {
                CleanupStreams();
                _isRecording = false;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRecording)
                return;

            _isRecording = false;
        }

        try
        {
            _captureThread?.Join(1000);
        }
        catch { }

        lock (_lock)
        {
            try
            {
                FinalizeWavHeader();
            }
            catch { }
            finally
            {
                CleanupStreams();
            }
        }
    }

    private void CleanupStreams()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _fileStream?.Dispose(); } catch { }
        _writer = null;
        _fileStream = null;
    }

    private void WriteWavHeaderPlaceholder(BinaryWriter writer)
    {
        // 44-byte standard RIFF WAV header placeholder
        byte[] placeholder = new byte[44];
        writer.Write(placeholder);
    }

    private void FinalizeWavHeader()
    {
        if (_fileStream == null || _writer == null || _bytesWritten <= 0 || _format.nSamplesPerSec == 0)
            return;

        _writer.Flush();
        _fileStream.Seek(0, SeekOrigin.Begin);

        uint dataSize = (uint)Math.Min(_bytesWritten, uint.MaxValue - 36);
        uint riffSize = dataSize + 36;

        // RIFF header
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(riffSize);
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // Format chunk
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write((uint)16); // Chunk size for PCM
        _writer.Write((ushort)_format.wFormatTag);
        _writer.Write((ushort)_format.nChannels);
        _writer.Write((uint)_format.nSamplesPerSec);
        _writer.Write((uint)_format.nAvgBytesPerSec);
        _writer.Write((ushort)_format.nBlockAlign);
        _writer.Write((ushort)_format.wBitsPerSample);

        // Data chunk
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write(dataSize);
        _writer.Flush();
    }

    private void CaptureLoop()
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr pFormat = IntPtr.Zero;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device);
            if (hr != 0 || device == null)
                return;

            var iidAudioClient = typeof(IAudioClient).GUID;
            hr = device.Activate(ref iidAudioClient, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var clientObj);
            if (hr != 0 || clientObj == null)
                return;

            audioClient = (IAudioClient)clientObj;
            hr = audioClient.GetMixFormat(out pFormat);
            if (hr != 0 || pFormat == IntPtr.Zero)
                return;

            _format = Marshal.PtrToStructure<WaveFormatEx>(pFormat);
            if (_format.wFormatTag == 0xFFFE /* WAVE_FORMAT_EXTENSIBLE */)
            {
                var ext = Marshal.PtrToStructure<WaveFormatExtensible>(pFormat);
                // Check if float or PCM
                if (ext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                {
                    _format.wFormatTag = 3; // WAVE_FORMAT_IEEE_FLOAT
                }
                else
                {
                    _format.wFormatTag = 1; // WAVE_FORMAT_PCM
                }
            }

            // 1 second buffer (10,000,000 * 100ns)
            long bufferDuration = 10_000_000;
            const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            hr = audioClient.Initialize(0 /* SHARED */, AUDCLNT_STREAMFLAGS_LOOPBACK, bufferDuration, 0, pFormat, IntPtr.Zero);
            if (hr != 0)
                return;

            var iidCaptureClient = typeof(IAudioCaptureClient).GUID;
            hr = audioClient.GetService(ref iidCaptureClient, out var captureObj);
            if (hr != 0 || captureObj == null)
                return;

            captureClient = (IAudioCaptureClient)captureObj;
            audioClient.Start();

            var stopwatch = Stopwatch.StartNew();
            byte[] silenceBuffer = new byte[Math.Max(4096, (int)_format.nBlockAlign * 1024)];

            while (_isRecording)
            {
                Thread.Sleep(15);
                if (!_isRecording)
                    break;

                bool hadData = false;
                while (captureClient.GetNextPacketSize(out var packetSize) == 0 && packetSize > 0)
                {
                    hadData = true;
                    hr = captureClient.GetBuffer(out var pData, out var numFramesToRead, out var bufferFlags, out _, out _);
                    if (hr == 0 && numFramesToRead > 0)
                    {
                        int bytesToRead = (int)(numFramesToRead * _format.nBlockAlign);
                        if ((bufferFlags & 1) != 0 /* SILENT */)
                        {
                            WriteSilence(bytesToRead, silenceBuffer);
                        }
                        else
                        {
                            byte[] buffer = new byte[bytesToRead];
                            Marshal.Copy(pData, buffer, 0, bytesToRead);
                            WriteAudio(buffer);
                        }

                        captureClient.ReleaseBuffer(numFramesToRead);
                    }
                    else
                    {
                        break;
                    }
                }

                // If no audio packet arrived (speakers idle), keep sync by writing silence matching elapsed time
                if (!hadData)
                {
                    long targetBytes = (long)(stopwatch.Elapsed.TotalSeconds * _format.nAvgBytesPerSec);
                    targetBytes = (targetBytes / _format.nBlockAlign) * _format.nBlockAlign;
                    long missing = targetBytes - _bytesWritten;
                    if (missing > 0)
                    {
                        int fillBytes = (int)Math.Min(missing, _format.nAvgBytesPerSec / 10); // max 100ms chunk
                        WriteSilence(fillBytes, silenceBuffer);
                    }
                }
            }

            audioClient.Stop();
        }
        catch
        {
            // Graceful ignore
        }
        finally
        {
            if (pFormat != IntPtr.Zero)
            {
                try { Marshal.FreeCoTaskMem(pFormat); } catch { }
            }
        }
    }

    private void WriteAudio(byte[] buffer)
    {
        lock (_lock)
        {
            if (_writer != null && _isRecording)
            {
                _writer.Write(buffer);
                _bytesWritten += buffer.Length;
            }
        }
    }

    private void WriteSilence(int byteCount, byte[] silenceBuffer)
    {
        lock (_lock)
        {
            if (_writer != null && _isRecording)
            {
                int remaining = byteCount;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, silenceBuffer.Length);
                    _writer.Write(silenceBuffer, 0, chunk);
                    _bytesWritten += chunk;
                    remaining -= chunk;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    // COM Interop definitions
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr strId);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr dataPointer, out uint numFramesToRead, out int bufferFlags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
}
