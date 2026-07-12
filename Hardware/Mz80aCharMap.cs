using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-80A analogue of <see cref="CharMap"/>: static translation from the
/// Unicode character a PC keystroke resolves to (after host-OS layout,
/// AltGr, dead-key handling) to an MZ-80A matrix strobe/bit position
/// plus MZ-shift state. Lets the keyboard matrix be driven by the CHAR
/// the OS produced rather than by a hard-wired PC-VK binding, so a UK
/// PC's Shift+2 → '"' lands on the MZ-80A '"' slot regardless of the
/// physical key that emitted it.
///
/// Defaults are derived from <see cref="Mz80aMatrixReference"/>'s Char
/// slots, so this map cannot drift from the canonical matrix without a
/// startup validator flagging it (see <see cref="Validate"/>).
///
/// MZ-80A text-mode convention: unshifted = UPPERCASE, shifted =
/// lowercase (opposite of PC). Following the MZ-700 CharMap policy,
/// PC 'a' and PC 'A' both map to the same unshifted slot (uppercase)
/// so plain PC typing produces authentic uppercase MZ-80A text. Users
/// wanting PC-style casing can add per-char overrides.
/// </summary>
public static class Mz80aCharMap
{
    public readonly record struct Press(int Strobe, int Bit, bool MzShift);

    public static readonly IReadOnlyDictionary<char, Press> Defaults = BuildDefaults();

    private static Dictionary<char, Press> BuildDefaults()
    {
        var m = new Dictionary<char, Press>();

        // Walk the canonical matrix. Every Char slot contributes up to
        // two entries: one for its unshifted glyph, one for its shifted
        // glyph. Numeric-pad slots are skipped — they collide with the
        // main-row digits and need a separate PC-keypad wiring pass
        // (Phase B).
        foreach (var slot in Mz80aMatrixReference.All.Values)
        {
            if (slot.Kind != Mz80aMatrixReference.SlotKind.Char) continue;
            if (slot.Id.StartsWith("NP")) continue;
            if (!string.IsNullOrEmpty(slot.UnshiftedGlyph))
            {
                foreach (char c in slot.UnshiftedGlyph!)
                    m[c] = new Press(slot.Strobe, slot.Bit, false);
            }
            if (!string.IsNullOrEmpty(slot.ShiftedGlyph))
            {
                foreach (char c in slot.ShiftedGlyph!)
                {
                    // First-wins on collisions so a direct unshifted
                    // entry beats a shifted-elsewhere alias (e.g. '/'
                    // sits at SLASH unshifted AND D7 shifted — prefer
                    // SLASH so no gratuitous MzShift is asserted).
                    if (!m.ContainsKey(c))
                        m[c] = new Press(slot.Strobe, slot.Bit, true);
                }
            }
        }

        // Letter case: authentic MZ-80A convention is unshifted-key =
        // UPPERCASE glyph, shifted-key = lowercase (opposite of PC).
        // The reference lists uppercase as UnshiftedGlyph, so the pass
        // above set 'A'..'Z' → (slot, MzShift=false). Rewire so the
        // PC-produced chars route naturally:
        //   PC 'a' unshifted  → (slot, MzShift=false) → MZ unshifted → 'A'
        //   PC 'A' from Shift → (slot, MzShift=true)  → MZ shifted   → 'a'
        // Every letter now carries an explicit MzShift, which also
        // eliminates the shift/key-release race the pre-2026-07-12
        // both-cases-unshifted layout exposed with SHIFT held across a
        // whole word.
        for (char up = 'A'; up <= 'Z'; up++)
        {
            if (m.TryGetValue(up, out var p))
            {
                m[char.ToLowerInvariant(up)] = new Press(p.Strobe, p.Bit, false);
                m[up] = new Press(p.Strobe, p.Bit, true);
            }
        }

        // Space isn't a Char slot in the reference — wire it explicitly.
        m[' '] = new Press(4, 0, false);

        // UK-layout shifted-digit chars that don't exist on the MZ-80A:
        // fall back to the MATRIX POSITION of the equivalent shifted-digit
        // on the MZ keyboard, so the user gets the MZ glyph at that
        // position (e.g. UK Shift+3='£' → MZ '#' slot). Mirrors the
        // MZ-700 CharMap approach.
        m['£'] = new Press(2, 6, true);   // UK Shift+3 → MZ '#' slot

        // MZ-80A has no apostrophe glyph in the matrix. Route PC ' to
        // the AT slot's shifted glyph (backtick) so a useful char lands
        // where the PC key would otherwise be dead. Backtick is also
        // reachable from the AT slot itself (PC Shift+' → char '@' on
        // UK, but that produces the '@' unshifted here — see AT entry
        // above); this alias just gives it a dedicated PC key.
        m['\''] = new Press(6, 4, true);

        return m;
    }

    /// <summary>
    /// Optional override layer set by the host application (typically
    /// <c>MainForm</c> from <c>Settings.Mz80aCharMapOverrides</c>).
    /// When non-null, its entries take precedence over
    /// <see cref="Defaults"/>.
    /// </summary>
    public static Mz80aCharMapOverrides? Overrides;

    public static bool TryLookup(char c, out Press press)
    {
        if (Overrides != null && Overrides.TryLookup(c, out press)) return true;
        if (Overrides != null && Overrides.IsSuppressed(c))
        {
            press = default;
            return false;
        }
        return Defaults.TryGetValue(c, out press);
    }

    /// <summary>
    /// Cross-checks <see cref="Defaults"/> against
    /// <see cref="Mz80aMatrixReference"/> in both directions.
    ///
    /// Char → slot: every default char points at a Char / Space / Enter
    /// slot (never Unused / Modifier / Cursor etc.). Glyph identity is
    /// not checked — fallback mappings (UK '£' → MZ '#' slot) are
    /// intentional and would false-positive a strict glyph match.
    ///
    /// Slot → char (coverage gap): every Char slot in the reference with
    /// a non-empty UnshiftedGlyph is reachable via at least one default
    /// unshifted press, and same for ShiftedGlyph via a shifted press.
    /// Numeric-pad slots (Id starts with "NP") are excluded — they alias
    /// the main-row digits and are wired separately (Phase B note).
    /// A gap here means the user can see a glyph on the keycap that PC
    /// typing cannot reach without a manual [CharMap.MZ80A] override.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();

        // Direction 1: char → slot. Every default entry points at a
        // typeable slot kind.
        foreach (var kv in Defaults)
        {
            if (!Mz80aMatrixReference.All.TryGetValue((kv.Value.Strobe, kv.Value.Bit), out var slot))
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Strobe}, {kv.Value.Bit}) is out of matrix range");
                continue;
            }
            var k = slot.Kind;
            if (k != Mz80aMatrixReference.SlotKind.Char &&
                k != Mz80aMatrixReference.SlotKind.Space &&
                k != Mz80aMatrixReference.SlotKind.Enter)
            {
                complaints.Add($"Defaults['{kv.Key}'] → ({kv.Value.Strobe}, {kv.Value.Bit}) is {k}; defaults must point at Char / Space / Enter slots");
            }
        }

        // Direction 2: glyph → char-map. Every glyph on a Char slot's
        // keycap is reachable from at least one default entry. Checked
        // by glyph identity (not by matrix position) — a glyph that
        // appears at two slots only needs one default hitting either.
        // Numeric-pad slot glyphs are excluded — they alias main-row
        // digits and are wired separately (Phase B note).
        foreach (var slot in Mz80aMatrixReference.All.Values)
        {
            if (slot.Kind != Mz80aMatrixReference.SlotKind.Char) continue;
            if (slot.Id.StartsWith("NP")) continue;
            CheckGlyphReachable(slot, slot.UnshiftedGlyph, "unshifted", complaints);
            CheckGlyphReachable(slot, slot.ShiftedGlyph, "shifted", complaints);
        }

        return complaints;
    }

    private static void CheckGlyphReachable(
        Mz80aMatrixReference.Slot slot, string? glyph, string polarity, List<string> complaints)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        foreach (char c in glyph)
        {
            if (!Defaults.ContainsKey(c))
                complaints.Add($"Slot '{slot.Id}' ({slot.Strobe}, {slot.Bit}) {polarity} glyph '{c}' is not reachable from any default char");
        }
    }
}
