using System;

namespace MZRaku.Hardware;

/// <summary>
/// Square-wave sound driven by 8253 counter-0 reload value and 8255 PC3 gate.
/// The PIT input clock on MZ-700 is approximately 895KHz (895031 Hz exactly in
/// some references, derived from the video system). Output frequency in mode 3
/// is inputHz / reload.
/// </summary>
public sealed class Sound : IDisposable
{
    private const int SampleRate = 44100;
    private const int ChunkMs = 20;
    private const int SamplesPerChunk = SampleRate * ChunkMs / 1000;   // 882
    private const int BytesPerChunk = SamplesPerChunk * 2;             // 1764 (16-bit mono)
    private const int BufferCount = 16;                                // ~320 ms of headroom
    private const double TargetBufferMs = 100.0;                       // feed-loop throttle

    private WinmmWaveOut? _wave;
    private System.Threading.Thread? _thread;
    private volatile bool _running;
    private volatile bool _muted;

    /// <summary>
    /// When true, FeedLoop stops submitting new PCM chunks and the
    /// wave-out queue is flushed immediately on transition. Used by
    /// MainForm to silence the currently-playing tone the instant the
    /// emulator pauses — without it the ~100 ms of already-queued audio
    /// keeps sounding for a beat after pause and any continuous tone
    /// (SA-1510 boot beep, MUSIC held note) sustains indefinitely
    /// because the PIT state doesn't advance to close its gate.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            if (value) _wave?.Reset();
        }
    }

    public double InputClockHz = 895000.0;
    // The MZ-700 has two independent gates between C0.OUT and the
    // speaker (confirmed against the service-manual schematic
    // 2026-06-19 — see Mz700SoundReference):
    //   Soft gate (Enabled)  ← PPI PC3 via IC7E LS74 FF2 → PIT GATE0.
    //   Hard gate (HardGate) ← write D0 to $E008, latched by IC7E
    //                          LS74 FF1, gates the speaker-amp NAND.
    // Audible iff both gates are asserted. Cleared by Reset (FF1.CL
    // = system RESET line on the schematic).
    public volatile bool Enabled;
    private volatile bool _hardGate;
    private volatile int _gateHoldChunks;
    private volatile int _reload = 0;

    /// <summary>
    /// $E008 D0 gate (MZ-80A) or IC7E FF1 latch (MZ-700). Setting true
    /// also latches a "recent-transition" flag for a couple of feed
    /// chunks so brief pulses (SA-1510's MSTA/MSTP toggle the gate in
    /// under a millisecond — much faster than FeedLoop's 20 ms poll)
    /// still make at least one audible chunk. On real hardware the
    /// speaker cone would move on the current pulse regardless of
    /// duration; this mimics that behavior.
    /// </summary>
    public bool HardGate
    {
        get => _hardGate || _gateHoldChunks > 0;
        set
        {
            _hardGate = value;
            if (value) _gateHoldChunks = 2;  // ~40 ms of audible pulse
        }
    }

    public void SetReload(int reload) { _reload = reload; }

    public void Start()
    {
        _wave = new WinmmWaveOut(SampleRate, 16, 1, BytesPerChunk, BufferCount);
        _running = true;
        _thread = new System.Threading.Thread(FeedLoop) { IsBackground = true };
        _thread.Start();
    }

    private void FeedLoop()
    {
        byte[] buf = new byte[BytesPerChunk];
        double phase = 0;
        while (_running)
        {
            try
            {
                if (_muted)
                {
                    System.Threading.Thread.Sleep(ChunkMs);
                    continue;
                }
                int reload = _reload;
                bool gate = Enabled && HardGate;
                // Consume one chunk of the pulse-hold latch, so brief
                // gate-on events fade after a couple of chunks even if
                // _hardGate is now false.
                if (_gateHoldChunks > 0 && !_hardGate) _gateHoldChunks--;
                double freq = (reload > 1) ? InputClockHz / reload : 0;
                if (!gate || freq < 20 || freq > 20000) freq = 0;

                double step = (freq > 0) ? freq / SampleRate : 0;
                for (int i = 0; i < SamplesPerChunk; i++)
                {
                    short s = 0;
                    if (freq > 0)
                    {
                        phase += step;
                        if (phase >= 1.0) phase -= 1.0;
                        s = (short)(phase < 0.5 ? 6000 : -6000);
                    }
                    buf[i * 2] = (byte)s;
                    buf[i * 2 + 1] = (byte)(s >> 8);
                }
                if (_wave != null)
                {
                    while (_wave.BufferedDuration.TotalMilliseconds > TargetBufferMs && _running)
                        System.Threading.Thread.Sleep(5);
                    _wave.AddSamples(buf, 0, buf.Length);
                }
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(200); } catch { }
        _wave?.Dispose();
        _wave = null;
    }
}
