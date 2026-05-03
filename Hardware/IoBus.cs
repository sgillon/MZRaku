using System;
using MZ700Emul.Z80;

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

    public byte MemIn(ushort addr)
    {
        int off = addr & 0x000F;
        if (off <= 3) return Ppi.Read(off);
        if (off <= 7) return Pit.Read(off - 4);
        if (off == 8)
        {
            // $E008 read: "Tempo, joystick, HBLNK input" via LS367 buffer
            // (per service manual). Bit 0 is the TEMPO signal driven by
            // the MZ-700's 555/556 cursor oscillator (~3 Hz), shared with
            // the cursor-blink input on PPI PortC. S-BASIC's MUSIC polls
            // this signal — the fact that BASIC's MUSIC was 10-20× too
            // slow under the previous C1.OUT (1 Hz) wiring confirms the
            // tempo source is the cursor osc, not the 8253.
            // Bit 7 piggybacks the VBLANK signal also tracked on PortCIn.
            byte v = 0;
            if (Ppi.TempoBit) v |= 0x01;
            if ((Ppi.PortCIn & 0x80) != 0) v |= 0x80;
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
