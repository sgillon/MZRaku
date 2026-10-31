using System;

namespace MZRaku.Core;

/// <summary>
/// Square-wave sound generator shared by all three machines. Output
/// frequency = <see cref="InputClockHz"/> / reload value when both
/// gates are open. Two AND-gated inputs, polled per audio chunk:
///   <see cref="Enabled"/> — soft gate. MZ-700 drives this from PPI
///     PC3 (speaker gate); MZ-80A has no soft gate and pins it true.
///   <see cref="HardGate"/> — hard gate. MZ-700 uses this as one half
///     of its dual-gate topology ($E011/$E012 pulse + $E008 D0
///     latch, ANDed via the speaker NAND). MZ-80A uses it alone as
///     the single hard gate at $E008 D0.
/// <see cref="InputClockHz"/> is set by the host machine: ~895 kHz
/// for MZ-700's PIT counter 0, 1 MHz for MZ-80A's counter 0.
///
/// MZ-800 uses <see cref="ExternalPcm"/> mode instead: the machine
/// renders PSG + counter-0 audio in emulated time and queues it via
/// <see cref="PushSample"/>.
///
/// The PCM goes to an <see cref="IAudioOut"/> the front end opens in
/// <see cref="OutputFormat"/> and hands to <see cref="Start"/>.
/// </summary>
public sealed class Sound : IDisposable
{
    private const int SampleRate = 44100;
    private const int ChunkMs = 20;
    private const int SamplesPerChunk = SampleRate * ChunkMs / 1000;   // 882
    private const int BytesPerChunk = SamplesPerChunk * 2;             // 1764 (16-bit mono)
    private const int BufferCount = 16;                                // ~320 ms of headroom
    private const double TargetBufferMs = 100.0;                       // feed-loop throttle
    // External PCM mode: device queue + ring each held shorter. The
    // ring must cover one emulated frame (~16.7 ms, rendered in a
    // burst) plus timer jitter; ~110 ms total output latency.
    private const double ExternalDeviceMs = 60.0;
    private const int ExternalRingTargetMs = 50;

    /// <summary>
    /// The format and buffering the feed thread plays at: 16-bit mono at
    /// <see cref="OutputSampleRate"/>, in chunks of one feed period.
    /// </summary>
    public static readonly AudioOutFormat OutputFormat =
        new(SampleRate, 16, 1, BytesPerChunk, BufferCount);

    private IAudioOut? _wave;
    private System.Threading.Thread? _thread;
    private volatile bool _running;
    private volatile bool _muted;

    /// <summary>
    /// When true, FeedLoop stops submitting new PCM chunks and the
    /// wave-out queue is flushed immediately on transition. Used by
    /// the host to silence the currently-playing tone the instant the
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

    // ---- Event log (--dump) ----
    //
    // The square-wave path plays whatever these four inputs hold when
    // the feed thread polls them, so its audio depends on real time and
    // can't be compared run to run. Logging every change to them,
    // stamped with the emulated CPU cycle, gives a deterministic record
    // of what the machine asked the speaker to do: DumpTraceRecorder
    // writes it as <dump>.snd.txt for the regression suite.

    /// <summary>
    /// When non-null, every change to the soft gate, hard gate, reload
    /// and input clock is appended as <c>&lt;cycle&gt; &lt;input&gt;=&lt;value&gt;</c>.
    /// Set through <see cref="StartEventLog"/>.
    /// </summary>
    internal System.Text.StringBuilder? EventLog { get; private set; }
    private Func<long>? _eventClock;

    /// <summary>
    /// Start logging input changes, stamped by <paramref name="clock"/>
    /// (the emulated CPU cycle count). The first line records the
    /// current state, so the log stands on its own.
    /// </summary>
    internal void StartEventLog(Func<long> clock)
    {
        _eventClock = clock;
        EventLog = new System.Text.StringBuilder();
        LogEvent($"init soft={(_enabled ? 1 : 0)} hard={(_hardGate ? 1 : 0)} reload={_reload} clock={_inputClockHz:0}");
    }

    private void LogEvent(string what) =>
        EventLog?.Append(_eventClock!()).Append(' ').AppendLine(what);

    private double _inputClockHz = 895000.0;
    internal double InputClockHz
    {
        get => _inputClockHz;
        set
        {
            if (value == _inputClockHz) return;
            _inputClockHz = value;
            LogEvent($"clock={value:0}");
        }
    }

    // The MZ-700 has two independent gates between C0.OUT and the
    // speaker (confirmed against the service-manual schematic
    // 2026-06-19 — see Mz700SoundReference):
    //   Soft gate (Enabled)  ← PPI PC3 via IC7E LS74 FF2 → PIT GATE0.
    //   Hard gate (HardGate) ← write D0 to $E008, latched by IC7E
    //                          LS74 FF1, gates the speaker-amp NAND.
    // Audible iff both gates are asserted. Cleared by Reset (FF1.CL
    // = system RESET line on the schematic).
    private volatile bool _enabled;
    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            LogEvent($"soft={(value ? 1 : 0)}");
        }
    }
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
            // A repeated write changes nothing the feed loop can hear:
            // the hold only counts down while the gate is closed.
            if (value != _hardGate) LogEvent($"hard={(value ? 1 : 0)}");
            _hardGate = value;
            if (value) _gateHoldChunks = 2;  // ~40 ms of audible pulse
        }
    }

    internal void SetReload(int reload)
    {
        if (reload != _reload) LogEvent($"reload={reload}");
        _reload = reload;
    }

    /// <summary>
    /// The raw $E008 D0 latch, without the pulse-hold stretch that
    /// <see cref="HardGate"/> adds for the chunk-polled square wave.
    /// External-PCM hosts render in emulated time and need the exact
    /// level (the hold only decays inside the square-wave feed loop).
    /// </summary>
    public bool HardGateLatch => _hardGate;

    // ---- External PCM mode (MZ-800 PSG) ----
    //
    // The square-wave path above snapshots gate/reload state once per
    // 20 ms chunk — fine for a single beeper, too coarse for a PSG
    // whose registers change every ~10 ms (BASIC MUSIC). In external
    // mode the machine renders samples in emulated time via
    // PushSample (emulation thread) and FeedLoop drains them. Frame
    // pacing is locked to real time (by the host), so producer and
    // consumer rates match on average; the drain rate only trims ±0.5 %
    // against a ~1 s moving average of the queue depth, to absorb
    // audio-clock drift without audible pitch change. (A first cut
    // steered on the instantaneous depth, which jumps by a frame's
    // worth every timer tick — sustained notes wobbled.) On underrun it
    // holds the last sample (no click) instead of inserting silence.

    /// <summary>
    /// When true, FeedLoop plays samples queued via
    /// <see cref="PushSample"/> instead of generating the square wave.
    /// Set once by the host machine before <see cref="Start"/>.
    /// </summary>
    internal bool ExternalPcm;

    /// <summary>Output sample rate the host must render at.</summary>
    internal const int OutputSampleRate = SampleRate;

    private readonly short[] _ring = new short[SampleRate];   // 1 s
    private volatile int _ringWrite;
    private volatile int _ringRead;
    private double _readFrac;
    private short _lastSample;
    private double _fillAverage = -1;

    private int RingCount => (_ringWrite - _ringRead + _ring.Length) % _ring.Length;

    /// <summary>
    /// Queue one output sample (external PCM mode). Called from the
    /// emulation thread. Drops the sample if the ring is full (the
    /// emulator running far ahead of real time, e.g. while muted).
    /// </summary>
    internal void PushSample(short s)
    {
        int w = _ringWrite;
        int next = (w + 1) % _ring.Length;
        if (next == _ringRead) return;
        _ring[w] = s;
        _ringWrite = next;
    }

    private void FillExternal(byte[] buf)
    {
        int target = SampleRate * ExternalRingTargetMs / 1000;
        int available = RingCount;
        // Chunks are 20 ms, so α = 0.02 averages over ~1 s.
        _fillAverage = _fillAverage < 0 ? available : _fillAverage + 0.02 * (available - _fillAverage);
        // >target → read slightly faster; <target → slightly slower.
        double ratio = 1.0 + 0.01 * (_fillAverage - target) / target;
        ratio = Math.Clamp(ratio, 0.995, 1.005);
        // Far behind real time (e.g. a debugger stall refilled the ring
        // late): drop the excess outright rather than play it back late.
        if (available > 4 * target)
        {
            _ringRead = (_ringWrite - target + _ring.Length) % _ring.Length;
            _fillAverage = target;
        }

        for (int i = 0; i < SamplesPerChunk; i++)
        {
            _readFrac += ratio;
            int advance = (int)_readFrac;
            _readFrac -= advance;
            for (int k = 0; k < advance; k++)
            {
                int r = _ringRead;
                if (r == _ringWrite) break;                     // underrun: hold level
                _lastSample = _ring[r];
                _ringRead = (r + 1) % _ring.Length;
            }
            buf[i * 2] = (byte)_lastSample;
            buf[i * 2 + 1] = (byte)(_lastSample >> 8);
        }
    }

    /// <summary>
    /// Start the feed thread, playing into <paramref name="output"/>
    /// (opened in <see cref="OutputFormat"/>). Sound owns it from here
    /// and disposes it in <see cref="Dispose"/>.
    /// </summary>
    internal void Start(IAudioOut output)
    {
        _wave = output;
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
                    // Discard whatever the emulator queued so unmuting
                    // doesn't replay stale audio.
                    if (ExternalPcm) _ringRead = _ringWrite;
                    System.Threading.Thread.Sleep(ChunkMs);
                    continue;
                }
                if (ExternalPcm)
                {
                    // Pace on the device queue first so the ring
                    // (filled in emulated time) is sampled at the
                    // moment the chunk is actually needed.
                    if (_wave != null)
                        while (_wave.BufferedDuration.TotalMilliseconds > ExternalDeviceMs && _running)
                            System.Threading.Thread.Sleep(2);
                    FillExternal(buf);
                    _wave?.AddSamples(buf, 0, buf.Length);
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
