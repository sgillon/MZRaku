using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Static translation: Unicode character → MZ-700 matrix position +
/// MZ-shift requirement. Lets us drive the keyboard matrix from the
/// resolved character a PC keystroke produces (after host-OS layout,
/// AltGr, dead-key handling), instead of from a configurable per-VK
/// map.
///
/// Built by walking <see cref="Mz700MatrixReference"/>'s Char slots
/// — every UnshiftedGlyph → (row, col, false), every ShiftedGlyph →
/// (row, col, true) — then applying MZ-700's case policy (default
/// text mode is uppercase, so lowercase PC letters alias the same
/// unshifted slot), a small set of collision-preference overrides,
/// and UK-layout fallbacks that route PC characters absent from the
/// MZ keyboard to the same MATRIX POSITION as the equivalent
/// shifted-digit on the MZ (so e.g. UK Shift+3 = '£' produces the
/// MZ '#' at that position).
///
/// v1.2 audit F-022 replaced the previous hand-coded 90-line
/// dictionary with this derivation so the canonical-reference
/// discipline that <see cref="Mz80aCharMap"/> already followed
/// extends to MZ-700 too. Startup <see cref="Validate"/> still
/// catches out-of-range or wrong-kind slots.
///
/// MZ-700 punctuation display codes are NOT ASCII-aligned — e.g.
/// (0,2) produces ';' unshifted and '+' shifted; (6,5) produces '-'
/// unshifted and '=' shifted. The reference is keyed by the GLYPH
/// the MZ-700 produces, so PC ';' lands on (0,2) regardless of the
/// PC keyboard layout that produced it.
/// </summary>
public static class CharMap
{
    public readonly record struct Press(int Row, int Col, bool MzShift);

    /// <summary>
    /// Built-in default character → MZ-matrix mapping. Exposed so
    /// the keyboard-map editor (and other introspective callers)
    /// can enumerate the canonical bindings without re-deriving them.
    /// </summary>
    public static readonly IReadOnlyDictionary<char, Press> Defaults = BuildDefaults();

    private static Dictionary<char, Press> BuildDefaults()
    {
        var m = new Dictionary<char, Press>();

        // Walk the canonical matrix. Every Char slot contributes up
        // to two entries: one per glyph string. First-wins on
        // collisions so a later slot doesn't clobber an earlier
        // choice — precedence overrides below handle the intentional
        // cases where first-in-iteration-order isn't what we want.
        foreach (var slot in Mz700MatrixReference.All.Values)
        {
            if (slot.Kind != Mz700MatrixReference.SlotKind.Char) continue;
            if (!string.IsNullOrEmpty(slot.UnshiftedGlyph))
            {
                foreach (char c in slot.UnshiftedGlyph!)
                    if (!m.ContainsKey(c))
                        m[c] = new Press(slot.Row, slot.Col, false);
            }
            if (!string.IsNullOrEmpty(slot.ShiftedGlyph))
            {
                foreach (char c in slot.ShiftedGlyph!)
                    if (!m.ContainsKey(c))
                        m[c] = new Press(slot.Row, slot.Col, true);
            }
        }

        // MZ-700 case policy: default text mode is uppercase. Send
        // the unshifted matrix position for both cases so plain PC
        // typing produces uppercase MZ output. Lowercase glyphs are
        // reachable via the MZ's own mode switch, not via PC shift.
        for (char up = 'A'; up <= 'Z'; up++)
        {
            if (m.TryGetValue(up, out var p))
                m[char.ToLowerInvariant(up)] = new Press(p.Row, p.Col, false);
        }

        // Space isn't a Char slot (it's kind=Space) — wire it explicitly.
        m[' '] = new Press(6, 4, false);

        // Collision-preference override: "'" appears on BOTH the AT
        // slot (1,5) shifted and the D7 slot (5,1) shifted. The
        // canonical hand-coded map picked D7 so PC users hitting the
        // digit-row shift key land on the visually-recognisable "7"
        // key. Preserved.
        m['\''] = new Press(5, 1, true);

        // UK-layout fallbacks: PC characters that don't exist on the
        // MZ-700 route to the MATRIX POSITION of the equivalent
        // shifted-digit on the MZ keyboard, so the user gets the MZ
        // glyph at that position rather than a dead key.
        m['£'] = new Press(5, 5, true);  // UK Shift+3 → MZ Shift+3 position ('#')
        m['^'] = new Press(5, 2, true);  // UK Shift+6 → MZ Shift+6 position ('&')
        m['<'] = new Press(6, 1, true);  // UK Shift+, → MZ Shift+, position
        m['>'] = new Press(6, 0, true);  // UK Shift+. → MZ Shift+. position

        return m;
    }

    /// <summary>
    /// Optional override layer set by the host application (typically
    /// <c>MainForm</c> from <c>Settings.CharMapOverrides</c>). When
    /// non-null, its entries take precedence over <see cref="Defaults"/>.
    /// </summary>
    public static CharMapOverrides? Overrides;

    public static bool TryLookup(char c, out Press press)
    {
        if (Overrides != null && Overrides.TryLookup(c, out press)) return true;
        // If the user has suppressed this default via the slot
        // editor (binding a different PC char to the slot it used to
        // point at), the default no longer fires either.
        if (Overrides != null && Overrides.IsSuppressed(c))
        {
            press = default;
            return false;
        }
        return Defaults.TryGetValue(c, out press);
    }

    /// <summary>
    /// Cross-checks <see cref="Defaults"/> against
    /// <see cref="Mz700MatrixReference"/>. Returns a list of
    /// complaints; empty means every default char points at a slot
    /// that produces typeable output (Char, Space, or Enter). Glyph
    /// identity isn't checked — fall-back mappings (e.g. UK '£'
    /// pointing at the MZ '#' slot) are intentional and would
    /// false-positive a strict glyph match.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var kv in Defaults)
        {
            var slot = Mz700MatrixReference.Get(kv.Value.Row, kv.Value.Col);
            if (slot is null)
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Row}, {kv.Value.Col}) is out of matrix range");
                continue;
            }
            var k = slot.Value.Kind;
            if (k != Mz700MatrixReference.SlotKind.Char &&
                k != Mz700MatrixReference.SlotKind.Space &&
                k != Mz700MatrixReference.SlotKind.Enter)
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Row}, {kv.Value.Col}) is {k} in the reference; CharMap defaults must point at Char / Space / Enter slots");
            }
        }
        return complaints;
    }
}
