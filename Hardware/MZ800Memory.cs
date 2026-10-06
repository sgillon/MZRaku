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
/// BASIC, for example, calls its ROM services with `OUT ($E3),A` …
/// `JP $F4xx` … `OUT ($E1),A` (routine at $1517) and maps VRAM in
/// with IN $E0 only while it draws.
///
/// VRAM storage: real hardware has one 16 KB VRAM chip (two with the
/// MZ-1R25) decoded differently per mode (tech-ref pp. 13-15). We keep
/// MZ-700-mode text/attribute VRAM (<see cref="Vram"/> /
/// <see cref="Aram"/>) and the four bitmap planes as separate buffers.
///
/// The MZ-800 bitmap side — WF/RF write and read formats, DMD, palette,
/// scroll registers — lives here too because every CPU access to the
/// $8000 window goes through it; <see cref="Mz800Video"/> only reads.
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

    // MZ-700-mode VRAM: 2 KB display codes + 2 KB attributes at
    // $D000-$DFFF when VRAM is mapped. Same shape as MZ700Memory.
    public byte[] Vram = new byte[0x800];
    public byte[] Aram = new byte[0x800];

    // MZ-800 bitmap VRAM: four planes, indexed by CPU address − $8000
    // through the WF / RF formats. 16 KB each: 320×200 uses the low
    // 8000 bytes (40 per raster); 640×200 uses the whole 16 KB CPU
    // window, even screen bytes at $0000-$1F3F and odd at $2000-$3F3F
    // (tech-ref p. 15). Planes III / IV exist only with the MZ-1R25
    // (<see cref="VramExpansion"/>).
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
    /// MZ-700 mode (DMD3:2 = 10) rather than an MZ-800 bitmap mode. Set
    /// by <see cref="SetDmdRegister"/>; decides where a mapped VRAM
    /// appears. The prohibited value DMD3:2 = 11 is treated as the
    /// 320×200 bitmap mode (real behaviour undefined, tech-ref p. 17).
    /// </summary>
    public bool Mz700Mode;

    public Mz800IoBus? IoBus;
    public Z80Cpu? Cpu;

    /// <summary>
    /// Write Format register, OUT ($CC),A (tech-ref p. 20): D7-D5 write
    /// mode, D4 B/A frame select, D3-D0 plane enables IV III II I.
    /// Decoded per CPU write by <see cref="WriteVideoPlane"/>.
    /// </summary>
    public byte WfRegister;

    /// <summary>
    /// Read Format register, OUT ($CD),A (tech-ref pp. 18-19): D7
    /// SEARCH / single, D4 B/A, D3-D0 plane (or search colour) IV III
    /// II I. Decoded per CPU read by <see cref="ReadVideoPlane"/>.
    /// </summary>
    public byte RfRegister;

    /// <summary>OUT ($CC),A.</summary>
    public void SetWfRegister(byte value) => WfRegister = value;

    /// <summary>OUT ($CD),A.</summary>
    public void SetRfRegister(byte value) => RfRegister = value;

    /// <summary>
    /// Display Mode register, OUT ($CE),A (tech-ref p. 17 Table-1):
    ///   DMD3:2  00 bitmap 320×200 · 01 bitmap 640×200 · 10 MZ-700 ·
    ///           11 prohibited
    ///   DMD1:0  320: 00 Frame A (I+II) · 01 Frame B (III+IV) ·
    ///                10 16-colour (I-IV)
    ///           640: 00 Frame A (I) · 01 Frame B (III) ·
    ///                10 4-colour (I+III)
    ///           11 prohibited. Frame B and the combined modes need the
    ///           MZ-1R25.
    /// </summary>
    public byte DmdRegister;

    /// <summary>
    /// Palette registers PLT0-3 (tech-ref p. 22). Each byte holds a
    /// 4-bit colour in I G R B order (D3 = intensity, D2 = G, D1 = R,
    /// D0 = B), resolved by <see cref="Mz800Video.IrgbToArgb"/>. In
    /// the 4-colour modes a pixel indexes here with
    /// <c>planeA_bit + 2 × planeB_bit</c>; in 320×200 16-colour mode
    /// only pixels in the group selected by <see cref="PaletteGroup"/>
    /// do (see <see cref="Mz800Video.SixteenColourLut"/>).
    /// </summary>
    public byte[] Palette = new byte[4];

    /// <summary>
    /// Palette switch SW0/SW1 (tech-ref p. 22): which of the four
    /// 16-colour groups — selected by the pixel's plane III / IV bits —
    /// is shown through PLT0-3 in 320×200 16-colour mode.
    /// </summary>
    public int PaletteGroup;

    /// <summary>
    /// Border colour — 4-bit IGRB, written via OUT ($CF),A with B=6
    /// (BCOL, tech-ref p. 23). The palette does not apply to the
    /// border (p. 22).
    /// </summary>
    public byte BorderColour;

    /// <summary>
    /// OUT ($F0),A palette write (tech-ref p. 22). D6-D4 = register
    /// select S2-S0: 0-3 → PLT0-3 with D3-D0 = I G R B; 4 → palette
    /// switch SW0/SW1 in D1-D0 (Exploding Fist's $40 selects palette
    /// group 0 — it is not a border write).
    ///
    /// The tech-ref marks D7 "x", but a write with D7=1 must not reach
    /// the palette: Uridium initialises with `$00 $11 … $EE` and
    /// real hardware shows a black background — if D7 were ignored,
    /// `$88` would set PLT0 to grey. So the whole high nibble is the
    /// select and anything other than 0-4 is dropped.
    /// </summary>
    public void WritePalette(byte value)
    {
        int select = value >> 4;
        if (select < 4) Palette[select] = (byte)(value & 0x0F);
        else if (select == 4) PaletteGroup = value & 0x03;
    }

    /// <summary>
    /// MZ-1R25 VRAM expansion fitted (planes III + IV exist). Set from
    /// settings at machine construction; default true. Without it,
    /// plane III/IV writes are dropped and reads float high, and the
    /// display modes that need them (Frame B, 320×200 16-colour,
    /// 640×200 4-colour) show only what planes I/II hold (tech-ref
    /// p. 17: "not assured").
    /// </summary>
    public bool VramExpansion = true;

    /// <summary>OUT ($CF),A with B=6 (BCOL): low nibble I G R B,
    /// high nibble unused (tech-ref p. 23).</summary>
    public void SetBorderColour(byte value) => BorderColour = (byte)(value & 0x0F);

    /// <summary>
    /// Hardware-scroll registers (tech-ref pp. 10-12), OUT ($CF),A with
    /// B=1..5. The CRTC remaps display addresses inside the window
    /// [SSA, SEA) — see <see cref="Mz800Video"/>'s scroll address map —
    /// so the CPU never moves VRAM to scroll.
    ///   <see cref="Ssa"/> B=4, 7-bit window start, 64-byte units
    ///                     (5 = one 8-raster character row); default $00.
    ///   <see cref="Sea"/> B=5, window end, same units; default $7D.
    ///   <see cref="Sw"/>  B=3, window width = SEA − SSA; default $7D.
    ///   <see cref="Sof"/> B=1 (low 8 bits) + B=2 (high 2 bits), 10-bit
    ///                     offset in 8-byte units (5 = one raster);
    ///                     must stay ≤ SW. Default 0.
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
    /// Optional --dump= log of every bank-latch change (PC, port, before
    /// → after). Capped at 4096 entries.
    /// </summary>
    public System.Text.StringBuilder? BankSwitchLog;

    /// <summary>
    /// Optional --dump= log of CPU writes to $8000-$BFFF (PC, address,
    /// value, bank state, mode, WF). Capped at 4096 entries.
    /// </summary>
    public System.Text.StringBuilder? VideoWriteLog;
    private int _videoWriteLogEntries;
    private const int VideoWriteLogCap = 4096;

    /// <summary>
    /// Optional --dump= log of DMD writes that switch between MZ-700 and
    /// MZ-800 mode (PC, value, bank state) — the quick answer to "when
    /// does this program change mode?" without reading the full CRTC
    /// log. Capped at 1024 entries.
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
    ///   10x REPLACE — "writes WD in a specific colour": enabled planes
    ///                 ← WD, the frame's other planes ← 0
    ///   11x PSET    — "writes only bit 1 of WD in a specific colour":
    ///                 enabled planes ← WD + VD, the frame's other
    ///                 planes ← ¬WD · VD (pixels where WD=0 untouched)
    ///       (WMD0 is "x" for both — Wheelie draws with WF=$F0/$F7)
    ///
    /// REPLACE and PSET act on the planes of the frame being written,
    /// which depends on the display mode (Table-1) and B/A — see
    /// <see cref="FramePlanes"/>. REPLACE with no planes enabled ($80)
    /// clears the frame (Exploding Fist, Jetpac, Uridium).
    ///
    /// WF=$00 (nothing programmed yet) writes plane I so pre-init
    /// writes stay visible in the debugger.
    /// </summary>
    private void WriteVideoPlane(ushort addr, byte value)
    {
        int offset = addr - 0x8000;
        if (offset < 0 || offset >= 0x4000) return;

        if (WfRegister == 0) { PlaneI[offset] = value; return; }

        int mode = (WfRegister >> 5) & 0x07;
        if (mode >= 0b100) mode &= 0b110;                  // WMD0 is don't-care for REPLACE / PSET
        int enabled = WfRegister & 0x0F;
        int frame = mode >= 0b100 ? FramePlanes(WfRegister) : 0;

        int planes = VramExpansion ? 4 : 2;
        for (int p = 0; p < planes; p++)
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
                case 0b110:                                                                 // PSET
                    if ((frame & bit) != 0)
                        plane[offset] = on ? (byte)(plane[offset] | value) : (byte)(plane[offset] & ~value);
                    break;
            }
        }
    }

    /// <summary>
    /// Planes (bit mask, bit 0 = plane I) making up the frame a REPLACE /
    /// PSET write or a SEARCH read targets — Table-1 (p. 17) with the
    /// WF / RF tables (pp. 19-20). DMD1:0 = 10 selects the combined
    /// modes (320 16-colour: I-IV; 640 4-colour: I+III); otherwise B/A
    /// (D4 of WF or RF) picks Frame A or B.
    /// </summary>
    private int FramePlanes(byte formatRegister)
    {
        bool frameB = (formatRegister & 0x10) != 0;
        bool combined = (DmdRegister & 0x03) == 0x02;
        if (Is640BitmapMode)
            return combined ? 0b0101 : frameB ? 0b0100 : 0b0001;
        return combined ? 0b1111 : frameB ? 0b1100 : 0b0011;
    }

    /// <summary>
    /// Read path for the MZ-800-mode bitmap-VRAM window, per the RF
    /// register (tech-ref pp. 18-19):
    ///   D7 = 0 → single-plane read of the plane selected in D3-D0
    ///           (only one should be set; the tech-ref leaves several
    ///           "not assured" — the lowest wins here).
    ///   D7 = 1 → SEARCH, see <see cref="SearchRead"/>.
    /// RF=$00 (nothing programmed yet) reads plane I. Reads of an
    /// absent plane (no MZ-1R25) float high.
    /// </summary>
    private byte ReadVideoPlane(ushort addr)
    {
        int offset = addr - 0x8000;
        if (offset < 0 || offset >= 0x4000) return 0xFF;

        if (RfRegister == 0) return PlaneI[offset];         // cold-boot fallback
        if ((RfRegister & 0x80) != 0) return SearchRead(offset);

        if ((RfRegister & 0x01) != 0) return PlaneI[offset];
        if ((RfRegister & 0x02) != 0) return PlaneII[offset];
        if (!VramExpansion) return 0xFF;                     // planes III/IV absent
        if ((RfRegister & 0x04) != 0) return PlaneIII[offset];
        if ((RfRegister & 0x08) != 0) return PlaneIV[offset];
        return 0xFF;
    }

    /// <summary>
    /// RF SEARCH read (RF D7 = 1, tech-ref pp. 18-19 Table-2): bit n of
    /// the result is 1 where pixel n's colour — its bits across the
    /// frame's planes — equals the RF plane bits (D3-D0 = IV III II I).
    /// Planes outside the frame are "disregarded" (don't-care). The
    /// frame follows the display mode and RF B/A, as for writes.
    /// Used by Wheelie (RF=$FF, colour 15), Manic Miner, Abu Simbel.
    /// </summary>
    private byte SearchRead(int offset)
    {
        int frame = FramePlanes(RfRegister);
        if (!VramExpansion) frame &= 0b0011;
        int result = 0xFF;
        for (int p = 0; p < 4; p++)
        {
            int bit = 1 << p;
            if ((frame & bit) == 0) continue;
            byte plane = p switch { 0 => PlaneI[offset], 1 => PlaneII[offset], 2 => PlaneIII[offset], _ => PlaneIV[offset] };
            // Keep pixels whose plane bit matches the wanted colour bit.
            result &= (RfRegister & bit) != 0 ? plane : ~plane;
        }
        return (byte)result;
    }

    /// <summary>
    /// Does a read of <paramref name="addr"/> hit the PPI/PIT window?
    /// Only in MZ-700 mode with the high ROM mapped (and not
    /// prohibited); in MZ-800 mode the I/O lives on ports instead.
    /// </summary>
    public bool IsIoRead(ushort addr)
        => Mz700Mode && RomHigh && !Prohibited && addr >= 0xE000 && addr <= 0xE00F;

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
        // --dump= diagnostic: every write to $8000-$BFFF, whatever the
        // mode or bank state.
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
    /// The full value is kept in <see cref="DmdRegister"/> for the
    /// renderer and the write / read frame logic; this method also
    /// tracks the MZ-700 ↔ MZ-800 switch. Bank latches are independent
    /// of DMD — the mode only decides where a mapped VRAM appears
    /// ($D000 vs $8000).
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
    /// True when DMD selects the 640×200 bitmap mode (DMD3:2 = 01).
    /// </summary>
    public bool Is640BitmapMode => (DmdRegister & 0x0C) == 0x04;

    public void LoadRom(byte[] rom)
    {
        int n = Math.Min(rom.Length, Rom.Length);
        Array.Copy(rom, Rom, n);
    }

    /// <summary>
    /// Restore power-on state — MZ-800 mode, every bank latch set
    /// (the OUT $E4 map), CRTC registers at their defaults, and blank
    /// bitmap planes.
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
        PaletteGroup = 0;
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
