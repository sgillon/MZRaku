using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Routes MZ-800 I/O and memory-mapped I/O to devices. See tech-ref
/// pp. 6-8 (I/O controller table). The MZ-800 exposes hardware on
/// both Z80 IN/OUT port space AND the $E000-$E00F memory-mapped
/// window (the latter only when in MZ-700 mode, matching MZ-700's
/// layout so the ROM's MZ-700 monitor works unchanged).
///
/// Port map (tech-ref p. 6):
///   $CC  OUT  CRTC WF (write format register)
///   $CD  OUT  CRTC RF (read format register)
///   $CE  OUT  CRTC DMD (display-mode register — bit 3 = MZ-700 mode)
///   $CE  IN   CRTC status read
///   $CF  OUT  CRTC indirect (SOF/SW/SSA/SEA/BCOL/CKSW, selected by B)
///   $D0-$D3       8255 PPI (MZ-800 mode; mapped to $E000-$E003 in MZ-700 mode)
///   $D4-$D7       8253 PIT (MZ-800 mode; mapped to $E004-$E007 in MZ-700 mode)
///   $E0-$E4  IN   memory bank control (side effect; data discarded)
///   $E5-$E6  IN   memory bank control (prohibited / return-to-previous)
///   $E008         TEMP/HBLK input + PIT C0 gate (MZ-700 mode only)
///   $F0     OUT   palette write
///   $F0     IN    joystick 1
///   $F1     IN    joystick 2
///   $F2     OUT   SN76489 PSG (write-only)
///   $FC-$FF       Z80 PIO (printer + joystick strobes)
///
/// Phase 1 status: PPI + PIT are hot-wired in both mode dispatches;
/// IN $E0-$E5 side-effects call <see cref="MZ800Memory.HandleBankSwitch"/>;
/// OUT $CE dispatches to <see cref="MZ800Memory.SetDmdRegister"/>.
/// CRTC other registers, palette, PSG, PIO, and joystick I/O are all
/// stubbed — writes accepted-and-dropped, reads return $FF. Those
/// come online with their respective phases (5, 5, 6, 7, 7).
/// </summary>
public sealed class Mz800IoBus : IIoBus
{
    public Ppi8255 Ppi = null!;
    public Pit8253 Pit = null!;
    public MZ800Memory Memory = null!;
    public Sound Sound = null!;
    public Z80Cpu Cpu = null!;

    // Phase 5 populates these; Phase 1 catches the writes so a slice
    // of the CRTC surface is at least visible in the debugger. WF/RF
    // ownership moved to MZ800Memory in Phase 5.2 — those registers
    // are used by the plane read/write paths, so keeping them on
    // Memory removes the null-safe IoBus?.WfRegister hop. DMD stays
    // here because its consumer (Memory.SetDmdRegister → Mz700Mode)
    // takes just the derived bit rather than the raw byte.
    public byte DmdRegister;

    /// <summary>
    /// Optional log sink for CRTC / palette register writes. Phase 5.0
    /// diagnostic to capture the sequence BASIC (and later MC games)
    /// programs into $CC/$CD/$CE/$CF/$F0 during cold-boot. See
    /// _mz800info/MZ800_VideoRendering_Research/00-current-state.md
    /// open questions. Populated only when --dump= is active. Capped
    /// at 4096 entries.
    /// </summary>
    public System.Text.StringBuilder? CrtcWriteLog;
    private int _crtcWriteLogEntries;
    private const int CrtcWriteLogCap = 4096;

    private void LogCrtcWrite(byte port, byte value, ushort b)
    {
        if (CrtcWriteLog == null || _crtcWriteLogEntries >= CrtcWriteLogCap) return;
        _crtcWriteLogEntries++;
        ushort pc = Cpu.PC;
        // $CF is indirect via B register; give the sub-reg selector its own field.
        if (port == 0xCF)
            CrtcWriteLog.AppendLine($"PC=${pc:X4} OUT (${port:X2}),${value:X2}  B=${b:X2}  [$CF indirect]");
        else
            CrtcWriteLog.AppendLine($"PC=${pc:X4} OUT (${port:X2}),${value:X2}");
        if (_crtcWriteLogEntries == CrtcWriteLogCap)
            CrtcWriteLog.AppendLine($"[...cap {CrtcWriteLogCap} entries, further writes suppressed]");
    }

    /// <summary>
    /// $E000-$E00F memory-mapped I/O window — MZ-700 mode only.
    /// Same shape as MZ-700's IoBus.MemIn (PPI at $E000-$E003, PIT
    /// at $E004-$E007, $E008 for TEMP/HBLK). Called from
    /// <see cref="MZ800Memory.Read"/> when Config is B_Mz700 and
    /// addr is in $E000-$E00F.
    /// </summary>
    public byte MemIn(ushort addr)
    {
        int off = addr & 0x000F;
        if (off <= 3) return Ppi.Read(off);
        if (off <= 7) return Pit.Read(off - 4);
        if (off == 8)
        {
            // MZ-700-mode $E008: TEMP bit + HBLK. Modelled the same
            // way MZ-700's IoBus does — TempoBit at D0 for MUSIC
            // duration polling. Joystick bits deliberately zeroed
            // in Phase 1 (Phase 7 wires the PIO joystick path).
            byte v = 0;
            if (Ppi.TempoBit) v |= 0x01;
            if ((Ppi.PortCIn & 0x80) != 0) v |= 0x80;   // VBLANK mirror
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
            // MZ-700-mode $E008 write: D0 controls PIT C0 gate (per
            // tech-ref p. 6 note). Model as the MZ-700 hard-gate for
            // now — real behaviour arrives with Phase 6 PSG work.
            Sound.HardGate = (value & 0x01) != 0;
            return;
        }
    }

    /// <summary>
    /// Z80 IN port — routed per tech-ref p. 6. Reads from $E0-$E5
    /// carry the memory-bank-switch side effect; the returned byte
    /// value is discarded by the CPU idiom `LD A,(nn)`.
    /// </summary>
    public byte In(ushort port)
    {
        byte p = (byte)(port & 0xFF);

        // Memory bank control ($E0-$E5 trigger; $E6 return-to-previous).
        if (p >= 0xE0 && p <= 0xE6)
        {
            Memory.HandleBankSwitch((byte)(p - 0xE0));
            return 0xFF;
        }

        // 8255 PPI in MZ-800 mode ($D0-$D3).
        if (p >= 0xD0 && p <= 0xD3) return Ppi.Read(p - 0xD0);
        // 8253 PIT in MZ-800 mode ($D4-$D7).
        if (p >= 0xD4 && p <= 0xD7) return Pit.Read(p - 0xD4);

        // CRTC status ($CE IN). Phase 5.3: return real VBLK bit
        // (D7) tracked by MZ800.RunFrame via Ppi.SetVBlank — same
        // signal used by $E008 mem-mapped in MZ-700 mode. HBLK (D6)
        // stays zero: we don't model per-scanline timing yet, and no
        // MC-game path exercised so far spin-waits on HBLK.
        // See research/04-read-format.md for the bit-layout guess
        // and open questions on the reserved bits.
        if (p == 0xCE)
        {
            byte status = 0;
            if ((Ppi.PortCIn & 0x80) != 0) status |= 0x80;
            return status;
        }

        // Joystick ports ($F0/$F1). No stick connected in Phase 1;
        // return $FF (all lines high = nothing pressed).
        if (p == 0xF0 || p == 0xF1) return 0xFF;

        // Z80 PIO ($FC-$FF). Phase 7 wires this properly; for now
        // return $FF so any polling loop sees "printer not ready".
        if (p >= 0xFC && p <= 0xFF) return 0xFF;

        return 0xFF;
    }

    /// <summary>
    /// Z80 OUT port — see <see cref="In"/> for the layout. CRTC's
    /// DMD register (OUT $CE) hooks into <see cref="MZ800Memory.SetDmdRegister"/>
    /// to flip MZ-700/MZ-800 mode. Other CRTC + palette + PSG + PIO
    /// writes get captured for future phases but aren't acted on yet.
    /// </summary>
    public void Out(ushort port, byte value)
    {
        byte p = (byte)(port & 0xFF);

        // 8255 PPI in MZ-800 mode ($D0-$D3).
        if (p >= 0xD0 && p <= 0xD3) { Ppi.Write(p - 0xD0, value); return; }
        // 8253 PIT in MZ-800 mode ($D4-$D7).
        if (p >= 0xD4 && p <= 0xD7) { Pit.Write(p - 0xD4, value); return; }

        // CRTC writes.
        if (p == 0xCC) { Memory.SetWfRegister(value); LogCrtcWrite(p, value, 0); return; }
        if (p == 0xCD) { Memory.SetRfRegister(value); LogCrtcWrite(p, value, 0); return; }
        if (p == 0xCE)
        {
            DmdRegister = value;
            Memory.SetDmdRegister(value);
            LogCrtcWrite(p, value, 0);
            return;
        }
        if (p == 0xCF)
        {
            // Indirect CRTC register write. B register (in high byte of
            // port word per tech-ref p. 23) selects sub-register:
            //   B=1 SOF1, B=2 SOF2, B=3 SW, B=4 SSA, B=5 SEA (Phase 5.7),
            //   B=6 BCOL border colour (Phase 5.4 — wired now),
            //   B=7 CKSW cursor/style (deferred).
            byte b = (byte)((port >> 8) & 0xFF);
            if (b == 6) Memory.SetBorderColour(value);
            LogCrtcWrite(p, value, b);
            return;
        }

        // Palette write ($F0 OUT — same port as joystick-1 IN, direction
        // decides which device). Phase 5.4 wires this: high nibble is
        // the target slot (0-3 = pixel palette), low nibble is IRGB.
        if (p == 0xF0) { Memory.WritePalette(value); LogCrtcWrite(p, value, 0); return; }

        // SN76489 PSG ($F2 OUT). Phase 6 wires this.
        if (p == 0xF2) return;

        // Z80 PIO ($FC-$FF). Phase 7 wires this.
        if (p >= 0xFC && p <= 0xFF) return;

        // Anything else: silent no-op (real hardware would decode
        // nothing and drift on the bus).
    }
}
