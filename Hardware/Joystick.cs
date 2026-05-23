using Z80Core;

namespace MZ700Emul.Hardware;

/// <summary>
/// Sharp MZ-1X03 joystick port emulation. Two sticks, each with X/Y
/// axes (0-255) and two pushbutton switches. Behaviour matches the
/// MZ-1X03 instruction manual:
///
/// $E008 bits 1-6 are joystick lines, multiplexed by VBLK through a
/// 74LS157:
///   - VBLK low (visible portion): bits 1-4 carry axis 555-timer
///     pulse outputs. After VBLK falls, each bit goes low for a
///     duration proportional to the corresponding axis position,
///     then back to high. The MZ-1X03's reading routine waits for
///     VBLK to fall, then counts CPU cycles while the bit is low,
///     yielding a 0-255 value.
///   - VBLK high (vertical blanking): bits 1-4 carry SW1/SW2 of
///     each stick (active-low: 0 = pressed).
///
/// All bits idle at 1 (active-low pull-ups). Sticks with
/// <see cref="StickState.Active"/> = false are treated as
/// disconnected — their bits stay at 1, so games like panic.mzf
/// that direct-read $E008 see "no input" rather than spurious
/// pulses or button presses.
///
/// Bit→stick mapping (per MZ-1X03 manual):
///   bit 1 = stick 1 X-axis pulse | stick 1 SW1
///   bit 2 = stick 1 Y-axis pulse | stick 1 SW2
///   bit 3 = stick 2 X-axis pulse | stick 2 SW1
///   bit 4 = stick 2 Y-axis pulse | stick 2 SW2
///   bits 5, 6 = unused (always 1)
/// </summary>
public sealed class Joystick
{
    public sealed class StickState
    {
        public bool Active;        // input source connected & feeding values
        public byte AxisX = 128;   // 0 = full-left, 128 = centre, 255 = full-right
        public byte AxisY = 128;   // 0 = full-up,   128 = centre, 255 = full-down
        public bool Sw1;           // pushbutton 1 currently pressed
        public bool Sw2;           // pushbutton 2 currently pressed
    }

    public StickState[] Sticks { get; } = new[] { new StickState(), new StickState() };

    public Z80Cpu? Cpu;

    // Pulse-low duration per axis-value unit. Calibrated against the
    // joystick-read routine in panic.mzf ($2220), which is the most
    // demanding consumer we have a full disassembly of. That routine,
    // after the VBLK falling edge, samples each axis bit at two fixed
    // cycle offsets — ~1490 and ~7390 Z80 cycles — and counts how many
    // samples caught the pulse still low (0 = axis low/left-up, 1 =
    // centre, 2 = axis high/right-down).
    //
    // For a centred axis (128) to read as "1" the pulse must end
    // between the two samples, and for a full-deflection axis (255) to
    // read as "2" it must still be low past 7390 cycles:
    //   128 * C  in (1490, 7390)  ->  C in (11.6, 57.7)
    //   255 * C  >   7390         ->  C >  29.0
    // The lower the value within that window, the more symmetric the
    // left/centre/right split, and the closer the BASIC JOY() function
    // (whose count loop is ~33 cycles/iteration) tracks 0..255. 33
    // sits just above panic's hard floor: full-right gives 255*33 =
    // 8415 cycles (1025-cycle / ~31-unit margin past the 7390 sample),
    // while centre gives 4224 — comfortably mid-window.
    //
    // Earlier guesses: 28 (too low — 255*28 = 7140 missed panic's
    // second sample, RIGHT/DOWN never detected) and 64 (too high — a
    // centred 128*64 = 8192 already passed the second sample, so centre
    // read as full RIGHT/DOWN).
    private const int CyclesPerCount = 33;
    private long _xPulseEnd0, _yPulseEnd0;
    private long _xPulseEnd1, _yPulseEnd1;

    /// <summary>
    /// Called from <c>MZ700.RunFrame</c> at the VBLK falling edge,
    /// matching the trigger event of the real 555 monostables.
    /// Snapshots each stick's axis values into pulse-end deadlines.
    /// </summary>
    public void OnVBlankFall(long currentCycle)
    {
        var s0 = Sticks[0];
        var s1 = Sticks[1];
        _xPulseEnd0 = currentCycle + (s0.Active ? s0.AxisX * CyclesPerCount : 0);
        _yPulseEnd0 = currentCycle + (s0.Active ? s0.AxisY * CyclesPerCount : 0);
        _xPulseEnd1 = currentCycle + (s1.Active ? s1.AxisX * CyclesPerCount : 0);
        _yPulseEnd1 = currentCycle + (s1.Active ? s1.AxisY * CyclesPerCount : 0);
    }

    /// <summary>
    /// Returns the current state of $E008 bits 1-6 (mask 0x7E). Bits 0
    /// and 7 are caller's responsibility (TEMPO, VBLANK).
    /// </summary>
    public byte GetPortBits(bool vblkHigh)
    {
        // All bits start at 1 (idle / pulled high / no joystick).
        byte v = 0x7E;
        var s0 = Sticks[0];
        var s1 = Sticks[1];
        long cyc = Cpu?.TotalCycles ?? 0;

        if (vblkHigh)
        {
            // Switch-multiplex phase. Active-low: pressed = 0.
            if (s0.Active && s0.Sw1) v &= unchecked((byte)~0x02);
            if (s0.Active && s0.Sw2) v &= unchecked((byte)~0x04);
            if (s1.Active && s1.Sw1) v &= unchecked((byte)~0x08);
            if (s1.Active && s1.Sw2) v &= unchecked((byte)~0x10);
        }
        else
        {
            // Axis-pulse phase. Bit is 0 while pulse is active.
            if (s0.Active && cyc < _xPulseEnd0) v &= unchecked((byte)~0x02);
            if (s0.Active && cyc < _yPulseEnd0) v &= unchecked((byte)~0x04);
            if (s1.Active && cyc < _xPulseEnd1) v &= unchecked((byte)~0x08);
            if (s1.Active && cyc < _yPulseEnd1) v &= unchecked((byte)~0x10);
        }
        return v;
    }
}
