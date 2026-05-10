using MZ700Emul.Z80;

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

    // Cycle counts at which each axis bit transitions back to high. The
    // MZ-1X03 reading routine times the low pulse with a tight Z80 loop
    // (INC DE; BIT n,(HL); JP Z,...) — ~28 t-states per iteration. So
    // pulseCycles = axisValue * 28 makes BASIC's JOY(0..3) return the
    // axis value back.
    private const int CyclesPerCount = 28;
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
