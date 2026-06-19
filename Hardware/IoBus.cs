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
    public Sound Sound = null!;

    /// <summary>
    /// Fires on every CPU write to $E008. Value is the data byte
    /// written. Consumed by the Sound Diagnostic so $E008 traffic
    /// surfaces alongside PIT and PC3 events.
    /// </summary>
    public event Action<byte>? OnE008Write;

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
        if (off == 8)
        {
            // $E008 write: D0 latches into IC7E LS74 FF1 (CK fires on
            // every write to the CSE2-decoded range, of which $E008
            // is one address). FF1.Q drives the speaker-amp NAND's
            // second input — D0=1 lets C0.OUT through to the
            // amplifier, D0=0 forces silence regardless of C0. This
            // is how the ROM's boot tone ends and how S-BASIC's
            // MUSIC produces discrete notes. See Mz700SoundReference
            // (SpeakerNandGate enum) for the schematic citation.
            Sound.HardGate = (value & 0x01) != 0;
            OnE008Write?.Invoke(value);
            return;
        }
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
