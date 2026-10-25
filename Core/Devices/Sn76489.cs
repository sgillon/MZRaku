using System;

namespace MZRaku.Hardware;

/// <summary>
/// TI SN76489(A)N programmable sound generator — three square-wave
/// tone channels plus one noise channel, each with a 4-bit
/// attenuator. MZ-800: write-only at port $F2 (tech-ref p. 31),
/// clocked from the CPU clock.
///
/// Register interface (one byte per write):
///   1 cc t dddd  latch: channel cc (0-2 tone, 3 noise), type t
///                (0 = tone/noise control, 1 = attenuation), low data
///   0 x dddddd   data: upper 6 bits of the latched tone period (or,
///                if the latch is attenuation / noise, replaces its
///                low bits)
///
/// Timing: the chip divides its input clock by 16. A tone channel's
/// 10-bit counter reloads with its period N and flips the channel
/// output at each expiry → f = clock / (32 × N). N = 0 behaves as
/// $400. The noise channel's counter reloads with $10/$20/$40 (or
/// tone 2's period when rate = 3); its flip-flop shifts a 15-bit LFSR
/// on each rising edge — white noise taps bits 0 and 1, periodic noise
/// recirculates bit 0. Writing the noise control register resets the
/// LFSR.
///
/// Attenuation: 2 dB per step, 15 = off. Output levels are unipolar
/// (0 or the channel's volume), the way the chip drives its mixer;
/// the host removes the DC component.
/// </summary>
public sealed class Sn76489
{
    private readonly int[] _period = new int[4];     // [3] = noise control (3 bits)
    private readonly int[] _atten = { 15, 15, 15, 15 };
    private readonly int[] _counter = new int[4];
    private readonly bool[] _out = new bool[4];
    private int _latchChannel;
    private bool _latchVolume;
    private int _lfsr = LfsrSeed;

    private const int LfsrSeed = 0x4000;  // 15-bit register, top bit set

    /// <summary>Linear amplitude per attenuation step (0 = loudest,
    /// 15 = silent), scaled so four channels at full volume sum to 1.</summary>
    private static readonly float[] Volume = BuildVolumeTable();

    private static float[] BuildVolumeTable()
    {
        var t = new float[16];
        for (int i = 0; i < 15; i++) t[i] = (float)(0.25 * Math.Pow(10, -2.0 * i / 20.0));
        t[15] = 0f;
        return t;
    }

    /// <summary>Optional diagnostic hook: every register byte written.</summary>
    public event Action<byte>? OnWrite;

    // Read-only register views for the Sound Diagnostic.
    /// <summary>10-bit tone period N of channel 0-2 (0 behaves as $400).</summary>
    public int TonePeriod(int channel) => _period[channel];
    /// <summary>4-bit attenuation of channel 0-3 (3 = noise); 15 = off.</summary>
    public int Attenuation(int channel) => _atten[channel];
    /// <summary>Noise control: D2 = white (1) / periodic (0), D1-D0 = rate.</summary>
    public int NoiseControl => _period[3];

    public void Reset()
    {
        Array.Clear(_period);
        Array.Clear(_counter);
        Array.Clear(_out);
        for (int i = 0; i < 4; i++) _atten[i] = 15;
        _latchChannel = 0;
        _latchVolume = false;
        _lfsr = LfsrSeed;
    }

    public void Write(byte value)
    {
        OnWrite?.Invoke(value);
        if ((value & 0x80) != 0)
        {
            _latchChannel = (value >> 5) & 0x03;
            _latchVolume = (value & 0x10) != 0;
            if (_latchVolume)
                _atten[_latchChannel] = value & 0x0F;
            else if (_latchChannel == 3)
                WriteNoise(value & 0x07);
            else
                _period[_latchChannel] = (_period[_latchChannel] & 0x3F0) | (value & 0x0F);
            return;
        }

        if (_latchVolume)
            _atten[_latchChannel] = value & 0x0F;
        else if (_latchChannel == 3)
            WriteNoise(value & 0x07);
        else
            _period[_latchChannel] = (_period[_latchChannel] & 0x00F) | ((value & 0x3F) << 4);
    }

    private void WriteNoise(int control)
    {
        _period[3] = control;
        _lfsr = LfsrSeed;
    }

    /// <summary>
    /// Advance one internal step (input clock ÷ 16) and return the
    /// summed output level of all four channels, 0..1.
    /// </summary>
    public float Step()
    {
        for (int ch = 0; ch < 3; ch++)
        {
            if (--_counter[ch] <= 0)
            {
                int p = _period[ch];
                _counter[ch] = p == 0 ? 0x400 : p;
                _out[ch] = !_out[ch];
            }
        }

        if (--_counter[3] <= 0)
        {
            int rate = _period[3] & 0x03;
            int reload = rate == 3 ? (_period[2] == 0 ? 0x400 : _period[2]) : 0x10 << rate;
            _counter[3] = reload;
            _out[3] = !_out[3];
            if (_out[3])
            {
                bool white = (_period[3] & 0x04) != 0;
                int feedback = white ? ((_lfsr ^ (_lfsr >> 1)) & 1) : (_lfsr & 1);
                _lfsr = (_lfsr >> 1) | (feedback << 14);
            }
        }

        float level = 0f;
        // Period 0/1 on a tone channel holds its output high — the
        // standard "DAC" trick for sample playback. Counting it as a
        // constant level instead of a ~110 kHz square matches that use.
        for (int ch = 0; ch < 3; ch++)
            if (_out[ch] || _period[ch] == 1) level += Volume[_atten[ch]];
        if ((_lfsr & 1) != 0) level += Volume[_atten[3]];
        return level;
    }
}
