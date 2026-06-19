using System;

namespace MZ700Emul.Hardware;

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
    public volatile bool HardGate;
    private volatile int _reload = 0;

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
                int reload = _reload;
                bool gate = Enabled && HardGate;
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
