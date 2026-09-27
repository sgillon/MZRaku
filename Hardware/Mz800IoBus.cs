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
///   $FC-$FF       Z80 PIO ($FC/$FD control A/B, $FE/$FF data A/B;
///                 PA4 = inverted PIT OUT0 → timer interrupt)
///
/// Status: PPI + PIT hot-wired in both mode dispatches; OUT $E0-$E6
/// and IN $E0/$E1 drive the bank latches (<see cref="MZ800Memory.HandleBankOut"/>
/// / <see cref="MZ800Memory.HandleBankIn"/>); CRTC + palette wired
/// (Phase 5); PIO control/data wired (Phase 6.0); PSG wired (Phase
/// 6.1); joystick reads return $FF (Phase 7).
/// </summary>
public sealed class Mz800IoBus : IIoBus
{
    public Ppi8255 Ppi = null!;
    public Pit8253 Pit = null!;
    public MZ800Memory Memory = null!;
    public Sound Sound = null!;
    public Z80Cpu Cpu = null!;
    public Z80Pio Pio = null!;
    public Sn76489 Psg = null!;
    /// <summary>CRTC status for IN $CE — supplied by the machine, which
    /// owns the beam position (see MZ800.CrtcStatus).</summary>
    public Func<byte>? CrtcStatus;

    // WF/RF ownership moved to MZ800Memory in Phase 5.2 and DMD
    // followed in Phase 5.6 (the renderer needs the raw byte now to
    // pick 320 vs 640 resolution) — MZ800Memory is the single source
    // of truth for every CRTC register byte.

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
    /// Optional log sink for PIO ($FC-$FF), PSG ($F2) and PPI
    /// ($D0-$D3 port-space) writes. Phase 6.0 diagnostic to capture
    /// how software sets up the interrupt path (PIO vector + control
    /// words, PPI PC0/PC2 mask bits) before we wire it. Populated only
    /// when --dump= is active. Capped at 4096 entries.
    /// </summary>
    public System.Text.StringBuilder? IntIoWriteLog;
    private int _intIoWriteLogEntries;
    private const int IntIoWriteLogCap = 4096;

    private void LogIntIoWrite(byte port, byte value)
        => LogIntIoNote($"PC=${Cpu.PC:X4} OUT (${port:X2}),${value:X2}  IM={Cpu.IM} I=${Cpu.I:X2}");

    /// <summary>Append a free-form line (e.g. an interrupt request) to
    /// <see cref="IntIoWriteLog"/>, sharing its cap.</summary>
    public void LogIntIoNote(string line)
    {
        if (IntIoWriteLog == null || _intIoWriteLogEntries >= IntIoWriteLogCap) return;
        _intIoWriteLogEntries++;
        IntIoWriteLog.AppendLine(line);
        if (_intIoWriteLogEntries == IntIoWriteLogCap)
            IntIoWriteLog.AppendLine($"[...cap {IntIoWriteLogCap} entries, further writes suppressed]");
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
    /// Z80 IN port — routed per tech-ref p. 6. Reads from $E0/$E1
    /// carry the CG-ROM / VRAM bank-switch side effect; the returned
    /// byte is discarded.
    /// </summary>
    public byte In(ushort port)
    {
        byte p = (byte)(port & 0xFF);

        // Memory bank control (IN $E0/$E1; $E2-$E6 undefined for IN).
        if (p >= 0xE0 && p <= 0xE6)
        {
            Memory.HandleBankIn((byte)(p - 0xE0));
            return 0xFF;
        }

        // 8255 PPI in MZ-800 mode ($D0-$D3).
        if (p >= 0xD0 && p <= 0xD3) return Ppi.Read(p - 0xD0);
        // 8253 PIT in MZ-800 mode ($D4-$D7).
        if (p >= 0xD4 && p <= 0xD7) return Pit.Read(p - 0xD4);

        // CRTC status ($CE IN) — beam-position bits from the machine's
        // raster model (Phase 7.2; bit meanings in MZ800.CrtcStatus).
        if (p == 0xCE) return CrtcStatus?.Invoke() ?? 0;

        // Joystick ports ($F0/$F1). No stick connected in Phase 1;
        // return $FF (all lines high = nothing pressed).
        if (p == 0xF0 || p == 0xF1) return 0xFF;

        // Z80 PIO data ports ($FE port A, $FF port B). Control ports
        // ($FC/$FD) are write-only.
        if (p == 0xFE || p == 0xFF) return Pio.ReadData(p == 0xFF);
        if (p == 0xFC || p == 0xFD) return 0xFF;

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

        // Memory bank control (OUT $E0-$E6, tech-ref p. 4-5).
        if (p >= 0xE0 && p <= 0xE6) { Memory.HandleBankOut((byte)(p - 0xE0)); return; }

        // 8255 PPI in MZ-800 mode ($D0-$D3).
        if (p >= 0xD0 && p <= 0xD3) { Ppi.Write(p - 0xD0, value); LogIntIoWrite(p, value); return; }
        // 8253 PIT in MZ-800 mode ($D4-$D7).
        if (p >= 0xD4 && p <= 0xD7) { Pit.Write(p - 0xD4, value); return; }

        // CRTC writes.
        if (p == 0xCC) { Memory.SetWfRegister(value); LogCrtcWrite(p, value, 0); return; }
        if (p == 0xCD) { Memory.SetRfRegister(value); LogCrtcWrite(p, value, 0); return; }
        if (p == 0xCE)
        {
            Memory.SetDmdRegister(value);
            LogCrtcWrite(p, value, 0);
            return;
        }
        if (p == 0xCF)
        {
            // Indirect CRTC register write. B register (in high byte of
            // port word per tech-ref p. 23) selects sub-register:
            //   B=1 SOF1 · B=2 SOF2 · B=3 SW · B=4 SSA · B=5 SEA (Phase 5.7)
            //   B=6 BCOL border colour (Phase 5.4)
            //   B=7 CKSW cursor/style (deferred — PCG cursor blink,
            //       low priority)
            byte b = (byte)((port >> 8) & 0xFF);
            switch (b)
            {
                case 1: Memory.SetSof1(value);       break;
                case 2: Memory.SetSof2(value);       break;
                case 3: Memory.SetSw(value);         break;
                case 4: Memory.SetSsa(value);        break;
                case 5: Memory.SetSea(value);        break;
                case 6: Memory.SetBorderColour(value); break;
                // B=0 and B=7+ silently drop (write-log still captures)
            }
            LogCrtcWrite(p, value, b);
            return;
        }

        // Palette write ($F0 OUT — same port as joystick-1 IN, direction
        // decides which device). Phase 5.4 wires this: high nibble is
        // the target slot (0-3 = pixel palette), low nibble is IRGB.
        if (p == 0xF0) { Memory.WritePalette(value); LogCrtcWrite(p, value, 0); return; }

        // SN76489 PSG ($F2 OUT, write-only). Phase 6.1.
        if (p == 0xF2) { LogIntIoWrite(p, value); Psg.Write(value); return; }

        // Z80 PIO: $FC/$FD control (A/B), $FE/$FF data (A/B). Phase
        // 6.0 — carries the PIT c0 → PA4 interrupt path.
        if (p >= 0xFC && p <= 0xFF)
        {
            LogIntIoWrite(p, value);
            if (p <= 0xFD) Pio.WriteControl(p == 0xFD, value);
            else Pio.WriteData(p == 0xFF, value);
            return;
        }

        // Anything else: silent no-op (real hardware would decode
        // nothing and drift on the bus).
    }
}
