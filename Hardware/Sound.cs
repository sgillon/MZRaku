using System;
using NAudio.Wave;

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
    private WaveOutEvent? _wave;
    private BufferedWaveProvider? _provider;
    private System.Threading.Thread? _thread;
    private volatile bool _running;

    public double InputClockHz = 895000.0;
    public volatile bool Enabled;   // PPI PC3 gate
    private volatile int _reload = 0;

    public void SetReload(int reload) { _reload = reload; }

    public void Start()
    {
        _wave = new WaveOutEvent { DesiredLatency = 100 };
        _provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };
        _wave.Init(_provider);
        _wave.Play();
        _running = true;
        _thread = new System.Threading.Thread(FeedLoop) { IsBackground = true };
        _thread.Start();
    }

    private void FeedLoop()
    {
        const int chunkMs = 20;
        int samplesPerChunk = SampleRate * chunkMs / 1000;
        byte[] buf = new byte[samplesPerChunk * 2];
        double phase = 0;
        while (_running)
        {
            try
            {
                int reload = _reload;
                bool gate = Enabled;
                double freq = (reload > 1) ? InputClockHz / reload : 0;
                if (!gate || freq < 20 || freq > 20000) freq = 0;

                double step = (freq > 0) ? freq / SampleRate : 0;
                for (int i = 0; i < samplesPerChunk; i++)
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
                if (_provider != null)
                {
                    while (_provider.BufferedDuration.TotalMilliseconds > 100 && _running)
                        System.Threading.Thread.Sleep(5);
                    _provider.AddSamples(buf, 0, buf.Length);
                }
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(200); } catch { }
        _wave?.Stop();
        _wave?.Dispose();
    }
}
