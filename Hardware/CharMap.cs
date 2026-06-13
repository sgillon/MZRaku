using System.Collections.Generic;

namespace MZ700Emul.Hardware;

/// <summary>
/// Static translation: Unicode character → MZ-700 matrix position +
/// MZ-shift requirement. Lets us drive the keyboard matrix from the
/// resolved character a PC keystroke produces (after host-OS layout,
/// AltGr, dead-key handling), instead of from a configurable per-VK map.
///
/// Built from the MZ-700 ROM key-translation tables at $0BEA (unshifted)
/// and $0C2A (shifted) in 1z-013a.rom. Scan formula from the ROM scan
/// routine at $0A50: index = row*8 + (7 - col).
///
/// MZ-700 punctuation display codes are NOT ASCII-aligned — e.g. (0,2)
/// produces ';' unshifted and '+' shifted; (6,5) produces '-' unshifted
/// and '=' shifted. The mapping below is keyed by the GLYPH the MZ-700
/// produces, so PC ';' lands on (0,2) regardless of the PC keyboard
/// layout that produced it.
/// </summary>
public static class CharMap
{
    public readonly record struct Press(int Row, int Col, bool MzShift);

    /// <summary>
    /// Built-in default character → MZ-matrix mapping. Exposed so the
    /// keyboard-map editor (and other introspective callers) can enumerate
    /// the canonical bindings without re-deriving them.
    /// </summary>
    public static readonly IReadOnlyDictionary<char, Press> Defaults = new Dictionary<char, Press>
    {
        // Letters. MZ-700 default text mode is uppercase, so we send the
        // unshifted matrix position for both cases. Lowercase glyphs are
        // reachable via the MZ's own mode switch, not via PC shift.
        ['A'] = new(4, 7, false), ['a'] = new(4, 7, false),
        ['B'] = new(4, 6, false), ['b'] = new(4, 6, false),
        ['C'] = new(4, 5, false), ['c'] = new(4, 5, false),
        ['D'] = new(4, 4, false), ['d'] = new(4, 4, false),
        ['E'] = new(4, 3, false), ['e'] = new(4, 3, false),
        ['F'] = new(4, 2, false), ['f'] = new(4, 2, false),
        ['G'] = new(4, 1, false), ['g'] = new(4, 1, false),
        ['H'] = new(4, 0, false), ['h'] = new(4, 0, false),
        ['I'] = new(3, 7, false), ['i'] = new(3, 7, false),
        ['J'] = new(3, 6, false), ['j'] = new(3, 6, false),
        ['K'] = new(3, 5, false), ['k'] = new(3, 5, false),
        ['L'] = new(3, 4, false), ['l'] = new(3, 4, false),
        ['M'] = new(3, 3, false), ['m'] = new(3, 3, false),
        ['N'] = new(3, 2, false), ['n'] = new(3, 2, false),
        ['O'] = new(3, 1, false), ['o'] = new(3, 1, false),
        ['P'] = new(3, 0, false), ['p'] = new(3, 0, false),
        ['Q'] = new(2, 7, false), ['q'] = new(2, 7, false),
        ['R'] = new(2, 6, false), ['r'] = new(2, 6, false),
        ['S'] = new(2, 5, false), ['s'] = new(2, 5, false),
        ['T'] = new(2, 4, false), ['t'] = new(2, 4, false),
        ['U'] = new(2, 3, false), ['u'] = new(2, 3, false),
        ['V'] = new(2, 2, false), ['v'] = new(2, 2, false),
        ['W'] = new(2, 1, false), ['w'] = new(2, 1, false),
        ['X'] = new(2, 0, false), ['x'] = new(2, 0, false),
        ['Y'] = new(1, 7, false), ['y'] = new(1, 7, false),
        ['Z'] = new(1, 6, false), ['z'] = new(1, 6, false),

        // Digits and their typical shifted forms. The shifted glyphs are
        // those produced by the MZ-700 ROM's $0C2A table when shift is
        // held; if any prove to be wrong on a specific layout, fix the
        // entry here rather than via per-user mapping.
        ['1'] = new(5, 7, false), ['!'] = new(5, 7, true),
        ['2'] = new(5, 6, false), ['"'] = new(5, 6, true),
        ['3'] = new(5, 5, false), ['#'] = new(5, 5, true),
        ['4'] = new(5, 4, false), ['$'] = new(5, 4, true),
        ['5'] = new(5, 3, false), ['%'] = new(5, 3, true),
        ['6'] = new(5, 2, false), ['&'] = new(5, 2, true),
        ['7'] = new(5, 1, false), ['\''] = new(5, 1, true),
        ['8'] = new(5, 0, false), ['('] = new(5, 0, true),
        ['9'] = new(6, 2, false), [')'] = new(6, 2, true),
        ['0'] = new(6, 3, false),

        // UK-layout shifted-digit characters that don't exist on the
        // MZ-700: fall back to the same MATRIX POSITION as the
        // equivalent shifted-digit on the MZ keyboard, so the user gets
        // the MZ glyph at that position (e.g. UK Shift+3='£' → MZ '#').
        ['£'] = new(5, 5, true),  // UK Shift+3 → MZ Shift+3 position ('#')
        ['^'] = new(5, 2, true),  // UK Shift+6 → MZ Shift+6 position ('&')
        ['<'] = new(6, 1, true),  // UK Shift+, → MZ Shift+, position
        ['>'] = new(6, 0, true),  // UK Shift+. → MZ Shift+. position

        // Asterisk lives on shift-colon on the MZ-700 (verified against
        // ROM shifted table at $0C30: code $6B = `*` glyph). Previously
        // mapped to (5,0) shifted which is MZ shift-8 and produces `(`.
        ['*'] = new(0, 1, true),

        // Up-arrow glyph (unshifted) and tilde (shifted) on the digit
        // row's "up-arrow" key between `-` and `\`. Slot (6,6) — picked
        // because it's the unassigned row-6 position between (6,5)='-'
        // and (6,7)='\'. Distinct from the cursor-up VK at (7,5), which
        // is a non-printable cursor control.
        ['↑'] = new(6, 6, false), // ↑
        ['~']      = new(6, 6, true),

        // Square brackets and curly braces — row 1 of the matrix carries
        // these alongside Y/Z. Slots reconciled 2026-06-13 against the
        // owner's manual via Mz700MatrixReference: ] at (1, 3) and [ at
        // (1, 4) — earlier code had these swapped.
        [']'] = new(1, 3, false),
        ['['] = new(1, 4, false),
        ['}'] = new(1, 3, true),
        ['{'] = new(1, 4, true),

        // Punctuation. Positions chosen by the GLYPH the MZ-700 produces
        // at that matrix slot, not by ASCII alignment.
        [','] = new(6, 1, false),
        ['.'] = new(6, 0, false),
        [';'] = new(0, 2, false),
        ['+'] = new(0, 2, true),
        [':'] = new(0, 1, false),
        ['-'] = new(6, 5, false),
        ['='] = new(6, 5, true),
        ['/'] = new(7, 0, false),
        ['?'] = new(7, 1, false),
        // @ at (1, 5) per the owner's manual (reconciled 2026-06-13 via
        // Mz700MatrixReference). Earlier code had this at (9, 3) which
        // is the F5 slot — sending '@' from PC silently pressed F5
        // until now.
        ['@'] = new(1, 5, false),
        [' '] = new(6, 4, false),
        ['\\'] = new(6, 7, false),
    };

    /// <summary>
    /// Optional override layer set by the host application (typically
    /// <c>MainForm</c> from <c>Settings.CharMapOverrides</c>). When non-null,
    /// its entries take precedence over <see cref="Defaults"/>.
    /// </summary>
    public static CharMapOverrides? Overrides;

    public static bool TryLookup(char c, out Press press)
    {
        if (Overrides != null && Overrides.TryLookup(c, out press)) return true;
        return Defaults.TryGetValue(c, out press);
    }

    /// <summary>
    /// Cross-checks <see cref="Defaults"/> against
    /// <see cref="Mz700MatrixReference"/>. Returns a list of complaints;
    /// empty means every default char points at a slot that produces
    /// typeable output (Char, Space, or Enter). Glyph identity isn't
    /// checked — fall-back mappings (e.g. UK '£' pointing at the MZ
    /// '#' slot) are intentional and would false-positive a strict
    /// glyph match.
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
