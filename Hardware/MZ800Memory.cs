using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-800 memory + banking (per tech-ref pp. 3-5, 24). The
/// MZ-800 has two operating modes, MZ-800 and MZ-700 — the mode flips
/// via OUT ($CE),A (DMD register, see <see cref="SetDmdRegister"/>) —
/// and a set of independent bank latches driven by the memory
/// controller ports (tech-ref p. 4-5 "Memory Bank Control"):
///
///   port      MZ-700 mode                      MZ-800 mode
///   OUT $E0   $0000-$7FFF → DRAM               same
///   OUT $E1   $D000-$FFFF → DRAM               $E000-$FFFF → DRAM
///   OUT $E2   $0000-$0FFF → monitor ROM        same
///   OUT $E3   $D000-$FFFF → VRAM/key+timer/ROM $E000-$FFFF → monitor ROM
///   OUT $E4   power-on map for the mode        power-on map for the mode
///   OUT $E5   $D000-$FFFF prohibited           $E000-$FFFF prohibited
///   OUT $E6   undo $E5                         undo $E5
///   IN  $E0   CG-ROM at $1000, PCG at $C000    CG-ROM at $1000, VRAM at $8000
///   IN  $E1   undo IN $E0                      undo IN $E0
///
/// Modelled as four latches (<see cref="RomLow"/>, <see cref="CgRom"/>,
/// <see cref="VramOn"/>, <see cref="RomHigh"/>) plus
/// <see cref="Prohibited"/>; the current mode decides where VRAM
/// appears ($8000-$BFFF bitmap planes in MZ-800 mode, $D000-$DFFF
/// text/attribute VRAM + $E000-$E00F I/O in MZ-700 mode). Power-on
/// has every latch set: MZ-700 monitor ROM at $0000, CG-ROM at $1000,
/// VRAM, and the MZ-800 IPL/monitor (1Z-016B) at $E000 — the reset
/// vector `JP $E800` lands in the IPL.
///
/// Phase 6.0 (2026-09-26) replaced the earlier four-config enum,
/// which only reacted to IN $E0-$E6. BASIC calls its ROM services
/// via `OUT ($E3),A` / `OUT ($E1),A` around a `JP $F4xx` (routine at
/// $1517) — invisible to the enum model, so the first ROM call ran
/// into empty DRAM.
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
    // Sizing: 16 KB per plane. In 320×200 mode only the low 8 KB
    // (8000 bytes = 40 cols × 200 rows) is used; the upper half sits
    // idle. In 640×200 mode a single "plane" spans the full 16 KB
    // CPU window ($8000-$BFFF) with display bytes interleaved
    // even/odd across the two halves — even bytes at offset
    // $0000-$1F3F, odd at $2000-$3F3F (tech-ref p. 15). Frame A =
    // Plane I (+ Plane II when 320×200 4-colour); Frame B = Plane III
    // (+ Plane IV when 320×200 4-colour). Phase 5.6 grew this from
    // 8 KB so 640-mode CPU writes to $A000-$BFFF no longer drop off
    // the end.
    public byte[] PlaneI   = new byte[0x4000];
    public byte[] PlaneII  = new byte[0x4000];
    public byte[] PlaneIII = new byte[0x4000];
    public byte[] PlaneIV  = new byte[0x4000];

    // Bank latches — see the class comment for the port table.
    /// <summary>$0000-$0FFF shows the MZ-700 monitor ROM (1Z-013B).</summary>
    public bool RomLow = true;
    /// <summary>$1000-$1FFF shows the CG-ROM (IN $E0 / IN $E1).</summary>
    public bool CgRom = true;
    /// <summary>VRAM is mapped: $8000-$BFFF planes (MZ-800 mode) or
    /// $D000-$DFFF text/attribute VRAM (MZ-700 mode).</summary>
    public bool VramOn = true;
    /// <summary>$E000-$FFFF shows the MZ-800 IPL/monitor ROM (MZ-800
    /// mode) or key/timer I/O + ROM (MZ-700 mode, from $E000).</summary>
    public bool RomHigh = true;
    /// <summary>OUT $E5 — the top block ($D000+ in MZ-700 mode, $E000+
    /// in MZ-800 mode) is deselected until OUT $E6.</summary>
    public bool Prohibited;

    /// <summary>Compact bank-state label for traces and logs.</summary>
    public string BankState
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(5);
            if (RomLow) parts.Add("ROM0");
            if (CgRom) parts.Add("CG");
            if (VramOn) parts.Add("VRAM");
            if (RomHigh) parts.Add("ROMH");
            if (Prohibited) parts.Add("PROH");
            return parts.Count == 0 ? "DRAM" : string.Join("+", parts);
        }
    }

    /// <summary>
    /// Which display mode the machine is in. Set by DMD-register
    /// writes at OUT ($CE),A — the DMD3+DMD2 field (mask $0C) selects
    /// the mode: $00 = MZ-800 320×200, $04 = MZ-800 640×200, $08 =
    /// MZ-700, $0C = prohibited. See tech-ref p. 17 Table-1.
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
    /// CRTC Display Mode register — full value from the last OUT ($CE),A.
    /// Bit layout per tech-ref p. 17 Table-1:
    ///   DMD3+DMD2 = combined mode/resolution field:
    ///     00 → MZ-800 bitmap 320×200
    ///     01 → MZ-800 bitmap 640×200
    ///     10 → MZ-700 mode (40×25 char cells)
    ///     11 → prohibited
    ///   DMD1+DMD0 = combined frame/plane designation (see Table-1):
    ///     for 320×200: 00 Frame A · 01 Frame B · 10 both (16-colour,
    ///     32-KB VRAM only) · 11 prohibited
    ///     for 640×200: 00 Frame A (Plane I) · 01 Frame B (Plane III,
    ///     32-KB VRAM only) · 10 both (4-colour, 32-KB VRAM only) ·
    ///     11 prohibited
    /// Phase 5.6 wired the combined decode; Phase 5.0-5.5 had only
    /// bit 3 acted on (which happened to be right for the two values
    /// the IPL and BASIC actually write: $00 and $08).
    /// </summary>
    public byte DmdRegister;

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
    /// Scroll registers (tech-ref pp. 10-11). CRTC uses these to
    /// window and offset the plane addresses driven onto the display,
    /// giving smooth vertical scroll and split-screen scroll windows
    /// without the CPU having to memmove any VRAM. Programmed via
    /// OUT ($CF),A with B=1..5.
    ///
    ///   <see cref="Ssa"/> B=4 (7-bit): scroll start address — top
    ///                    of the scroll window in units of $5 (each
    ///                    unit = 1 character row = 8 scanlines).
    ///                    Range $0-$78; default $0.
    ///   <see cref="Sea"/> B=5 (7-bit): scroll end address, same
    ///                    units. Range $5-$7D; default $7D
    ///                    (covers full 200 scanlines).
    ///   <see cref="Sw"/>  B=3 (7-bit): scroll width = SEA - SSA.
    ///                    Default $7D (whole display is one scroll
    ///                    region). Constraint: SW &gt; SOF.
    ///   <see cref="Sof"/> B=1 (SOF1, low 8 bits) + B=2 (SOF2, high
    ///                    2 bits) — 10-bit scroll offset. Increment
    ///                    $5 = shift display up by 1 scanline
    ///                    (tech-ref §3 smooth-scroll example).
    ///                    Range $0-$3E8; default $0.
    ///
    /// Phase 5.7 MVP: only SOF is fed into the renderers (both 320
    /// and 640) as a full-screen circular scroll wrapping within the
    /// full 200-scanline plane extent. SSA/SEA/SW are stored but not
    /// yet used to define a windowed scroll region — that arrives
    /// when either BASIC's split CONSOLE or an MC game (Uridium's
    /// candidate) exercises it. See research/06-hardware-scroll.md.
    /// </summary>
    public byte Ssa = 0x00;
    /// <inheritdoc cref="Ssa" />
    public byte Sea = 0x7D;
    /// <inheritdoc cref="Ssa" />
    public byte Sw  = 0x7D;
    /// <inheritdoc cref="Ssa" />
    public ushort Sof;

    /// <summary>OUT ($CF),A with B=4 — sets <see cref="Ssa"/>.</summary>
    public void SetSsa(byte value) => Ssa = (byte)(value & 0x7F);
    /// <summary>OUT ($CF),A with B=5 — sets <see cref="Sea"/>.</summary>
    public void SetSea(byte value) => Sea = (byte)(value & 0x7F);
    /// <summary>OUT ($CF),A with B=3 — sets <see cref="Sw"/>.</summary>
    public void SetSw(byte value)  => Sw  = (byte)(value & 0x7F);

    /// <summary>OUT ($CF),A with B=1 — sets the low 8 bits of
    /// <see cref="Sof"/> (SOF7..SOF0), preserving SOF9..SOF8.</summary>
    public void SetSof1(byte value)
        => Sof = (ushort)((Sof & 0x0300) | value);

    /// <summary>OUT ($CF),A with B=2 — sets the high 2 bits of
    /// <see cref="Sof"/> (SOF9..SOF8, low bits of value), preserving
    /// SOF7..SOF0.</summary>
    public void SetSof2(byte value)
        => Sof = (ushort)((Sof & 0x00FF) | ((value & 0x03) << 8));

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
    /// Phase 5.8 diagnostic: every OUT ($CE),A that actually changes
    /// <see cref="Mz700Mode"/> or <see cref="Config"/> emits a line
    /// with PC + before/after state. Answers "what mode transitions
    /// does software do during boot/game-load?" without needing to
    /// grep the (much noisier) CrtcWriteLog. Populated only under
    /// --dump=; capped at 1024 entries — mode transitions are rare so
    /// this cap is much lower than the plane-write log's.
    /// See research/08-mode-flip.md.
    /// </summary>
    public System.Text.StringBuilder? ModeFlipLog;
    private int _modeFlipLogEntries;
    private const int ModeFlipLogCap = 1024;

    /// <summary>
    /// Does this address route through the bitmap-VRAM plane storage
    /// rather than DRAM? MZ-800 mode with VRAM mapped: $8000-$BFFF in
    /// 640×200 mode, $8000-$9FFF in 320×200 mode — tech-ref p. 4
    /// NOTE: "In the case of 320 × 200 mode, contents of $8000-$9FFF
    /// are transferred, instead, and those after $A000 are
    /// transferred to DRAM."
    /// </summary>
    private bool RoutesToBitmapVram(ushort addr)
        => !Mz700Mode && VramOn && addr >= 0x8000
           && addr <= (Is640BitmapMode ? 0xBFFF : 0x9FFF);

    // Top block the OUT $E5 "prohibited" latch deselects.
    private bool InProhibitedBlock(ushort addr)
        => Prohibited && addr >= (Mz700Mode ? 0xD000 : 0xE000);

    /// <summary>
    /// Write path for the MZ-800-mode bitmap-VRAM window, per the WF
    /// table on tech-ref p. 20. WF layout: D7-D5 write mode, D4 B/A
    /// (frame select), D3-D0 plane enables IV III II I. WD = CPU data,
    /// VD = current VRAM byte.
    ///
    ///   000 SINGLE  — enabled planes ← WD
    ///   001 XOR     — enabled planes ← WD ⊕ VD
    ///   010 OR      — enabled planes ← WD + VD
    ///   011 RESET   — enabled planes ← ¬WD · VD
    ///       (disabled planes unchanged for all four; plane bits are
    ///       absolute plane numbers)
    ///   100 REPLACE — "writes WD in a specific colour": enabled planes
    ///                 ← WD, the frame's other planes ← 0
    ///   101 PSET    — "writes only bit 1 of WD in a specific colour":
    ///                 enabled planes ← WD + VD, the frame's other
    ///                 planes ← ¬WD · VD (pixels where WD=0 untouched)
    ///   110 / 111   — undefined; no-op
    ///
    /// REPLACE and PSET act on the planes of the frame being written,
    /// which depends on the display mode (Table-1) and B/A — see
    /// <see cref="WriteFramePlanes"/>. REPLACE with no planes enabled
    /// ($80) clears the frame: Exploding Fist, Jetpac and Uridium rely
    /// on it. Phase 5.2 treated REPLACE as "enabled planes only" and
    /// left PSET as a no-op, so cleared graphics lingered on the other
    /// plane (fixed 2026-09-27).
    ///
    /// Address decode: plane offset = addr - $8000 (see
    /// research/02-plane-layout.md for the 640-mode interleave).
    ///
    /// Cold-boot fallback: WF=$00 decodes as SINGLE with no planes
    /// enabled — semantically a no-op. Fall back to plane I so the
    /// very first writes (before any code programs WF) land somewhere
    /// the debugger can see them.
    /// </summary>
    private void WriteVideoPlane(ushort addr, byte value)
    {
        int offset = addr - 0x8000;
        if (offset < 0 || offset >= 0x4000) return;

        if (WfRegister == 0) { PlaneI[offset] = value; return; }

        int mode = (WfRegister >> 5) & 0x07;
        int enabled = WfRegister & 0x0F;
        int frame = mode >= 0b100 ? WriteFramePlanes() : 0;

        for (int p = 0; p < 4; p++)
        {
            int bit = 1 << p;
            byte[] plane = p switch { 0 => PlaneI, 1 => PlaneII, 2 => PlaneIII, _ => PlaneIV };
            bool on = (enabled & bit) != 0;
            switch (mode)
            {
                case 0b000: if (on) plane[offset] = value; break;                          // SINGLE
                case 0b001: if (on) plane[offset] ^= value; break;                         // XOR
                case 0b010: if (on) plane[offset] |= value; break;                         // OR
                case 0b011: if (on) plane[offset] &= (byte)~value; break;                  // RESET
                case 0b100:                                                                 // REPLACE
                    if ((frame & bit) != 0) plane[offset] = on ? value : (byte)0;
                    break;
                case 0b101:                                                                 // PSET
                    if ((frame & bit) != 0)
                        plane[offset] = on ? (byte)(plane[offset] | value) : (byte)(plane[offset] & ~value);
                    break;
            }
        }
    }

    /// <summary>
    /// Planes (bit mask, bit 0 = plane I) that make up the frame a
    /// REPLACE / PSET write targets — tech-ref Table-1 (p. 17) and the
    /// WF table (p. 20). DMD1:0 = 10 selects the combined modes
    /// (320 16-colour: I-IV; 640 4-colour: I+III); otherwise WF B/A
    /// picks frame A or B.
    /// </summary>
    private int WriteFramePlanes()
    {
        bool frameB = (WfRegister & 0x10) != 0;
        bool combined = (DmdRegister & 0x03) == 0x02;
        if (Is640BitmapMode)
            return combined ? 0b0101 : frameB ? 0b0100 : 0b0001;
        return combined ? 0b1111 : frameB ? 0b1100 : 0b0011;
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
        if (offset < 0 || offset >= 0x4000) return 0xFF;

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
        if (addr < 0x1000) return RomLow ? Rom[addr] : Ram[addr];       // MZ-700 monitor ROM
        if (addr < 0x2000) return CgRom ? Rom[addr] : Ram[addr];        // CG-ROM
        if (RoutesToBitmapVram(addr)) return ReadVideoPlane(addr);
        if (addr < 0xD000) return Ram[addr];
        if (InProhibitedBlock(addr)) return 0xFF;

        if (Mz700Mode)
        {
            if (addr < 0xE000)
            {
                if (!VramOn) return Ram[addr];
                return addr < 0xD800 ? Vram[addr - 0xD000] : Aram[addr - 0xD800];
            }
            if (!RomHigh) return Ram[addr];
            if (addr <= 0xE00F) return IoBus?.MemIn(addr) ?? 0xFF;      // key / timer
            return Rom[0x2000 + (addr - 0xE000)];
        }

        if (addr >= 0xE000 && RomHigh) return Rom[0x2000 + (addr - 0xE000)]; // MZ-800 IPL/monitor (1Z-016B)
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
                $"PC=${pc:X4} W ${addr:X4}=${value:X2} bank={BankState} " +
                $"mode={(Mz700Mode ? "MZ700" : "MZ800")} WF=${wf:X2}");
            if (_videoWriteLogEntries == VideoWriteLogCap)
                VideoWriteLog.AppendLine($"[...cap {VideoWriteLogCap} entries, further writes suppressed]");
        }

        // Writes to a ROM window land in the DRAM beneath (same pattern
        // as MZ-700 / MZ-80A — the ROM is read-only and DRAM captures
        // the writes for when the ROM is banked out).
        if (RoutesToBitmapVram(addr)) { WriteVideoPlane(addr, value); return; }
        if (addr < 0xD000) { Ram[addr] = value; return; }
        if (InProhibitedBlock(addr)) return;

        if (Mz700Mode && VramOn && addr < 0xE000)
        {
            if (addr < 0xD800) Vram[addr - 0xD000] = value;
            else Aram[addr - 0xD800] = value;
            return;
        }
        if (Mz700Mode && RomHigh && addr <= 0xE00F) { IoBus?.MemOut(addr, value); return; }
        Ram[addr] = value;
    }

    /// <summary>
    /// OUT ($E0-$E6) bank control — tech-ref p. 4-5. <paramref name="cmd"/>
    /// is the port's low nibble. See the class comment for the table.
    /// </summary>
    public void HandleBankOut(byte cmd)
    {
        string prev = BankState;
        switch (cmd)
        {
            case 0x00: RomLow = false; CgRom = false; break;           // $0000-$7FFF → DRAM
            case 0x01:                                                 // top block → DRAM
                RomHigh = false;
                if (Mz700Mode) VramOn = false;                         // MZ-700: $D000-$FFFF
                break;
            case 0x02: RomLow = true; break;                           // monitor ROM at $0000
            case 0x03:                                                 // top block → ROM (+VRAM/IO)
                RomHigh = true;
                if (Mz700Mode) VramOn = true;
                break;
            case 0x04:                                                 // power-on map
                RomLow = true; RomHigh = true; VramOn = true;
                CgRom = !Mz700Mode;                                    // MZ-700: $1000-$CFFF DRAM
                break;
            case 0x05: Prohibited = true; break;
            case 0x06: Prohibited = false; break;
        }
        LogBank($"OUT ${0xE0 + cmd:X2}", prev);
    }

    /// <summary>
    /// IN ($E0/$E1) bank control — tech-ref p. 5. The read is the
    /// trigger; the data returned is discarded. IN $E0 maps the CG-ROM
    /// at $1000-$1FFF (the IPL copies it into PCG this way) and, in
    /// MZ-800 mode, VRAM at $8000; IN $E1 undoes both. The tech-ref
    /// defines no IN behaviour for $E2-$E6.
    ///
    /// Phase 2.5 fix (2026-08-28): $E0 and $E1 were swapped, causing
    /// the IPL's LDIR at $E8B4 to copy from DRAM (zeros) instead of
    /// CG-ROM, and — worse — the subsequent CALL $001B (GETL) ran
    /// with the stack ($10DE-$10F0) inside the CG-ROM window.
    /// </summary>
    public void HandleBankIn(byte cmd)
    {
        string prev = BankState;
        switch (cmd)
        {
            case 0x00:
                CgRom = true;
                if (!Mz700Mode) VramOn = true;
                break;
            case 0x01:
                CgRom = false;
                if (!Mz700Mode) VramOn = false;
                break;
        }
        LogBank($"IN ${0xE0 + cmd:X2}", prev);
    }

    private int _bankLogEntries;
    private const int BankLogCap = 4096;

    private void LogBank(string op, string prev)
    {
        if (BankSwitchLog == null || _bankLogEntries >= BankLogCap) return;
        string now = BankState;
        if (now == prev) return;
        _bankLogEntries++;
        ushort pc = Cpu != null ? Cpu.PC : (ushort)0;
        BankSwitchLog.AppendLine(
            $"PC=${pc:X4} {op} mode={(Mz700Mode ? "MZ700" : "MZ800")} bank={prev}→{now}");
        if (_bankLogEntries == BankLogCap)
            BankSwitchLog.AppendLine($"[...cap {BankLogCap} entries, further switches suppressed]");
    }

    /// <summary>
    /// Handle an OUT ($CE),A DMD-register write. Per tech-ref p. 17
    /// Table-1 the top nibble carries two 2-bit fields:
    ///   DMD3+DMD2 (mask $0C) — mode/resolution:
    ///     $00 = MZ-800 bitmap 320×200
    ///     $04 = MZ-800 bitmap 640×200
    ///     $08 = MZ-700 mode
    ///     $0C = prohibited
    ///   DMD1+DMD0 (mask $03) — frame/plane designation (Table-1).
    /// The full value is stashed in <see cref="DmdRegister"/> so the
    /// renderer can inspect resolution and frame; this method tracks
    /// the MZ-700 ↔ MZ-800 mode transition.
    ///
    /// Bank latches are independent of DMD — the current mode only
    /// changes where a mapped VRAM appears ($D000 vs $8000). Phase 5.8
    /// added a config "auto-flip" here because the old four-config
    /// enum had MZ-700-only configs that dropped plane writes after a
    /// switch to MZ-800 mode; with latches (Phase 6.0) a DMD=$00 write
    /// routes $8000 to the planes whenever VRAM is mapped, so the
    /// auto-flip is gone. See research/08-mode-flip.md.
    ///
    /// If <see cref="ModeFlipLog"/> is populated, every actual mode
    /// transition is captured there for diagnostic replay.
    /// </summary>
    public void SetDmdRegister(byte value)
    {
        var prevMode = Mz700Mode;
        ushort pc = Cpu != null ? Cpu.PC : (ushort)0;

        DmdRegister = value;
        Mz700Mode = (value & 0x0C) == 0x08;

        if (ModeFlipLog != null && prevMode != Mz700Mode
            && _modeFlipLogEntries < ModeFlipLogCap)
        {
            _modeFlipLogEntries++;
            ModeFlipLog.AppendLine(
                $"PC=${pc:X4} OUT ($CE),${value:X2}  " +
                $"mode={(prevMode ? "MZ700" : "MZ800")}→{(Mz700Mode ? "MZ700" : "MZ800")}  " +
                $"bank={BankState}");
            if (_modeFlipLogEntries == ModeFlipLogCap)
                ModeFlipLog.AppendLine($"[...cap {ModeFlipLogCap} entries, further transitions suppressed]");
        }
    }

    /// <summary>
    /// True when DMD selects MZ-800 640×200 bitmap mode (DMD3+DMD2 =
    /// 01). Consumed by the renderer dispatch in <see cref="MZ800"/>.
    /// </summary>
    public bool Is640BitmapMode => (DmdRegister & 0x0C) == 0x04;

    public void LoadRom(byte[] rom)
    {
        int n = Math.Min(rom.Length, Rom.Length);
        Array.Copy(rom, Rom, n);
    }

    /// <summary>
    /// Restore power-on state — MZ-800 mode, every bank latch set
    /// (the OUT $E4 map), and blank bitmap planes. Phase 5.1 added the
    /// plane-clear so a Reset gives a defined black display in MZ-800
    /// mode instead of carrying pre-reset plane data forward.
    /// </summary>
    public void ResetBankState()
    {
        Mz700Mode = false;
        RomLow = CgRom = VramOn = RomHigh = true;
        Prohibited = false;
        WfRegister = 0;
        RfRegister = 0;
        DmdRegister = 0;
        BorderColour = 0;
        // Scroll defaults per tech-ref p. 10 §2 — "no scroll" state
        // that covers the full 200-scanline display.
        Ssa = 0x00;
        Sea = 0x7D;
        Sw  = 0x7D;
        Sof = 0x0000;
        Array.Clear(Palette,  0, Palette.Length);
        Array.Clear(PlaneI,   0, PlaneI.Length);
        Array.Clear(PlaneII,  0, PlaneII.Length);
        Array.Clear(PlaneIII, 0, PlaneIII.Length);
        Array.Clear(PlaneIV,  0, PlaneIV.Length);
    }
}
