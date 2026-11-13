using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Reverse lookup from display code to the (row, col, MzShift) slot
/// that types it in MZ-800 BASIC 1Z-016, per BASIC keyboard mode
/// (<see cref="MZ800.BasicKeyboardMode"/>: 0 ALPHA, 1 shift lock,
/// 2 GRAPH). Feeds the Font Sheet's click-to-type (v1.5 4.8).
///
/// Found by disassembly of 1Z-016's key scan ($0BF6, table pick at
/// $0CB6, lookup at $3C3E):
/// <list type="bullet">
///   <item>Four 18-byte tables at $0D47: ALPHA, ALPHA shifted, GRAPH
///   shifted, GRAPH. Each is a pointer to one of the MZ-700-mode
///   monitor ROM's key tables, then row 0's eight codes, then row
///   7's. In shift lock the SHIFT key is inverted.</item>
///   <item>Rows 0 and 7 give Sharp ASCII straight from the table. Rows
///   1–6 read the ROM table (display codes) at pointer + index and turn
///   the code into ASCII ($3C1C): the first i with ROM[$0A92 + i] equal
///   to it, except $40 → $80 and $80 → $C0, and $F0 or above for no
///   key.</item>
///   <item>The index is <c>row*8 + (7 - col)</c>, as in the MZ-700
///   monitor's scan.</item>
/// </list>
/// BASIC keeps the screen as ASCII and draws a character with the
/// CG-ROM's bank-0 glyph for its display code ($3C00): ROM[$0A92 + a],
/// except $80 → $40 and $C0 → $80. A scratch sweep typed every
/// character key in all three modes through the auto-typer: all 299
/// landed as this model says, in BASIC's text map and on screen.
///
/// Codes below $20 (CR, cursor keys, …) are controls, not characters.
/// Unshifted entries win on duplicates, as in <see cref="Mz700KeyTables"/>.
/// Valid only while 1Z-016 runs (<see cref="MZ800.BasicKeyboardMode"/>
/// not null); the tables live in BASIC's RAM.
/// </summary>
public sealed class Mz800KeyTables
{
    private const ushort BasicTables = 0x0D47;
    private const int BasicTableSize = 18;
    private const int MonitorAsciiToDisplay = 0x0A92;   // in the MZ-700-mode monitor
    private const int NoCode = -1;

    private readonly Mz800Memory _mem;
    private readonly Dictionary<(byte Code, int Mode), (int Row, int Col, bool MzShift)> _map = new();
    private bool _built;

    internal Mz800KeyTables(Mz800Memory mem) { _mem = mem; }

    /// <summary>Forget the built map (machine reset or new ROMs).</summary>
    internal void Invalidate()
    {
        _map.Clear();
        _built = false;
    }

    /// <summary>
    /// Returns the slot that types bank-0 display code
    /// <paramref name="code"/> in BASIC keyboard mode
    /// <paramref name="basicMode"/> (0 ALPHA, 1 shift lock, 2 GRAPH), or
    /// null if no key does. Call only while 1Z-016 runs.
    /// </summary>
    public (int Row, int Col, bool MzShift)? FindByDisplayCode(byte code, int basicMode)
    {
        if (!_built) Build();
        return _map.TryGetValue((code, basicMode), out var slot) ? slot : null;
    }

    private void Build()
    {
        for (int mode = 0; mode <= 2; mode++)
            for (int shift = 0; shift <= 1; shift++)
                for (int row = 0; row < 8; row++)
                    for (int col = 0; col < 8; col++)
                    {
                        int ascii = KeyCode(Table(mode, shift == 1), row * 8 + (7 - col));
                        if (ascii < 0x20) continue;
                        var key = ((byte)AsciiToDisplay(ascii), mode);
                        if (!_map.ContainsKey(key)) _map[key] = (row, col, shift == 1);
                    }
        _built = true;
    }

    // The table $0CB6..$0CE5 picks.
    private static int Table(int mode, bool shift)
    {
        if (mode == 2) return BasicTables + (shift ? 2 : 3) * BasicTableSize;
        bool effective = mode == 1 ? !shift : shift;
        return BasicTables + (effective ? BasicTableSize : 0);
    }

    // $3C3E: the ASCII code a key gives, or NoCode.
    private int KeyCode(int table, int index)
    {
        var ram = _mem.Ram;
        if (index < 8) return ram[table + 2 + index];
        if (index >= 56) return ram[table + 10 + index - 56];
        int ptr = ram[table] | ram[table + 1] << 8;
        return DisplayToAscii(_mem.Rom[(ptr + index) & 0x0FFF]);
    }

    // $3C1C.
    private int DisplayToAscii(byte display)
    {
        if (display >= 0xF0 || display == 0x73) return NoCode;
        if (display == 0x40 || display == 0x80) return display + 0x40;
        for (int i = 0; i < 256; i++)
            if (_mem.Rom[MonitorAsciiToDisplay + i] == display) return i;
        return NoCode;
    }

    // $3C00, for ASCII $20 and up.
    private int AsciiToDisplay(int ascii)
    {
        if (ascii == 0x80 || ascii == 0xC0) return ascii - 0x40;
        return _mem.Rom[MonitorAsciiToDisplay + ascii];
    }
}
