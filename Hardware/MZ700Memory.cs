using System;
using MZ700Emul.Z80;

namespace MZ700Emul.Hardware;

/// <summary>
/// MZ-700 memory map:
///   0x0000-0x0FFF : Monitor ROM (1Z-013A) or RAM when banked out
///   0x1000-0xCFFF : RAM
///   0xD000-0xD7FF : VRAM (character codes) or RAM
///   0xD800-0xDFFF : Color/attribute RAM or RAM
///   0xE000-0xE00F : Memory-mapped I/O (8255 PPI + 8253 PIT + misc)
///   0xE010-0xFFFF : RAM (extended) or RAM when banked
///
/// Memory banking is triggered ONLY via port I/O on MZ-700 (OUT ($E0..$E5),A).
/// The memory region 0xE010-0xFFFF is plain RAM — writes there must NOT trigger
/// bank-switching, otherwise BASIC's memory init loops will re-enable the ROM
/// and crash.
/// </summary>
public sealed class MZ700Memory : IMemory
{
    public byte[] Rom = new byte[0x1000];        // 4KB monitor ROM
    public byte[] Ram = new byte[0x10000];       // full 64K RAM backing
    public byte[] Vram = new byte[0x800];        // 2KB character VRAM
    public byte[] Aram = new byte[0x800];        // 2KB attribute RAM

    public bool RomEnabled = true;
    public bool VramIoEnabled = true;

    public IoBus? IoBus;
    public Z80Cpu? Cpu;
    public System.Text.StringBuilder? BankSwitchLog;

    public byte Read(ushort addr)
    {
        if (addr < 0x1000)
        {
            return RomEnabled ? Rom[addr] : Ram[addr];
        }
        if (addr >= 0xD000 && addr <= 0xD7FF)
        {
            return VramIoEnabled ? Vram[addr - 0xD000] : Ram[addr];
        }
        if (addr >= 0xD800 && addr <= 0xDFFF)
        {
            return VramIoEnabled ? Aram[addr - 0xD800] : Ram[addr];
        }
        if (addr >= 0xE000 && addr <= 0xE00F)
        {
            if (VramIoEnabled && IoBus != null) return IoBus.MemIn(addr);
            return Ram[addr];
        }
        return Ram[addr];
    }

    public void Write(ushort addr, byte value)
    {
        if (addr < 0x1000)
        {
            // Writes always go to RAM (ROM is read-only, RAM is beneath it)
            Ram[addr] = value;
            return;
        }
        if (addr >= 0xD000 && addr <= 0xD7FF)
        {
            if (VramIoEnabled) Vram[addr - 0xD000] = value;
            else Ram[addr] = value;
            return;
        }
        if (addr >= 0xD800 && addr <= 0xDFFF)
        {
            if (VramIoEnabled) Aram[addr - 0xD800] = value;
            else Ram[addr] = value;
            return;
        }
        if (addr >= 0xE000 && addr <= 0xE00F)
        {
            if (VramIoEnabled && IoBus != null) IoBus.MemOut(addr, value);
            else Ram[addr] = value;
            return;
        }
        Ram[addr] = value;
    }

    public void HandleBankSwitch(byte cmd)
    {
        bool prevRom = RomEnabled, prevVio = VramIoEnabled;
        switch (cmd)
        {
            case 0x00: RomEnabled = false; break;
            case 0x01: VramIoEnabled = false; break;
            case 0x02: RomEnabled = true; break;
            case 0x03: VramIoEnabled = true; break;
            case 0x04: RomEnabled = false; VramIoEnabled = false; break;
            case 0x05: RomEnabled = true; VramIoEnabled = true; break;
        }
        if (BankSwitchLog != null && (prevRom != RomEnabled || prevVio != VramIoEnabled))
        {
            ushort pc = Cpu != null ? Cpu.PC : (ushort)0;
            BankSwitchLog.AppendLine($"PC=${pc:X4} cmd=${cmd:X2} -> Rom={RomEnabled} Vio={VramIoEnabled}");
        }
    }

    public void LoadRom(byte[] rom)
    {
        int n = Math.Min(rom.Length, Rom.Length);
        Array.Copy(rom, Rom, n);
    }
}
