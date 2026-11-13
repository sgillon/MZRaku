using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Reverse lookup from MZ-700 display code to the (row, col, MzShift)
/// slot that types it in S-BASIC 1Z-013B, per keyboard mode. Feeds the
/// Font Sheet's click-to-type, which works in BASIC only (v1.5 4.8a).
///
/// S-BASIC runs with the monitor ROM banked out and scans the keyboard
/// with its own tables (found by disassembly, v1.5 Phase 2.3 and 4.8a):
/// <list type="bullet">
///   <item>Four 64-byte tables at $121D, in Sharp ASCII: ALPHA, ALPHA
///   shifted, GRAPH, GRAPH shifted. Index <c>row*8 + (7 - col)</c>,
///   rows 0–7 (rows 8 and 9 hold modifier and function keys).</item>
///   <item>Characters reach the screen through BASIC's ASCII → display
///   table at $0F1C (ASCII $20–$FF).</item>
///   <item>The mode is $0060, which is also the cursor glyph: $EF ALPHA,
///   $43 shift lock (SHIFT counts inverted for the ALPHA tables), $FF
///   GRAPH. GRAPH selects GRAPH from any mode; ALPHA selects ALPHA, or
///   shift lock when pressed in shift lock; SHIFT+ALPHA selects shift
///   lock, or ALPHA when pressed in shift lock.</item>
/// </list>
///
/// Every code these tables give is a font bank-0 code: GRAPH mode picks
/// a different table, not a different bank. Bank 1 is reached only by a
/// program setting attribute bit 7 (issue #3).
///
/// Unshifted presses win on duplicates, so the simpler keystroke is the
/// canonical way to reach a given glyph. Modes are independent.
/// </summary>
public sealed class Mz700KeyTables
{
    private const int TableLength = 64;
    private const ushort SBasicKeyTables = 0x121D;
    private const ushort SBasicAsciiToDisplay = 0x0F1C;   // entry for ASCII $20
    private const ushort SBasicModeFlag = 0x0060;
    private const byte AlphaCursor = 0xEF, ShiftLockCursor = 0x43, GraphCursor = 0xFF;
    // First row of S-BASIC's ALPHA table, to tell 1Z-013B from any other
    // program in RAM.
    private static readonly byte[] SBasicAlphaRow0 = { 0x90, 0x17, 0xFC, 0x19, 0x00, 0x3B, 0x3A, 0x0D };

    private readonly Dictionary<(byte Code, int Mode), (int Row, int Col, bool MzShift)> _map = new();

    /// <summary>RAM / banking state, to see whether S-BASIC is running.</summary>
    internal Mz700Memory? Memory;

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

    /// <summary>
    /// S-BASIC's keyboard mode: 0 ALPHA, 1 shift lock, 2 GRAPH. Null
    /// while S-BASIC isn't running, or if the flag holds anything else.
    /// </summary>
    public int? BasicKeyboardMode
    {
        get
        {
            if (!SBasicRunning()) return null;
            return Memory!.Ram[SBasicModeFlag] switch
            {
                AlphaCursor => 0,
                ShiftLockCursor => 1,
                GraphCursor => 2,
                _ => null,
            };
        }
    }

    private void Build(Mz700Memory mem)
    {
        _map.Clear();
        for (int mode = 0; mode <= 2; mode++)
        foreach (bool pressed in new[] { false, true })
        {
            // Shift lock inverts SHIFT for the ALPHA tables.
            bool shift = mode == 1 ? !pressed : pressed;
            int table = (mode == 2 ? 2 : 0) + (shift ? 1 : 0);
            for (int i = 0; i < TableLength; i++)
            {
                int row = i / 8, col = 7 - i % 8;
                // Mode / control keys (ALPHA, GRAPH, Enter, cursors...)
                // give control codes, not characters.
                if (Mz700MatrixReference.SpecialLabels.ContainsKey((row, col))) continue;
                // Below $20 are control codes (cursor moves, CR, nothing
                // at all for $00), not characters.
                byte ascii = mem.Ram[SBasicKeyTables + table * TableLength + i];
                if (ascii < 0x20) continue;
                var key = (mem.Ram[SBasicAsciiToDisplay + ascii - 0x20], mode);
                if (!_map.ContainsKey(key)) _map[key] = (row, col, pressed);
            }
        }
    }

    /// <summary>
    /// Returns the slot that types the given bank-0 display code in
    /// S-BASIC keyboard mode <paramref name="mode"/> (0 ALPHA, 1 shift
    /// lock, 2 GRAPH), or null if no slot does or S-BASIC isn't running.
    /// </summary>
    public (int Row, int Col, bool MzShift)? FindByDisplayCode(byte code, int mode)
    {
        if (!SBasicRunning())
        {
            _map.Clear();
            return null;
        }
        // BASIC's tables never change while it runs; build once.
        if (_map.Count == 0) Build(Memory!);
        return _map.TryGetValue((code, mode), out var slot) ? slot : null;
    }
}
