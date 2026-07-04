using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A memory map (per Owner's Manual §3.1.1, Figure 3.2):
///
///   Normal state:
///     $0000-$0FFF   Monitor ROM (SA-1510, 4 KiB)
///     $1000-$CFFF   Main memory RAM (48 KiB — 32 KiB standard + 16 KiB expansion)
///     $D000-$DFFF   Video RAM window (4 KiB — 2 KiB visible + 2 KiB scroll buffer)
///     $E000-$EFFF   Memory-mapped I/O (PPI, PIT, sound gate, video mode, memory swap)
///     $F000-$FFFF   Floppy drive control area (returns $FF when no drive attached)
///
///   Memory-swap state (triggered by <c>LD A,($E00C)</c>):
///     $0000-$0FFF   RAM (ROM banked out)
///     $C000-$CFFF   Monitor ROM (moved here)
///     ...rest unchanged
///
/// The memory swap is toggled by READING specific addresses inside the
/// $E000-$EFFF MMIO window — a departure from the MZ-700's OUT-port
/// bank-switch mechanism. The Read() path detects those two addresses
/// before dispatching to the IoBus, so the toggles fire regardless of
/// whether anyone reads the returned data byte.
///
/// Unlike the MZ-700 there's no attribute RAM at $D800 — the MZ-80A is
/// monochrome and uses that region as the extra 2 KiB scroll buffer.
/// </summary>
public sealed class MZ80AMemory : IMemory
{
    public byte[] Rom = new byte[0x1000];    // 4 KiB monitor ROM (SA-1510)
    public byte[] Ram = new byte[0x10000];   // full 64 KiB RAM backing
    public byte[] Vram = new byte[0x1000];   // 4 KiB VRAM window ($D000-$DFFF)

    /// <summary>
    /// When true, the monitor ROM is visible at $C000-$CFFF and RAM
    /// is exposed at $0000-$0FFF (CP/M-style layout). Toggled by
    /// reads at $E00C (set) / $E010 (clear); persists until re-toggled.
    /// </summary>
    public bool RomSwapped;

    public Mz80aIoBus? IoBus;
    public Z80Cpu? Cpu;

    public byte Read(ushort addr)
    {
        // Memory-swap side effects fire even when the read is
        // dispatched into the MMIO window below. Handle before the
        // normal address decode so the state change is observable.
        if (addr == 0xE00C) { RomSwapped = true; }
        else if (addr == 0xE010) { RomSwapped = false; }

        if (addr < 0x1000)
        {
            return RomSwapped ? Ram[addr] : Rom[addr];
        }
        if (addr >= 0xC000 && addr <= 0xCFFF)
        {
            return RomSwapped ? Rom[addr - 0xC000] : Ram[addr];
        }
        if (addr >= 0xD000 && addr <= 0xDFFF)
        {
            return Vram[addr - 0xD000];
        }
        if (addr >= 0xE000 && addr <= 0xEFFF)
        {
            return IoBus?.MemIn(addr) ?? 0xFF;
        }
        if (addr >= 0xF000)
        {
            // Floppy drive control area — no drive attached, reads
            // float high on the bus.
            return 0xFF;
        }
        return Ram[addr];
    }

    public void Write(ushort addr, byte value)
    {
        if (addr < 0x1000)
        {
            // Writes always land in RAM (ROM is read-only; RAM is
            // beneath it — becomes visible when swapped).
            Ram[addr] = value;
            return;
        }
        if (addr >= 0xC000 && addr <= 0xCFFF)
        {
            // Same shape — writes go to RAM even when ROM is swapped
            // to this region. Real hardware has an EPROM here in
            // swapped state; RAM still exists on the CPU board and
            // captures the writes for the future unswap.
            Ram[addr] = value;
            return;
        }
        if (addr >= 0xD000 && addr <= 0xDFFF)
        {
            Vram[addr - 0xD000] = value;
            return;
        }
        if (addr >= 0xE000 && addr <= 0xEFFF)
        {
            IoBus?.MemOut(addr, value);
            return;
        }
        if (addr >= 0xF000)
        {
            // Floppy area — writes ignored (no drive).
            return;
        }
        Ram[addr] = value;
    }

    public void LoadRom(byte[] rom)
    {
        int n = Math.Min(rom.Length, Rom.Length);
        Array.Copy(rom, Rom, n);
    }
}
