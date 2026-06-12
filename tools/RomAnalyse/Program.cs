// MZ-700 monitor ROM dump + disassembly helper. Built for the ad-hoc
// "where does feature X live in the ROM?" questions that come up when
// adding emulator features.
//
// Default ROM path: ..\..\roms\1z-013a.rom (relative to this project),
// or pass an explicit path as the first argument:
//
//   dotnet run --project tools\RomAnalyse                 # default ROM
//   dotnet run --project tools\RomAnalyse -- C:\path.rom  # explicit path
//
// Known landmarks (extend as we find more):
//   $0A50  keyboard scan routine
//   $0BEA  key-translation table — ALPHA unshifted
//   $0C2A  key-translation table — ALPHA shifted
//   $0C6A  key-translation table — GRAPH unshifted   (discovered 2026-06-07)
//   $0CAA  key-translation table — GRAPH shifted     (discovered 2026-06-07)
//   $1170  RAM byte — mirrors PC/MZ shift state (mode flag for table dispatch)
//
// All four key-translation tables are 64 bytes laid out as
// row*8 + (7 - col), and dispatched via (mode_flag << 6) offset from
// the base at $0BEA.

using System;
using System.IO;
using Z80Core;

namespace RomAnalyse;

internal sealed class RomMem : IMemory
{
    private readonly byte[] _rom;
    public RomMem(byte[] rom) => _rom = rom;
    public byte Read(ushort addr) => addr < _rom.Length ? _rom[addr] : (byte)0;
    public void Write(ushort addr, byte value) { /* read-only ROM view */ }
}

internal static class Program
{
    private static void Main(string[] args)
    {
        // Default path is relative to the repo root, two levels up from
        // this project. Run from anywhere via `dotnet run --project ...`.
        string path = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "roms", "1z-013a.rom");
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"ROM not found: {path}");
            Console.Error.WriteLine("Pass an explicit path as the first argument.");
            Environment.Exit(1);
        }

        byte[] rom = File.ReadAllBytes(path);
        Console.WriteLine($"ROM: {path}  ({rom.Length} bytes)");

        // Hex dumps of the four key-translation tables.
        Dump("ALPHA unshifted @ $0BEA", rom, 0x0BEA, 64);
        Dump("ALPHA shifted   @ $0C2A", rom, 0x0C2A, 64);
        Dump("GRAPH unshifted @ $0C6A", rom, 0x0C6A, 64);
        Dump("GRAPH shifted   @ $0CAA", rom, 0x0CAA, 64);

        var mem = new RomMem(rom);
        Console.WriteLine();
        Console.WriteLine("--- Disassembly: keyboard scan routine @$0A50 ---");
        Disasm(mem, 0x0A50, 100);

        Console.WriteLine();
        Console.WriteLine("--- Simulated RomKeyTables.Build (what FontSheet sees) ---");
        // Mirror the same skip-list logic as Hardware/SpecialKeyMap.cs:
        var slotLabels = new HashSet<(int row, int col)>
        {
            (0,0),(0,4),(0,6),(7,2),(7,3),(7,4),(7,5),(7,6),(7,7),
            (8,0),(8,6),(8,7),(9,3),(9,4),(9,5),(9,6),(9,7),
        };
        var byCode = new Dictionary<(byte Code, int Bank), (int Row, int Col, bool MzShift)>();
        Load(rom, 0x0BEA, 0, false, slotLabels, byCode);
        Load(rom, 0x0C2A, 0, true,  slotLabels, byCode);
        Load(rom, 0x0C6A, 1, false, slotLabels, byCode);
        Load(rom, 0x0CAA, 1, true,  slotLabels, byCode);

        int bank0 = 0, bank1 = 0;
        foreach (var kv in byCode)
            if (kv.Key.Bank == 0) bank0++; else bank1++;
        Console.WriteLine($"  bank 0 (ALPHA) entries: {bank0}");
        Console.WriteLine($"  bank 1 (GRAPH) entries: {bank1}");
        Console.WriteLine();
        Console.WriteLine("  Sample bank-1 lookups:");
        byte[] probe = { 0x32, 0x42, 0xB6, 0xD8, 0x76, 0x3C, 0x9C, 0xCA };
        foreach (var c in probe)
        {
            bool ok = byCode.TryGetValue((c, 1), out var slot);
            string txt = ok ? $"({slot.Row},{slot.Col}, shift={slot.MzShift})" : "NOT FOUND";
            Console.WriteLine($"    bank 1 code ${c:X2} → {txt}");
        }
    }

    private static void Load(byte[] rom, int offset, int bank, bool mzShift,
        HashSet<(int, int)> slotLabels,
        Dictionary<(byte Code, int Bank), (int Row, int Col, bool MzShift)> byCode)
    {
        for (int i = 0; i < 64 && offset + i < rom.Length; i++)
        {
            int row = i / 8;
            int col = 7 - (i % 8);
            if (slotLabels.Contains((row, col))) continue;
            byte code = rom[offset + i];
            var key = (code, bank);
            if (!byCode.ContainsKey(key)) byCode[key] = (row, col, mzShift);
        }
    }

    private static void Dump(string label, byte[] rom, int offset, int length)
    {
        Console.WriteLine();
        Console.WriteLine($"{label}: {length} bytes from ${offset:X4}");
        for (int row = 0; row < length; row += 16)
        {
            Console.Write($"  ${offset + row:X4}: ");
            for (int c = 0; c < 16 && row + c < length; c++)
                Console.Write($"{rom[offset + row + c]:X2} ");
            Console.Write("  | ");
            for (int c = 0; c < 16 && row + c < length; c++)
            {
                byte b = rom[offset + row + c];
                Console.Write(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            Console.WriteLine();
        }
    }

    private static void Disasm(IMemory mem, int start, int byteLen)
    {
        int pc = start;
        while (pc < start + byteLen)
        {
            var r = Z80Disassembler.Disassemble(mem, (ushort)pc);
            Console.WriteLine($"  ${pc:X4}: {r.Text}");
            pc += r.Length;
        }
    }
}
