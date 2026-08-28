using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 analogue of <see cref="CharMap"/>: static translation from
/// the Unicode character a PC keystroke resolves to (after host-OS
/// layout, AltGr, dead-key handling) to an MZ-800 matrix (strobe, bit)
/// plus MZ-shift state. Lets the keyboard matrix be driven by the
/// resolved CHAR rather than by a hard-wired PC-VK binding, so UK PC
/// layouts and PC muscle memory both work without per-user VK mapping.
///
/// Built by walking <see cref="Mz800MatrixReference"/>'s Char slots —
/// same discipline MZ-700's CharMap follows against its own reference.
/// Because MZ-800's matrix is nearly identical to MZ-700's, the
/// collision-preference overrides and UK-layout fallbacks below mirror
/// <see cref="CharMap"/> position-for-position.
///
/// MZ-800 text-mode convention matches MZ-700's: default text mode is
/// UPPERCASE, so lowercase PC letters alias the same unshifted slot
/// (PC 'a' and PC 'A' both produce MZ 'A'). Lowercase glyphs are
/// reachable via ALPHA / GRAPH mode toggles, not via PC shift.
/// </summary>
public static class Mz800CharMap
{
    public readonly record struct Press(int Strobe, int Bit, bool MzShift);

    public static readonly IReadOnlyDictionary<char, Press> Defaults = BuildDefaults();

    private static Dictionary<char, Press> BuildDefaults()
    {
        var m = new Dictionary<char, Press>();

        // Walk the canonical matrix. Every Char slot contributes up
        // to two entries: one per glyph string. First-wins on
        // collisions.
        foreach (var slot in Mz800MatrixReference.All.Values)
        {
            if (slot.Kind != Mz800MatrixReference.SlotKind.Char) continue;
            if (!string.IsNullOrEmpty(slot.UnshiftedGlyph))
            {
                foreach (char c in slot.UnshiftedGlyph!)
                    if (!m.ContainsKey(c))
                        m[c] = new Press(slot.Strobe, slot.Bit, false);
            }
            if (!string.IsNullOrEmpty(slot.ShiftedGlyph))
            {
                foreach (char c in slot.ShiftedGlyph!)
                    if (!m.ContainsKey(c))
                        m[c] = new Press(slot.Strobe, slot.Bit, true);
            }
        }

        // MZ-800 case policy: same as MZ-700 — default text mode is
        // uppercase, so lowercase PC letters alias the unshifted slot.
        for (char up = 'A'; up <= 'Z'; up++)
        {
            if (m.TryGetValue(up, out var p))
                m[char.ToLowerInvariant(up)] = new Press(p.Strobe, p.Bit, false);
        }

        // Space is a Space-kind slot, wired explicitly.
        m[' '] = new Press(6, 4, false);

        // Collision-preference override: "'" appears on both the AT
        // slot (1, 5) shifted and the D7 slot (5, 1) shifted. Prefer
        // D7 so PC users hitting the digit-row shift key land on the
        // visually-recognisable "7" position. Matches MZ-700.
        m['\''] = new Press(5, 1, true);

        // UK-layout fallbacks: PC characters that don't exist on the
        // MZ-800 route to the MATRIX POSITION of the equivalent
        // shifted-digit on the MZ keyboard, so the user gets an MZ
        // glyph at that position rather than a dead key. Same
        // positions as MZ-700 (matrices are position-identical for
        // the digit row).
        m['£'] = new Press(5, 5, true);  // UK Shift+3 → MZ '#'
        m['^'] = new Press(5, 2, true);  // UK Shift+6 → MZ '&'
        m['<'] = new Press(6, 1, true);  // UK Shift+, → MZ ','-slot shifted
        m['>'] = new Press(6, 0, true);  // UK Shift+. → MZ '.'-slot shifted

        return m;
    }

    // Override layer (Settings.Mz800CharMapOverrides + a
    // MatrixOverrides<Press> subclass) arrives with Phase 8's
    // settings-dialog upgrade. Phase 3 keyboard bring-up only needs
    // the walked-reference defaults.

    public static bool TryLookup(char c, out Press press)
        => Defaults.TryGetValue(c, out press);

    /// <summary>
    /// Cross-checks <see cref="Defaults"/> against
    /// <see cref="Mz800MatrixReference"/>. Returns human-readable
    /// complaints; empty means every default char points at a
    /// typeable slot (Char, Space, or Enter). Glyph identity is not
    /// checked — UK fallbacks (£ → MZ '#') are intentional and would
    /// false-positive a strict glyph match. Mirrors
    /// <see cref="CharMap.Validate"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var kv in Defaults)
        {
            var slot = Mz800MatrixReference.Get(kv.Value.Strobe, kv.Value.Bit);
            if (slot is null)
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Strobe}, {kv.Value.Bit}) is out of matrix range");
                continue;
            }
            var k = slot.Value.Kind;
            if (k != Mz800MatrixReference.SlotKind.Char &&
                k != Mz800MatrixReference.SlotKind.Space &&
                k != Mz800MatrixReference.SlotKind.Enter)
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Strobe}, {kv.Value.Bit}) is {k}; defaults must point at Char / Space / Enter slots");
            }
        }
        return complaints;
    }
}
