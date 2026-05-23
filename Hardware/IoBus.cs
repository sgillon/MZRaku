using System;
using Z80Core;

namespace MZ700Emul.Hardware;

/// <summary>
/// Routes memory-mapped I/O (0xE000-0xE00F) to 8255 PPI, 8253 PIT, and misc.
/// Also stubs Z80 IN/OUT port access (MZ-700 does not use port I/O).
/// </summary>
public sealed class IoBus : IIoBus
{
    public Ppi8255 Ppi = null!;
    public Pit8253 Pit = null!;
    public MZ700Memory Memory = null!;
    public Joystick Joystick = null!;

    public byte MemIn(ushort addr)
    {
        int off = addr & 0x000F;
        if (off <= 3) return Ppi.Read(off);
        if (off <= 7) return Pit.Read(off - 4);
        if (off == 8)
        {
            // $E008 read: "Tempo, joystick, HBLNK input" via LS367 buffer
            // (per service manual).
            //   bit 0   = TEMPO (cursor-osc / 555 timer signal at ~50 Hz),
            //             polled by S-BASIC's MUSIC for note duration.
            //   bits 1-6 = MZ-1X03 joystick lines (active-low, multiplexed
            //              by VBLK between axis pulses and switch states —
            //              see Hardware/Joystick.cs).
            //   bit 7   = VBLANK signal (also tracked on PPI PortC PC7).
            bool vblkHigh = (Ppi.PortCIn & 0x80) != 0;
            byte v = Joystick.GetPortBits(vblkHigh);
            if (Ppi.TempoBit) v |= 0x01;
            if (vblkHigh) v |= 0x80;
            return v;
        }
        return 0xFF;
    }

    public void MemOut(ushort addr, byte value)
    {
        int off = addr & 0x000F;
        if (off <= 3) { Ppi.Write(off, value); return; }
        if (off <= 7) { Pit.Write(off - 4, value); return; }
        // $E008 write on MZ-700 is used (in some ROM versions) to clear the
        // maskable-interrupt latch; in our simplified IRQ model we just drop it.
    }

    // Z80 IN/OUT: on MZ-700 most port I/O is unused, except ports $E0-$E5
    // which mirror the memory-mapped bank-switch commands at $E010-$E015.
    // S-BASIC (1Z-013B) uses OUT ($E0), A to unmap the monitor ROM.
    public byte In(ushort port) => 0xFF;
    public void Out(ushort port, byte value)
    {
        byte p = (byte)(port & 0xFF);
        if (p >= 0xE0 && p <= 0xE5)
        {
            Memory.HandleBankSwitch((byte)(p - 0xE0));
        }
    }
}
