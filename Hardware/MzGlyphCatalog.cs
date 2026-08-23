using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Reverse-lookup helper for MZ-700 glyphs: given a matrix slot +
/// shift state, return the canonical printable char that slot
/// produces (per <see cref="CharMap.Defaults"/>). Feeds the shared
/// keyboard-diagram / matrix-grid label rendering.
///
/// Special-slot labels for the non-printable keys (cursors, BREAK,
/// F-keys, mode keys) come straight from
/// <see cref="Mz700MatrixReference.FindSpecialLabel"/>.
///
/// Limitation: only covers glyphs the host can produce as a Unicode
/// char (whatever's in CharMap.Defaults). MZ-only graphics blocks /
/// kana are reachable from the MZ keyboard but have no Unicode
/// equivalent — those need the ROM key-translation tables at $0BEA
/// / $0C2A in 1z-013a.rom to reverse (see <see cref="RomKeyTables"/>).
/// </summary>
public static class MzGlyphCatalog
{
    // First-wins over CharMap.Defaults' declaration order: 'A' comes
    // before 'a', '1' before '!' — so uppercase becomes the canonical
    // glyph per slot, matching the MZ-700's default text mode.
    private static readonly Dictionary<(int Row, int Col, bool MzShift), char> _glyphBySlot =
        BuildGlyphBySlot();

    private static Dictionary<(int, int, bool), char> BuildGlyphBySlot()
    {
        var m = new Dictionary<(int, int, bool), char>();
        foreach (var kv in CharMap.Defaults)
        {
            var slot = (kv.Value.Row, kv.Value.Col, kv.Value.MzShift);
            if (!m.ContainsKey(slot)) m[slot] = kv.Key;
        }
        return m;
    }

    /// <summary>Returns the canonical printable glyph at the given slot, or null if it has no printable glyph.</summary>
    public static char? FindByPrintableSlot(int row, int col, bool mzShift) =>
        _glyphBySlot.TryGetValue((row, col, mzShift), out var ch) ? ch : null;

    /// <summary>Returns the friendly label for a non-printable slot, or null if the slot has no special-key label.</summary>
    public static string? FindSpecialLabel(int row, int col) =>
        Mz700MatrixReference.FindSpecialLabel(row, col);
}
