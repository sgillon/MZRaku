using System;
using System.Runtime.InteropServices;

namespace MZRaku.Hardware;

/// <summary>
/// Minimal streaming PCM player on top of Windows' built-in
/// <c>winmm.dll</c> <c>waveOut*</c> API — the same DLL we already
/// P/Invoke from <see cref="JoystickInput"/>. Replaces our previous
/// NAudio dependency for the one job we needed it for: push 16-bit
/// PCM chunks at a fixed sample rate and let the OS mixer play them.
///
/// Mechanism: a fixed pool of unmanaged audio buffers (allocated once
/// in the constructor) plus a pinned array of <c>WAVEHDR</c> headers.
/// <see cref="AddSamples"/> finds a free header, copies the caller's
/// bytes into the matching unmanaged buffer, prepares it and queues it
/// with <c>waveOutWrite</c>. Headers are marked done by the driver
/// via the <c>WHDR_DONE</c> flag, at which point we recycle the slot.
/// If no slot is free when <see cref="AddSamples"/> is called the
/// chunk is dropped (equivalent to NAudio's
/// <c>DiscardOnBufferOverflow</c>).
/// </summary>
internal sealed class WinmmWaveOut : IDisposable
{
    private readonly int _bytesPerSecond;
    private readonly int _bufferSize;
    private readonly int _bufferCount;

    private readonly IntPtr[] _bufPtrs;
    private readonly WAVEHDR[] _hdrs;
    private GCHandle _hdrsPin;
    private readonly bool[] _prepared;

    private IntPtr _hwo;
    private readonly object _lock = new();
    private bool _disposed;

    public WinmmWaveOut(int sampleRate, int bitsPerSample, int channels,
                        int bufferSize, int bufferCount)
    {
        _bufferSize = bufferSize;
        _bufferCount = bufferCount;
        _bytesPerSecond = sampleRate * channels * bitsPerSample / 8;

        _bufPtrs = new IntPtr[bufferCount];
        _hdrs = new WAVEHDR[bufferCount];
        _prepared = new bool[bufferCount];
        _hdrsPin = GCHandle.Alloc(_hdrs, GCHandleType.Pinned);

        for (int i = 0; i < bufferCount; i++)
            _bufPtrs[i] = Marshal.AllocHGlobal(bufferSize);

        short blockAlign = (short)(channels * bitsPerSample / 8);
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = (short)channels,
            nSamplesPerSec = sampleRate,
            nAvgBytesPerSec = _bytesPerSecond,
            nBlockAlign = blockAlign,
            wBitsPerSample = (short)bitsPerSample,
            cbSize = 0,
        };

        int rc = waveOutOpen(out _hwo, WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (rc != 0)
            throw new InvalidOperationException($"waveOutOpen failed (mmsys error {rc})");
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_lock)
            {
                long bytes = 0;
                for (int i = 0; i < _bufferCount; i++)
                {
                    if (_prepared[i] && (_hdrs[i].dwFlags & WHDR_DONE) == 0)
                        bytes += _hdrs[i].dwBufferLength;
                }
                return TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);
            }
        }
    }

    /// <summary>
    /// Submit a chunk of PCM. Bytes are copied; the caller's buffer can
    /// be reused as soon as this returns. If no slot is free or the
    /// chunk is larger than the configured buffer size, the chunk is
    /// silently dropped.
    /// </summary>
    public void AddSamples(byte[] data, int offset, int count)
    {
        if (count <= 0 || count > _bufferSize) return;

        lock (_lock)
        {
            if (_disposed || _hwo == IntPtr.Zero) return;

            int slot = FindFreeSlot();
            if (slot < 0) return;

            Marshal.Copy(data, offset, _bufPtrs[slot], count);
            _hdrs[slot] = new WAVEHDR
            {
                lpData = _bufPtrs[slot],
                dwBufferLength = count,
                dwFlags = 0,
            };
            int sz = Marshal.SizeOf<WAVEHDR>();
            int rc = waveOutPrepareHeader(_hwo, ref _hdrs[slot], sz);
            if (rc != 0) return;
            _prepared[slot] = true;
            waveOutWrite(_hwo, ref _hdrs[slot], sz);
        }
    }

    private int FindFreeSlot()
    {
        int sz = Marshal.SizeOf<WAVEHDR>();
        for (int i = 0; i < _bufferCount; i++)
        {
            if (!_prepared[i]) return i;
            if ((_hdrs[i].dwFlags & WHDR_DONE) != 0)
            {
                waveOutUnprepareHeader(_hwo, ref _hdrs[i], sz);
                _prepared[i] = false;
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Immediately stop playback and flush any queued buffers. The
    /// device stays open so a subsequent <see cref="AddSamples"/>
    /// resumes without re-init latency. Used by <see cref="Sound.Muted"/>
    /// to kill the currently-playing tone the instant the emulator
    /// pauses, rather than letting the ~100 ms of already-queued audio
    /// drain naturally.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed || _hwo == IntPtr.Zero) return;
            waveOutReset(_hwo);
            int sz = Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < _bufferCount; i++)
            {
                if (_prepared[i])
                {
                    waveOutUnprepareHeader(_hwo, ref _hdrs[i], sz);
                    _prepared[i] = false;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_hwo != IntPtr.Zero)
            {
                waveOutReset(_hwo);
                int sz = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < _bufferCount; i++)
                {
                    if (_prepared[i])
                    {
                        waveOutUnprepareHeader(_hwo, ref _hdrs[i], sz);
                        _prepared[i] = false;
                    }
                }
                waveOutClose(_hwo);
                _hwo = IntPtr.Zero;
            }

            for (int i = 0; i < _bufferCount; i++)
            {
                if (_bufPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_bufPtrs[i]);
                    _bufPtrs[i] = IntPtr.Zero;
                }
            }

            if (_hdrsPin.IsAllocated) _hdrsPin.Free();
        }
    }

    // ---- WinMM waveOut P/Invoke ----

    private const short WAVE_FORMAT_PCM = 1;
    private const int WAVE_MAPPER = -1;
    private const int CALLBACK_NULL = 0;
    private const int WHDR_DONE = 0x00000001;

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx,
                                          IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }
}
