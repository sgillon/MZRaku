using System.Collections.Generic;

namespace MZ700Emul.Hardware;

/// <summary>
/// Reverse lookup from MZ-700 display code to the (row, col, MzShift)
/// slot that produces it on the keyboard. Built by reading the monitor
/// ROM's key-translation tables at $0BEA (unshifted) and $0C2A (shifted).
///
/// Scan formula from the ROM scan routine at $0A50:
/// <c>index = row*8 + (7 - col)</c>. Each table is 64 bytes covering
/// rows 0–7 — rows 8 and 9 hold modifier and function keys that don't
/// produce display codes directly.
///
/// Unshifted entries win on duplicates so the simpler keystroke is the
/// canonical way to reach a given glyph.
/// </summary>
public sealed class RomKeyTables
{
    private const int UnshiftedTableOffset = 0x0BEA;
    private const int ShiftedTableOffset = 0x0C2A;
    private const int TableLength = 64;

    private readonly Dictionary<byte, (int Row, int Col, bool MzShift)> _byCode = new();

    /// <summary>
    /// (Re)populate the inverse map from a freshly-loaded monitor ROM.
    /// Safe to call repeatedly; clears any prior state first.
    ///
    /// Skips slots that are mode / control keys (ALPHA, GRAPH, Enter,
    /// cursors, etc. — see <see cref="SpecialKeyMap.SlotLabels"/>):
    /// their bytes in the table are scan-side markers the ROM's
    /// keyboard handler intercepts, not display codes that reach VRAM.
    /// Without this filter the inverse map happily reports e.g. "$C9 is
    /// at slot (0,4)" — but pressing (0,4) just toggles ALPHA mode.
    /// </summary>
    public void Build(byte[] monitorRom)
    {
        _byCode.Clear();
        for (int i = 0; i < TableLength && UnshiftedTableOffset + i < monitorRom.Length; i++)
        {
            int row = i / 8;
            int col = 7 - (i % 8);
            if (SpecialKeyMap.SlotLabels.ContainsKey((row, col))) continue;
            byte code = monitorRom[UnshiftedTableOffset + i];
            if (!_byCode.ContainsKey(code)) _byCode[code] = (row, col, false);
        }
        for (int i = 0; i < TableLength && ShiftedTableOffset + i < monitorRom.Length; i++)
        {
            int row = i / 8;
            int col = 7 - (i % 8);
            if (SpecialKeyMap.SlotLabels.ContainsKey((row, col))) continue;
            byte code = monitorRom[ShiftedTableOffset + i];
            if (!_byCode.ContainsKey(code)) _byCode[code] = (row, col, true);
        }
    }

    /// <summary>Returns the slot that produces the given display code, or null if no slot does.</summary>
    public (int Row, int Col, bool MzShift)? FindByDisplayCode(byte code) =>
        _byCode.TryGetValue(code, out var slot) ? slot : null;

    public int Count => _byCode.Count;

    public IEnumerable<KeyValuePair<byte, (int Row, int Col, bool MzShift)>> All => _byCode;
}
