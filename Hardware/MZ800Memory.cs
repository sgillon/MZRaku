using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-800 memory + banking (per tech-ref pp. 3-5, 24). The
/// MZ-800 has two operating modes, MZ-800 and MZ-700, plus a
/// per-mode set of bank configurations selected by IN reads at
/// I/O ports $E0-$E5 (data returned is discarded; the read is the
/// trigger). The mode bit itself flips via OUT ($CE),A — bit 3 of
/// the DMD register (see <see cref="Mz800IoBus"/>).
///
/// Full behaviour would need every one of the six-per-mode bank
/// combinations from the tech-ref table on p. 4, but Phase 1 only
/// wires the paths the cold-boot flow actually walks:
///
///   1. Power-on: config (a), MZ-800 mode. ROM at $0000-$0FFF (MZ-700
///      monitor), CG-ROM at $1000-$1FFF, DRAM at $2000-$7FFF, VRAM at
///      $8000-$BFFF (320×200 bitmap), DRAM $C000-$DFFF, MON (1Z-016B)
///      at $E000-$FFFF. CPU boots at $0000 which is `JP $E800` —
///      jumps into the MZ-800 IPL at ROM offset $2800.
///
///   2. IPL sets MZ-700 mode via OUT ($CE),A with A=$08 (DMD bit 3).
///
///   3. IPL flips to config (c) via IN ($E1),A — puts CG-ROM at
///      $1000-$1FFF and VRAM (PCG area) at $D000-$DFFF, so the IPL
///      can copy CG data into PCG.
///
///   4. IPL flips back to config (b) via IN ($E0),A — restores
///      text/attribute VRAM at $D000-$D7FF/$D800-$DFFF, DRAM at
///      $1000-$CFFF, I/O at $E000-$E00F, MON at $E010-$FFFF. CPU
///      resumes the monitor at $0000.
///
///   5. Monitor writes the '*' prompt to VRAM at $D000.
///
/// So Phase 1 supports three configs — (a) at reset, (c) for the
/// PCG copy, (b) for normal MZ-700-mode operation — plus DRAM-only
/// config (d) which BASIC uses. The remaining transitions (IN $E2/$E3/
/// $E4/$E5) get modeled well enough not to crash but aren't verified
/// against a specific boot path yet. Phase 4 (BASIC + cassette) is
/// the next flow that will exercise them.
///
/// Underlying VRAM storage: real hardware has one 16 KB VRAM chip
/// with different address decodes per mode (see tech-ref p. 13). For
/// Phase 1 we model separate 2 KB Vram + 2 KB Aram buffers matching
/// MZ-700 semantics — the PCG copy in step 3 above lands in these
/// same buffers because the CPU-facing address ($D000-$DFFF) is the
/// same. Phase 5 refactors to a plane-oriented model when the CRTC
/// bitmap renderer arrives.
/// </summary>
public sealed class MZ800Memory : IMemory
{
    // 16 KB combined ROM: $0000-$0FFF MZ-700 monitor, $1000-$1FFF CG,
    // $2000-$3FFF MZ-800 IPL + monitor + BASIC-IOCS. See tech-ref p. 24
    // "4-10 ROM configuration". The IPL's entry point at CPU $E800 is
    // ROM offset $2800.
    public byte[] Rom = new byte[0x4000];

    // Full 64 KB backing DRAM. All writes to non-ROM/non-IO regions
    // land here; reads from RAM-visible regions pull from here.
    public byte[] Ram = new byte[0x10000];

    // 2 KB text VRAM + 2 KB attribute VRAM, visible at $D000-$DFFF in
    // MZ-700 mode config (b). Same shape as MZ700Memory.Vram/Aram so
    // Phase 2's Mz800Video can fork Mz700Video with minimal change.
    public byte[] Vram = new byte[0x800];
    public byte[] Aram = new byte[0x800];

    // MZ-800 native-mode bitmap VRAM — four bit-planes of 8 KB each.
    // Phase 5.1: added as plane-oriented storage so writes to CPU
    // $8000-$BFFF in MZ-800 mode land here (routed via WF register)
    // instead of vanishing into Ram[]. Phase 5.0 dump analysis
    // (research/07-basic-rendering-path.md) proved BASIC writes ~4 KB+
    // to this window with WF=$83 (REPLACE, Frame A, planes I+II) and
    // was silently absorbed by the old D_AllRam Ram[]-only path.
    //
    // Sizing: 8 KB per plane covers 320×200 bitmap (8000 bytes) with
    // headroom, and 640×200 (16000 bytes) via two-planes-per-scanline
    // partitioning per tech-ref pp. 10-13. Frame A = planes I+II,
    // Frame B = planes III+IV. Phase 5.5 renderer reads these to
    // paint the display; 5.7 hardware-scroll retreads the addressing.
    public byte[] PlaneI   = new byte[0x2000];
    public byte[] PlaneII  = new byte[0x2000];
    public byte[] PlaneIII = new byte[0x2000];
    public byte[] PlaneIV  = new byte[0x2000];

    // Bank-configuration enum. Names follow the tech-ref diagram
    // letters (a,b,c,d). Extra configs the tech-ref table hints at
    // (e.g. after IN $E1 in MZ-800 mode) can be added when a specific
    // boot path lands on them.
    public enum BankConfig
    {
        A_Power,     // Power-on / after Reset (MZ-800 mode default)
        B_Mz700,     // MZ-700-mode operating: ROM+DRAM+VRAM+IO+ROM
        C_PcgWrite,  // MZ-700-mode PCG update: ROM+CGROM+DRAM+VRAM+ROM
        D_AllRam,    // BASIC/user code: 64 KB DRAM (Phase 5.1: $8000-$BFFF routes to bitmap planes in MZ-800 mode)
    }

    public BankConfig Config = BankConfig.A_Power;

    /// <summary>
    /// Which display mode the machine is in. Set by DMD-register
    /// writes at OUT ($CE),A — bit 3 of the value flips the mode
    /// (DMD3=1 → MZ-700 mode, DMD3=0 → MZ-800 mode). See tech-ref
    /// p. 17 Table-1.
    /// </summary>
    public bool Mz700Mode;

    public Mz800IoBus? IoBus;
    public Z80Cpu? Cpu;

    /// <summary>
    /// CRTC Write Format register — set by OUT ($CC),A. Bit layout
    /// (tech-ref pp. 10-13): D7-D5 = write MODE (000 SINGLE / 001 XOR /
    /// 010 OR / 011 RESET / 100 REPLACE / 101 PSET); D4 = FRAME
    /// (0 = Frame A, planes I+II; 1 = Frame B, planes III+IV);
    /// D3-D0 = per-plane enable (D0 plane I, D1 plane II, D2 plane III,
    /// D3 plane IV). Consumed by <see cref="WriteVideoPlane"/> on every
    /// CPU write to $8000-$BFFF in MZ-800 mode.
    /// </summary>
    public byte WfRegister;

    /// <summary>
    /// CRTC Read Format register — set by OUT ($CD),A. Selects which
    /// plane <see cref="ReadVideoPlane"/> returns and (bit 4) whether
    /// SEARCH mode is active. Phase 5.2 captures the value; Phase 5.3
    /// wires the read decode.
    /// </summary>
    public byte RfRegister;

    /// <summary>
    /// Phase 5.2: OUT ($CC),A hook. Ownership of the WF register byte
    /// moved from Mz800IoBus to Memory in 5.2 because Memory is the
    /// consumer. Pass-through today — kept as a method so future phases
    /// (renderer cache invalidation, Phase 5.7 scroll) can hook cleanly.
    /// </summary>
    public void SetWfRegister(byte value) => WfRegister = value;

    /// <summary>Phase 5.2 companion to <see cref="SetWfRegister"/> for the RF register.</summary>
    public void SetRfRegister(byte value) => RfRegister = value;

    /// <summary>
    /// 4-entry pixel palette. Each byte holds a 4-bit IRGB code
    /// (D3=I intensity, D2=R, D1=G, D0=B) — same layout as CGA/EGA.
    /// A 2-plane pixel decodes to <c>(planeI_bit &lt;&lt; 1) | planeII_bit</c>
    /// = colour code 0..3 which indexes here. Phase 5.5 renderer
    /// resolves each entry through <see cref="Mz800Video.IrgbToArgb"/>.
    /// See tech-ref p. 22 and research/05-palette.md.
    /// </summary>
    public byte[] Palette = new byte[4];

    /// <summary>
    /// Border colour — 4-bit IRGB same shape as a palette entry.
    /// Written via OUT ($CF),A with B=6 per plan (tech-ref p. 23).
    /// Real hardware wiring TBC; the ambiguity between $CF B=6 and
    /// an alternative $F0 high-nibble=4 encoding is captured in
    /// research/05-palette.md. Phase 5.5 renderer paints this
    /// around the 320×200 active area.
    /// </summary>
    public byte BorderColour;

    /// <summary>Phase 5.4: OUT ($F0),A palette write. High nibble is the
    /// target slot (0-3 = pixel palette; 4-15 currently no-op pending
    /// tech-ref clarification), low nibble is the IRGB value.</summary>
    public void WritePalette(byte value)
    {
        int index = (value >> 4) & 0x0F;
        byte irgb = (byte)(value & 0x0F);
        if (index < Palette.Length) Palette[index] = irgb;
        // High-nibble 4-15 is captured in the CRTC write log; not
        // routed to any state today. Phase 5.5 visual verification
        // decides whether index 4 is border colour (see research doc).
    }

    /// <summary>Phase 5.4: OUT ($CF),A with B=6 border-colour write.
    /// Low nibble is IRGB, high nibble unused per tech-ref p. 23.</summary>
    public void SetBorderColour(byte value) => BorderColour = (byte)(value & 0x0F);

    /// <summary>
    /// Optional log sink (mirror of MZ700Memory.BankSwitchLog).
    /// Useful during Phase 1 bring-up to see the IPL's bank-switch
    /// sequence in the debugger.
    /// </summary>
    public System.Text.StringBuilder? BankSwitchLog;

    /// <summary>
    /// Optional log sink for CPU writes into the MZ-800-mode bitmap
    /// VRAM window ($8000-$BFFF). Phase 5.0 diagnostic to answer
    /// "does BASIC actually write here, and if so what WF register
    /// is active?" — see _mz800info/MZ800_VideoRendering_Research/
    /// 00-current-state.md open questions. Populated only when
    /// --dump= is active; the null-conditional check inside Write()
    /// keeps the hot path free otherwise. Capped at 4096 entries so
    /// a runaway boot doesn't OOM the trace.
    /// </summary>
    public System.Text.StringBuilder? VideoWriteLog;
    private int _videoWriteLogEntries;
    private const int VideoWriteLogCap = 4096;

    /// <summary>
    /// Phase 5.1: does this address in the current mode/config route
    /// through the bitmap-VRAM plane storage rather than DRAM? True
    /// when the CPU is in MZ-800 mode and the address falls in the
    /// $8000-$BFFF window. In MZ-700 mode this range is normal DRAM
    /// (config B_Mz700 keeps VRAM at $D000-$DFFF instead).
    ///
    /// Applies to both A_Power and D_AllRam — the two MZ-800-mode
    /// configs BASIC and the IPL land in. Our tech-ref comment on
    /// D_AllRam said "VRAM windows disabled" but Phase 5.0 dump
    /// analysis proved BASIC treats $8000-$BFFF as bitmap VRAM even
    /// in D_AllRam. Since D_AllRam is where BASIC lives, plane
    /// routing has to work there.
    /// </summary>
    private bool RoutesToBitmapVram(ushort addr)
        => !Mz700Mode && addr >= 0x8000 && addr <= 0xBFFF
           && (Config == BankConfig.A_Power || Config == BankConfig.D_AllRam);

    /// <summary>
    /// Phase 5.2 write path for the MZ-800-mode bitmap-VRAM window.
    /// Honours all six WF write modes (tech-ref pp. 10-13):
    ///
    ///   000 SINGLE  — write value to each enabled plane (treated as
    ///                 REPLACE for now; the SINGLE/REPLACE distinction
    ///                 in the tech-ref is subtle and worth re-reading
    ///                 in Phase 5.5 verification if a game misbehaves)
    ///   001 XOR     — plane_byte ^= value
    ///   010 OR      — plane_byte |= value
    ///   011 RESET   — plane_byte &amp;= ~value  (clear bits where value=1)
    ///   100 REPLACE — plane_byte = value
    ///   101 PSET    — colour-code write via a separate colour register
    ///                 (deferred; needs a model we don't have yet — see
    ///                 research/03-write-format.md)
    ///   110 / 111   — prohibited per tech-ref; silent no-op
    ///
    /// FRAME (D4) selects the plane pair:
    ///   0 → Frame A (planes I + II, enabled by D0/D1)
    ///   1 → Frame B (planes III + IV, enabled by D2/D3)
    ///
    /// Address decode: plane offset = addr - $8000. Only the lower
    /// 8000 bytes of the 16 KB window are meaningful for 320×200
    /// (40 cols × 200 rows); writes past offset $1FFF are dropped
    /// (Phase 5.6 revisits for 640×200).
    ///
    /// Cold-boot fallback: WF=$00 decodes as SINGLE with no planes
    /// enabled — semantically a no-op. Fall back to REPLACE plane I
    /// so the very first writes (before any code programs WF) land
    /// somewhere the debugger can see them.
    /// </summary>
    private void WriteVideoPlane(ushort addr, byte value)
    {
        int offset = addr - 0x8000;
        if (offset < 0 || offset >= 0x2000) return;

        if (WfRegister == 0) { PlaneI[offset] = value; return; }

        int mode = (WfRegister >> 5) & 0x07;
        bool frameB = (WfRegister & 0x10) != 0;

        byte[] plane0, plane1;
        bool enable0, enable1;
        if (!frameB)
        {
            plane0 = PlaneI;   enable0 = (WfRegister & 0x01) != 0;
            plane1 = PlaneII;  enable1 = (WfRegister & 0x02) != 0;
        }
        else
        {
            plane0 = PlaneIII; enable0 = (WfRegister & 0x04) != 0;
            plane1 = PlaneIV;  enable1 = (WfRegister & 0x08) != 0;
        }

        switch (mode)
        {
            case 0b000: // SINGLE (treated as REPLACE pending 5.5 verification)
            case 0b100: // REPLACE
                if (enable0) plane0[offset] = value;
                if (enable1) plane1[offset] = value;
                break;
            case 0b001: // XOR
                if (enable0) plane0[offset] ^= value;
                if (enable1) plane1[offset] ^= value;
                break;
            case 0b010: // OR
                if (enable0) plane0[offset] |= value;
                if (enable1) plane1[offset] |= value;
                break;
            case 0b011: // RESET
                if (enable0) plane0[offset] &= (byte)~value;
                if (enable1) plane1[offset] &= (byte)~value;
                break;
            case 0b101: // PSET — deferred
            default:    // 110 / 111 prohibited
                break;
        }
    }

    /// <summary>
    /// Phase 5.3 read path for the MZ-800-mode bitmap-VRAM window.
    /// Honours the RF register (tech-ref pp. 13-14):
    ///
    ///   D4 = 0 → single-plane read. Low nibble is per-plane enables
    ///           (D0=I, D1=II, D2=III, D3=IV), same convention as WF.
    ///           First-enabled plane wins if multiple bits set.
    ///   D4 = 1 → SEARCH mode: return a bitmask where each bit=1 marks
    ///           a pixel whose across-plane colour code matches a
    ///           search-colour register. Used by MC games for
    ///           collision detection / sprite masking. Deferred —
    ///           returns $FF today. Revisit when an MC game exercises
    ///           it and the tech-ref colour-register semantics are
    ///           settled (see research/04-read-format.md).
    ///
    /// Cold-boot fallback: RF=$00 decodes as single-plane with no
    /// enables set — semantically "no plane". Fall back to PlaneI
    /// so any read before the IPL programs RF (WF=$00 case too)
    /// still returns something the CPU can work with.
    ///
    /// Off-window addresses (past the plane storage size) return
    /// $FF (bus-idle).
    /// </summary>
    private byte ReadVideoPlane(ushort addr)
    {
        int offset = addr - 0x8000;
        if (offset < 0 || offset >= 0x2000) return 0xFF;

        if (RfRegister == 0) return PlaneI[offset];         // cold-boot fallback
        if ((RfRegister & 0x10) != 0) return 0xFF;          // SEARCH mode - deferred

        if ((RfRegister & 0x01) != 0) return PlaneI[offset];
        if ((RfRegister & 0x02) != 0) return PlaneII[offset];
        if ((RfRegister & 0x04) != 0) return PlaneIII[offset];
        if ((RfRegister & 0x08) != 0) return PlaneIV[offset];
        return 0xFF;
    }

    public byte Read(ushort addr)
    {
        switch (Config)
        {
            case BankConfig.A_Power:
                // MZ-800 mode power-on layout (tech-ref p. 3 config a)
                if (addr < 0x1000) return Rom[addr];                    // MZ-700 monitor
                if (addr < 0x2000) return Rom[addr];                    // CG ROM ($1000-$1FFF)
                if (addr >= 0xE000) return Rom[0x2000 + (addr - 0xE000)]; // MZ-800 IPL/monitor (1Z-016B)
                if (RoutesToBitmapVram(addr)) return ReadVideoPlane(addr);
                return Ram[addr];                                       // DRAM elsewhere

            case BankConfig.B_Mz700:
                // MZ-700-mode operating layout (tech-ref p. 3 config b)
                if (addr < 0x1000) return Rom[addr];                    // MZ-700 monitor
                if (addr >= 0xD000 && addr <= 0xD7FF) return Vram[addr - 0xD000];
                if (addr >= 0xD800 && addr <= 0xDFFF) return Aram[addr - 0xD800];
                if (addr >= 0xE000 && addr <= 0xE00F)
                    return IoBus?.MemIn(addr) ?? 0xFF;
                if (addr >= 0xE010) return Rom[0x2000 + (addr - 0xE000)];
                return Ram[addr];

            case BankConfig.C_PcgWrite:
                // MZ-700-mode PCG-update layout (tech-ref p. 3 config c).
                // CG-ROM at $1000-$1FFF is visible; $D000-$DFFF still
                // routes to VRAM/ARAM (same underlying buffers as (b))
                // so the IPL's copy-from-$1000-to-$D000 loop lands in
                // the buffers Phase 2's renderer will draw from.
                if (addr < 0x1000) return Rom[addr];
                if (addr < 0x2000) return Rom[addr];                    // CG-ROM window
                if (addr >= 0xD000 && addr <= 0xD7FF) return Vram[addr - 0xD000];
                if (addr >= 0xD800 && addr <= 0xDFFF) return Aram[addr - 0xD800];
                if (addr >= 0xE000) return Rom[0x2000 + (addr - 0xE000)];
                return Ram[addr];

            case BankConfig.D_AllRam:
                // All-DRAM layout (BASIC / large user code). ROM banked
                // out entirely. Phase 5.1: the $8000-$BFFF window is
                // routed through the bitmap-VRAM planes when in MZ-800
                // mode — Phase 5.0 dump analysis proved BASIC treats it
                // as bitmap VRAM even here (not "disabled" as originally
                // documented). MZ-700 mode inside D_AllRam still sees
                // $8000-$BFFF as DRAM.
                if (RoutesToBitmapVram(addr)) return ReadVideoPlane(addr);
                return Ram[addr];
        }
        return Ram[addr];
    }

    public void Write(ushort addr, byte value)
    {
        // Phase 5.0 diagnostic: log every write to the MZ-800 bitmap
        // VRAM window in every config, so we can settle whether BASIC
        // does write here (currently absorbed into Ram[] by D_AllRam).
        // Deliberately outside the switch so it fires for every mode.
        if (VideoWriteLog != null
            && addr >= 0x8000 && addr <= 0xBFFF
            && _videoWriteLogEntries < VideoWriteLogCap)
        {
            _videoWriteLogEntries++;
            ushort pc = Cpu != null ? Cpu.PC : (ushort)0;
            byte wf = WfRegister;
            VideoWriteLog.AppendLine(
                $"PC=${pc:X4} W ${addr:X4}=${value:X2} cfg={Config} " +
                $"mode={(Mz700Mode ? "MZ700" : "MZ800")} WF=${wf:X2}");
            if (_videoWriteLogEntries == VideoWriteLogCap)
                VideoWriteLog.AppendLine($"[...cap {VideoWriteLogCap} entries, further writes suppressed]");
        }

        // Writes to the ROM window always land in RAM beneath (same
        // pattern as MZ-700 / MZ-80A — the ROM is read-only and RAM
        // captures the writes for the future all-RAM state).
        switch (Config)
        {
            case BankConfig.A_Power:
                if (RoutesToBitmapVram(addr)) { WriteVideoPlane(addr, value); return; }
                if (addr >= 0xE000)
                {
                    // MZ-800 mode: writes to $E000-$FFFF go to RAM
                    // beneath the MON-ROM.
                    Ram[addr] = value;
                    return;
                }
                Ram[addr] = value;
                return;

            case BankConfig.B_Mz700:
                if (addr < 0x1000) { Ram[addr] = value; return; }        // ROM shadow
                if (addr >= 0xD000 && addr <= 0xD7FF) { Vram[addr - 0xD000] = value; return; }
                if (addr >= 0xD800 && addr <= 0xDFFF) { Aram[addr - 0xD800] = value; return; }
                if (addr >= 0xE000 && addr <= 0xE00F) { IoBus?.MemOut(addr, value); return; }
                if (addr >= 0xE010) { Ram[addr] = value; return; }       // ROM shadow
                Ram[addr] = value;
                return;

            case BankConfig.C_PcgWrite:
                if (addr < 0x1000) { Ram[addr] = value; return; }
                if (addr >= 0x1000 && addr < 0x2000) { Ram[addr] = value; return; } // CG-ROM shadow
                if (addr >= 0xD000 && addr <= 0xD7FF) { Vram[addr - 0xD000] = value; return; }
                if (addr >= 0xD800 && addr <= 0xDFFF) { Aram[addr - 0xD800] = value; return; }
                if (addr >= 0xE000) { Ram[addr] = value; return; }       // ROM shadow
                Ram[addr] = value;
                return;

            case BankConfig.D_AllRam:
                if (RoutesToBitmapVram(addr)) { WriteVideoPlane(addr, value); return; }
                Ram[addr] = value;
                return;
        }
    }

    /// <summary>
    /// Handle a bank-switch trigger from an IN ($E0-$E5) read. Command
    /// is the low nibble (0-5). The full tech-ref table (p. 4) has
    /// per-mode entries for each command; Phase 1 wires the boot-path
    /// transitions and stubs the rest so unknown commands are visible
    /// in the log rather than silently corrupting state.
    ///
    /// Boot-path transitions this method handles (tech-ref p. 5,
    /// "Memory Bank Control" table for IN reads):
    ///   $E0  MZ-700 mode: → (c)  — expose CG-ROM at $1000, PCG VRAM at $C000
    ///   $E1  MZ-700 mode: → (b)  — restore DRAM at $1000 and $C000
    ///   $E1  MZ-800 mode: → (d)  — all DRAM (used by BASIC per feasibility)
    ///   $E4  either mode: → default  — power-on-like restore
    ///
    /// Phase 2.5 fix (2026-08-28): $E0 and $E1 were swapped, causing
    /// the IPL's LDIR at $E8B4 to copy from DRAM (zeros) instead of
    /// CG-ROM, and — worse — the subsequent CALL $001B (GETL) ran
    /// with the stack ($10DE-$10F0) inside the CG-ROM window, so
    /// every RET popped a font byte as the return address and
    /// re-entered the MZ-700 monitor's cold-boot init at $007C in a
    /// tight infinite loop. Swapping the two lines fixes both.
    /// </summary>
    public void HandleBankSwitch(byte cmd)
    {
        var prev = Config;
        var prevMode = Mz700Mode;

        switch (cmd)
        {
            case 0x00:  // $E0
                // MZ-700 mode: expose CG-ROM at $1000, VRAM (PCG) at $C000
                // so the IPL's LDIR at $E8B4 copies CG-ROM into PCG.
                if (Mz700Mode) Config = BankConfig.C_PcgWrite;
                // In MZ-800 mode, $E0 puts DRAM at $0000-$7FFF per the
                // tech-ref table. Not on a Phase-1 boot path; log only.
                break;
            case 0x01:  // $E1
                // MZ-700 mode: revert the $E0 CG-ROM windowing — $1000
                // and $C000 return to DRAM. Stack works normally again.
                // MZ-800 mode: full DRAM (used by BASIC per feasibility).
                if (Mz700Mode) Config = BankConfig.B_Mz700;
                else Config = BankConfig.D_AllRam;
                break;
            case 0x02:  // $E2
                // MON-ROM back at $0000-$0FFF only. On the boot path we
                // start with ROM already visible, so this is a no-op
                // relative to config (b)/(c).
                if (Mz700Mode && Config == BankConfig.D_AllRam)
                    Config = BankConfig.B_Mz700;
                break;
            case 0x03:  // $E3
                // MZ-700 mode: put VRAM+MON back at $D000-$FFFF.
                // Effectively means "back to config (b)".
                if (Mz700Mode) Config = BankConfig.B_Mz700;
                break;
            case 0x04:  // $E4
                // Default power-on-like restore for the current mode.
                Config = Mz700Mode ? BankConfig.B_Mz700 : BankConfig.A_Power;
                break;
            case 0x05:  // $E5
                // Tech-ref lists this as "prohibited" in both modes.
                // Silent no-op; log so an accidental hit surfaces.
                break;
            case 0x06:  // $E6
                // "Return to state before prohibited" — treat as no-op
                // for Phase 1.
                break;
        }

        if (BankSwitchLog != null && (prev != Config || prevMode != Mz700Mode))
        {
            ushort pc = Cpu != null ? Cpu.PC : (ushort)0;
            BankSwitchLog.AppendLine(
                $"PC=${pc:X4} IN ${(0xE0 + cmd):X2} " +
                $"mode={(prevMode ? "MZ700" : "MZ800")}→{(Mz700Mode ? "MZ700" : "MZ800")} " +
                $"cfg={prev}→{Config}");
        }
    }

    /// <summary>
    /// Set the display-mode bit from a DMD-register write. Called from
    /// <see cref="Mz800IoBus"/> when the CPU does OUT ($CE),A. DMD bit
    /// 3 (mask $08) selects the mode; the other bits control resolution
    /// and frame selection which Phase 1 ignores.
    ///
    /// Flipping to MZ-700 mode also transitions the config to (b) if
    /// we were in (a) — the tech-ref diagram shows this as the
    /// first-step of the IPL's mode change.
    /// </summary>
    public void SetDmdRegister(byte value)
    {
        bool wantMz700 = (value & 0x08) != 0;
        if (wantMz700 && !Mz700Mode)
        {
            Mz700Mode = true;
            if (Config == BankConfig.A_Power) Config = BankConfig.B_Mz700;
        }
        else if (!wantMz700 && Mz700Mode)
        {
            Mz700Mode = false;
            // Don't auto-flip the config — leave it to the caller's
            // subsequent IN $E0-$E5 sequence.
        }
    }

    public void LoadRom(byte[] rom)
    {
        int n = Math.Min(rom.Length, Rom.Length);
        Array.Copy(rom, Rom, n);
    }

    /// <summary>
    /// Restore power-on state — MZ-800 mode, config (a), and blank
    /// bitmap planes. Phase 5.1 added the plane-clear so a Reset
    /// gives a defined black display in MZ-800 mode instead of
    /// carrying pre-reset plane data forward.
    /// </summary>
    public void ResetBankState()
    {
        Mz700Mode = false;
        Config = BankConfig.A_Power;
        WfRegister = 0;
        RfRegister = 0;
        BorderColour = 0;
        Array.Clear(Palette,  0, Palette.Length);
        Array.Clear(PlaneI,   0, PlaneI.Length);
        Array.Clear(PlaneII,  0, PlaneII.Length);
        Array.Clear(PlaneIII, 0, PlaneIII.Length);
        Array.Clear(PlaneIV,  0, PlaneIV.Length);
    }
}
