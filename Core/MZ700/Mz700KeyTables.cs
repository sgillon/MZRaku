using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Reverse lookup from MZ-700 display code to the (row, col, MzShift)
/// slot that types it, per keyboard mode (ALPHA or GRAPH). Feeds the
/// Font Sheet's click-to-type.
///
/// Two sources, because S-BASIC runs with the monitor ROM banked out
/// and scans the keyboard with its own tables:
/// <list type="bullet">
///   <item>S-BASIC 1Z-013B, while it is running: four 64-byte tables
///   at $121D (ALPHA, ALPHA shifted, GRAPH, GRAPH shifted) in Sharp
///   ASCII, turned into display codes through BASIC's ASCII → display
///   table at $0F1C (ASCII $20–$FF).</item>
///   <item>Otherwise the monitor ROM's four tables, already in display
///   codes: $0BEA ALPHA, $0C2A ALPHA shifted, $0C6A GRAPH (found
///   2026-06-07), $0CAA GRAPH shifted (mostly empty; S-BASIC's GRAPH
///   shifted table is full).</item>
/// </list>
/// Both use the layout of the ROM's scan routine at $0A50:
/// <c>index = row*8 + (7 - col)</c>, rows 0–7 (rows 8 and 9 hold
/// modifier and function keys). For the codes both give, they agree.
///
/// Every code either source gives is a font bank-0 code: GRAPH mode
/// picks a different table, not a different bank, and S-BASIC types a
/// GRAPH key with the attribute's bank bit clear. Bank 1 is reached
/// only by a program setting attribute bit 7. The earlier model put the
/// GRAPH tables in bank 1, which is why issue #3 looked like a missing
/// attribute write (v1.5 Phase 2.3).
///
/// Unshifted entries win on duplicates so the simpler keystroke is the
/// canonical way to reach a given glyph. Modes are independent.
/// </summary>
public sealed class Mz700KeyTables
{
    private const int AlphaUnshiftedOffset = 0x0BEA;
    private const int AlphaShiftedOffset   = 0x0C2A;
    private const int GraphUnshiftedOffset = 0x0C6A;
    private const int GraphShiftedOffset   = 0x0CAA;
    private const int TableLength = 64;

    // S-BASIC 1Z-013B: key tables (ALPHA, ALPHA shifted, GRAPH, GRAPH
    // shifted, back to back) and its ASCII → display-code table.
    private const ushort SBasicKeyTables = 0x121D;
    private const ushort SBasicAsciiToDisplay = 0x0F1C;   // entry for ASCII $20
    // First row of S-BASIC's ALPHA table, to tell 1Z-013B from any other
    // program in RAM.
    private static readonly byte[] SBasicAlphaRow0 = { 0x90, 0x17, 0xFC, 0x19, 0x00, 0x3B, 0x3A, 0x0D };

    private readonly Dictionary<(byte Code, bool Graph), (int Row, int Col, bool MzShift)> _rom = new();
    private readonly Dictionary<(byte Code, bool Graph), (int Row, int Col, bool MzShift)> _sbasic = new();

    /// <summary>RAM / banking state, to see whether S-BASIC is running.</summary>
    internal Mz700Memory? Memory;

    /// <summary>
    /// (Re)populate the monitor map from a freshly-loaded monitor ROM.
    /// Safe to call repeatedly; clears any prior state first.
    /// </summary>
    internal void Build(byte[] monitorRom)
    {
        _rom.Clear();
        LoadRomTable(monitorRom, AlphaUnshiftedOffset, graph: false, mzShift: false);
        LoadRomTable(monitorRom, AlphaShiftedOffset,   graph: false, mzShift: true);
        LoadRomTable(monitorRom, GraphUnshiftedOffset, graph: true,  mzShift: false);
        LoadRomTable(monitorRom, GraphShiftedOffset,   graph: true,  mzShift: true);
    }

    private void LoadRomTable(byte[] rom, int offset, bool graph, bool mzShift)
    {
        for (int i = 0; i < TableLength && offset + i < rom.Length; i++)
            Add(_rom, i, rom[offset + i], graph, mzShift);
    }

    /// <summary>
    /// Records table entry <paramref name="index"/> as typing
    /// <paramref name="code"/>. Skips slots that are mode / control keys
    /// (ALPHA, GRAPH, Enter, cursors, etc. — see
    /// <see cref="Mz700MatrixReference.SpecialLabels"/>): their bytes are
    /// scan-side markers the keyboard handler intercepts, not display
    /// codes that reach VRAM. Without this filter the map would report
    /// e.g. "$C9 is at slot (0,4)", but pressing (0,4) just selects ALPHA.
    /// </summary>
    private static void Add(Dictionary<(byte, bool), (int, int, bool)> map,
        int index, byte code, bool graph, bool mzShift)
    {
        int row = index / 8;
        int col = 7 - (index % 8);
        if (Mz700MatrixReference.SpecialLabels.ContainsKey((row, col))) return;
        var key = (code, graph);
        if (!map.ContainsKey(key)) map[key] = (row, col, mzShift);
    }

    /// <summary>
    /// True when S-BASIC 1Z-013B owns the machine: monitor ROM banked
    /// out and BASIC's ALPHA key table in place.
    /// </summary>
    private bool SBasicRunning()
    {
        if (Memory is not { RomEnabled: false } mem) return false;
        for (int i = 0; i < SBasicAlphaRow0.Length; i++)
            if (mem.Ram[SBasicKeyTables + i] != SBasicAlphaRow0[i]) return false;
        return true;
    }

    private void BuildSBasic(Mz700Memory mem)
    {
        _sbasic.Clear();
        for (int t = 0; t < 4; t++)
        {
            bool graph = t >= 2, mzShift = (t & 1) != 0;
            for (int i = 0; i < TableLength; i++)
            {
                // Below $20 are control codes (cursor moves, CR, nothing
                // at all for $00), not characters.
                byte ascii = mem.Ram[SBasicKeyTables + t * TableLength + i];
                if (ascii < 0x20) continue;
                Add(_sbasic, i, mem.Ram[SBasicAsciiToDisplay + ascii - 0x20], graph, mzShift);
            }
        }
    }

    /// <summary>
    /// Returns the slot that types the given bank-0 display code in the
    /// given keyboard mode (false = ALPHA, true = GRAPH), or null if no
    /// slot does. Uses S-BASIC's own tables while it is running, else
    /// the monitor ROM's. Mode matters because the same matrix slot
    /// produces different display codes in ALPHA and GRAPH mode.
    /// </summary>
    public (int Row, int Col, bool MzShift)? FindByDisplayCode(byte code, bool graphMode)
    {
        var map = _rom;
        if (SBasicRunning())
        {
            // BASIC's tables never change while it runs; build once.
            if (_sbasic.Count == 0) BuildSBasic(Memory!);
            map = _sbasic;
        }
        else
        {
            _sbasic.Clear();
        }
        return map.TryGetValue((code, graphMode), out var slot) ? slot : null;
    }
}
