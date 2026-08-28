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

    // Bank-configuration enum. Names follow the tech-ref diagram
    // letters (a,b,c,d). Extra configs the tech-ref table hints at
    // (e.g. after IN $E1 in MZ-800 mode) can be added when a specific
    // boot path lands on them.
    public enum BankConfig
    {
        A_Power,     // Power-on / after Reset (MZ-800 mode default)
        B_Mz700,     // MZ-700-mode operating: ROM+DRAM+VRAM+IO+ROM
        C_PcgWrite,  // MZ-700-mode PCG update: ROM+CGROM+DRAM+VRAM+ROM
        D_AllRam,    // BASIC/user code: all 64 KB DRAM
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
    /// Optional log sink (mirror of MZ700Memory.BankSwitchLog).
    /// Useful during Phase 1 bring-up to see the IPL's bank-switch
    /// sequence in the debugger.
    /// </summary>
    public System.Text.StringBuilder? BankSwitchLog;

    public byte Read(ushort addr)
    {
        switch (Config)
        {
            case BankConfig.A_Power:
                // MZ-800 mode power-on layout (tech-ref p. 3 config a)
                if (addr < 0x1000) return Rom[addr];                    // MZ-700 monitor
                if (addr < 0x2000) return Rom[addr];                    // CG ROM ($1000-$1FFF)
                if (addr >= 0xE000) return Rom[0x2000 + (addr - 0xE000)]; // MZ-800 IPL/monitor (1Z-016B)
                return Ram[addr];                                       // DRAM + VRAM window as DRAM

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
                // out entirely, VRAM/IO windows disabled.
                return Ram[addr];
        }
        return Ram[addr];
    }

    public void Write(ushort addr, byte value)
    {
        // Writes to the ROM window always land in RAM beneath (same
        // pattern as MZ-700 / MZ-80A — the ROM is read-only and RAM
        // captures the writes for the future all-RAM state).
        switch (Config)
        {
            case BankConfig.A_Power:
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
    /// Boot-path transitions this method handles:
    ///   $E0  MZ-700 mode: (c) → (b)  — restore text/attr VRAM
    ///   $E1  MZ-700 mode: (b) → (c)  — expose CG-ROM at $1000, PCG at $D000
    ///   $E1  MZ-800 mode: (a) → (d)  — all DRAM (used by BASIC per feasibility)
    ///   $E4  either mode: → default  — power-on-like restore
    /// </summary>
    public void HandleBankSwitch(byte cmd)
    {
        var prev = Config;
        var prevMode = Mz700Mode;

        switch (cmd)
        {
            case 0x00:  // $E0
                if (Mz700Mode) Config = BankConfig.B_Mz700;
                // In MZ-800 mode, $E0 puts DRAM at $0000-$7FFF per the
                // tech-ref table. Not on a Phase-1 boot path; log only.
                break;
            case 0x01:  // $E1
                if (Mz700Mode) Config = BankConfig.C_PcgWrite;
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
    /// Restore power-on state — MZ-800 mode, config (a).
    /// </summary>
    public void ResetBankState()
    {
        Mz700Mode = false;
        Config = BankConfig.A_Power;
    }
}
