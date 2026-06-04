using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// Aggregated catalog of MZ-700 keyboard slots and the glyphs / labels
/// associated with them. Built once at process start from
/// <see cref="CharMap.Defaults"/> + <see cref="SpecialKeyMap.SlotLabels"/>;
/// feeds the keyboard-map editor's matrix grid and glyph picker.
///
/// Limitation: only covers glyphs the host can produce as a Unicode char
/// (whatever's in CharMap.Defaults). MZ-only graphics blocks / kana are
/// reachable from the MZ keyboard but have no Unicode equivalent — those
/// need the ROM key-translation tables ($0BEA / $0C2A in 1z-013a.rom) to
/// reverse, which the browse-all picker will add when it lands.
/// </summary>
public static class MzGlyphCatalog
{
    /// <summary>A printable MZ glyph and the matrix slot that produces it.</summary>
    public readonly record struct PrintableSlot(char Glyph, int Row, int Col, bool MzShift);

    /// <summary>A non-printable MZ key slot (cursors, BREAK, function keys, mode keys).</summary>
    public readonly record struct SpecialSlot(int Row, int Col, string Label);

    /// <summary>Canonical printable glyph per matrix slot — for the matrix grid's per-cell label.</summary>
    public static IReadOnlyDictionary<(int Row, int Col, bool MzShift), char> GlyphBySlot { get; }

    /// <summary>Inverse of GlyphBySlot: lookup the slot that produces a given printable glyph.</summary>
    public static IReadOnlyDictionary<char, (int Row, int Col, bool MzShift)> SlotByGlyph { get; }

    /// <summary>All printable slots, ordered by (row, col, shift) for stable enumeration.</summary>
    public static IReadOnlyList<PrintableSlot> Printable { get; }

    /// <summary>All non-printable special slots, ordered by (row, col).</summary>
    public static IReadOnlyList<SpecialSlot> Special { get; }

    static MzGlyphCatalog()
    {
        // First-wins: CharMap.Defaults declares 'A' before 'a', '1' before
        // '!' (different slots, no conflict), etc. So walking in declaration
        // order and keeping the first char per slot picks uppercase as the
        // canonical glyph for letters, which matches the MZ-700's default
        // text mode.
        var glyphBySlot = new Dictionary<(int, int, bool), char>();
        foreach (var kv in CharMap.Defaults)
        {
            var slot = (kv.Value.Row, kv.Value.Col, kv.Value.MzShift);
            if (!glyphBySlot.ContainsKey(slot)) glyphBySlot[slot] = kv.Key;
        }
        GlyphBySlot = glyphBySlot;

        // SlotByGlyph: every char in Defaults points to its slot. Multiple
        // chars can target the same slot (e.g. 'A' and 'a'); both entries
        // are kept so FindByGlyph works for either.
        SlotByGlyph = CharMap.Defaults.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.Row, kv.Value.Col, kv.Value.MzShift));

        Printable = glyphBySlot
            .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3)
            .Select(kv => new PrintableSlot(kv.Value, kv.Key.Item1, kv.Key.Item2, kv.Key.Item3))
            .ToList();

        Special = SpecialKeyMap.SlotLabels
            .OrderBy(kv => kv.Key.row).ThenBy(kv => kv.Key.col)
            .Select(kv => new SpecialSlot(kv.Key.row, kv.Key.col, kv.Value))
            .ToList();
    }

    /// <summary>Returns the slot that produces the given printable glyph, or null if no slot does.</summary>
    public static (int Row, int Col, bool MzShift)? FindByGlyph(char c) =>
        SlotByGlyph.TryGetValue(c, out var slot) ? slot : null;

    /// <summary>Returns the canonical printable glyph at the given slot, or null if it has no printable glyph.</summary>
    public static char? FindByPrintableSlot(int row, int col, bool mzShift) =>
        GlyphBySlot.TryGetValue((row, col, mzShift), out var ch) ? ch : null;

    /// <summary>Returns the friendly label for a non-printable slot, or null if the slot has no special-key label.</summary>
    public static string? FindSpecialLabel(int row, int col) =>
        SpecialKeyMap.SlotLabels.TryGetValue((row, col), out var s) ? s : null;
}
