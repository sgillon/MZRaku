using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Routes MZ-80A memory-mapped I/O ($E000-$EFFF plus the $E200-$E2FF
/// scroll window) to the shared 8255 PPI / 8253 PIT device models and
/// to the machine's video / sound / memory subsystems.
///
/// Bit assignments follow Owner's Manual Table 3.1 (printed p.163):
///
///   $E000 W  D7 = reset cursor timer; D3-D0 = key strobe (0-9)
///   $E001 R  D7-D0 = keyboard row data (active-low, 10x8 matrix)
///   $E002 R  D7=V-Blank; D6=cursor-timer status; D5=cassette read data;
///            D4=cassette R/W status
///   $E002 W  D3=cassette motor; D2=timer interrupt mask;
///            D1=cassette write data; D0=V-Gate
///   $E003 W  PPI mode control byte
///   $E004 W  Set PIT counter 0
///   $E005 R/W  PIT counter 1 read / write
///   $E006 R/W  PIT counter 2 read / write
///   $E007 W  PIT mode control
///   $E008 R  D7 = tempo-timer status; D0 = H-Blank
///   $E008 W  D0 = sound ON/OFF (single-bit gate; no MZ-700-style NAND)
///   $E00C R  Swap monitor ROM to $C000
///   $E010 R  Restore monitor ROM to $0000
///   $E014 R  CRT display: normal
///   $E015 R  CRT display: reverse
///   $E200-$E2FF R  Set hardware scroll offset (low byte = 8-char units)
///
/// Compared to MZ-700's IoBus (in <see cref="IoBus"/>), $E002 bit
/// meanings are rearranged: cassette motor moves from D0 to D3, V-Gate
/// takes D0's slot, and INTMSK moves from D2 (MZ-700) to D2 (same
/// position on MZ-80A too, actually). $E008 loses the joystick and the
/// speaker-NAND dual-gate — a single bit does the sound gating job.
///
/// Phase 1 status: PPI + PIT are hot-wired. Video mode toggles
/// ($E014/$E015), hardware scroll ($E200-$E2FF), and sound gate
/// ($E008 W D0) are recognised but ignored at the device level — they
/// come online in Phases 2 and 5. The memory-swap side effects
/// ($E00C/$E010 R) are handled inside <see cref="MZ80AMemory"/>.
/// </summary>
public sealed class Mz80aIoBus : IIoBus
{
    public Ppi8255 Ppi = null!;
    public Pit8253 Pit = null!;
    public MZ80AMemory Memory = null!;
    public Sound Sound = null!;

    // Phase 2 will populate this with a video renderer; the video-mode
    // and scroll registers ($E014/$E015/$E200-$E2FF) toggle its state.
    public bool ReverseVideo;
    public int ScrollOffset;

    // Toggles on every $E008 read to unblock the SA-1510 boot polling
    // loop that reads bit 0 (H-Blank) and RRCAs it into carry. See
    // the comment in MemIn where it's used.
    private bool _hblankToggle;


    public byte MemIn(ushort addr)
    {
        // $E200-$E2FF: hardware scroll offset set. Reading any address
        // in this range latches the low byte as the scroll offset in
        // 8-character units. Returned byte value is unused by the
        // ROM (LD A,(nn) discards the loaded byte in this idiom).
        if (addr >= 0xE200 && addr <= 0xE2FF)
        {
            ScrollOffset = addr & 0xFF;
            return 0xFF;
        }

        // Video-mode toggles.
        if (addr == 0xE014) { ReverseVideo = false; return 0xFF; }
        if (addr == 0xE015) { ReverseVideo = true; return 0xFF; }

        // Memory-swap toggles are handled by MZ80AMemory itself (the
        // side effect must fire even if this method never runs); the
        // returned data byte is discarded by the ROM's LD A,(nn).
        if (addr == 0xE00C || addr == 0xE010) return 0xFF;

        // PPI + PIT + $E008 status live in the $E000-$E008 block.
        int off = addr & 0x00FF;
        if (off <= 3) return Ppi.Read(off);            // $E000-$E003 PPI
        if (off <= 7) return Pit.Read(off - 4);        // $E004-$E007 PIT
        if (off == 8)
        {
            // $E008 R: D7 = tempo-timer status, D0 = H-Blank.
            //
            // SA-1510 has a tight polling loop at $02DB that reads
            // $E008 and RRCAs bit 0 into carry, looping while
            // carry is clear — i.e. waiting for H-Blank to go HIGH.
            // On real hardware the H-Blank signal is a 15.72 kHz
            // square-ish pulse; we don't model raster timing at
            // sub-instruction granularity, so simulate the wait
            // exiting by toggling H-Blank on every read. The next
            // read observes the opposite state and the busy-loop
            // completes within one iteration. Coarse, but enough
            // for boot polls; real per-scanline modelling can wait
            // until something needs the accurate rate.
            _hblankToggle = !_hblankToggle;
            byte v = 0;
            if (Ppi.TempoBit) v |= 0x80;
            if (_hblankToggle) v |= 0x01;
            return v;
        }
        return 0xFF;
    }

    public void MemOut(ushort addr, byte value)
    {
        int off = addr & 0x00FF;
        if (off <= 3) { Ppi.Write(off, value); return; }
        if (off <= 7) { Pit.Write(off - 4, value); return; }
        if (off == 8)
        {
            // $E008 W: D0 = sound gate (hard gate in front of the
            // audio amp — no MZ-700-style dual-NAND, just one bit).
            Sound.HardGate = (value & 0x01) != 0;
            return;
        }
        // Writes to $E014, $E015, $E200-$E2FF etc. are undefined on
        // real hardware (those addresses are read-triggered) — ignore.
    }

    // MZ-80A doesn't use Z80 IN/OUT port I/O for anything the ROM
    // depends on (no port-based bank switching like MZ-700's $E0-$E5).
    public byte In(ushort port) => 0xFF;
    public void Out(ushort port, byte value) { }
}
