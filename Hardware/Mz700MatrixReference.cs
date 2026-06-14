using System.Collections.Generic;

namespace MZ700Emul.Hardware;

/// <summary>
/// Canonical MZ-700 keyboard matrix reference — the single source of
/// truth for "which slot holds what." Every other matrix-aware piece of
/// the codebase (SpecialKeyMap, MzKeyboardLayout, CharMap,
/// tools/RomAnalyse) is expected to derive from or validate against
/// this table.
///
/// Until this file existed, the same (row, col) coordinates were
/// re-encoded independently in four places, which let drift go silently
/// undetected — the CTRL = (9, 2) → (8, 6) bug fixed on 2026-06-12 had
/// been latent for weeks because nothing forced the codebase's
/// coordinates to be cross-checked against the manual or each other.
///
/// SOURCE: Sharp MZ-700 Owner's Manual key-matrix table. Manual is not
/// in the repo (Sharp copyright — see [[reference-docs]]); the user
/// holds a local copy.
///
/// MATRIX SHAPE: 10 rows × 8 cols. Scanned by the keyboard ROM routine
/// at $0A50 with the ROM key-translation tables at $0BEA (ALPHA
/// unshifted), $0C2A (ALPHA shifted), $0C6A (GRAPH unshifted) and $0CAA
/// (GRAPH shifted). Table index formula: row*8 + (7 - col).
///
/// CONFIDENCE: cells marked <see cref="SlotKind.Unknown"/> have not yet
/// been confirmed against the manual. <see cref="Validate"/> reports
/// them at startup so they show up as actionable items rather than
/// silent gaps.
/// </summary>
public static class Mz700MatrixReference
{
    public enum SlotKind
    {
        /// <summary>Produces a glyph. Has unshifted + shifted ROM codes.</summary>
        Char,
        /// <summary>F1..F5.</summary>
        Function,
        /// <summary>SHIFT, CTRL — held while another key is pressed.</summary>
        Modifier,
        /// <summary>GRAPH, ALPHA — one-shot mode toggle.</summary>
        Mode,
        /// <summary>BREAK, INST, DEL.</summary>
        Edit,
        /// <summary>←, →, ↑, ↓.</summary>
        Cursor,
        /// <summary>CR (the wide return key).</summary>
        Enter,
        /// <summary>SPACE bar.</summary>
        Space,
        /// <summary>Cell exists in the scan grid but no physical key sits there.</summary>
        Unused,
        /// <summary>Physical key cap with no function — the unlabelled filler dummy at (0, 7).</summary>
        Blank,
        /// <summary>Not yet confirmed against the owner's manual. Flagged at startup.</summary>
        Unknown,
    }

    /// <summary>
    /// One cell in the scan matrix. Char cells carry their unshifted and
    /// shifted display glyphs (as the strings the diagram and the
    /// keyboard editor render). For non-Char cells <see cref="Id"/> is
    /// the only label (e.g. "CTRL", "F5", "←").
    /// </summary>
    public readonly record struct Slot(
        int Row,
        int Col,
        SlotKind Kind,
        string Id,
        string? UnshiftedGlyph = null,
        string? ShiftedGlyph = null);

    public const int Rows = 10;
    public const int Cols = 8;

    // Cell map. Every (row, col) pair in [0..9] × [0..7] has an entry —
    // including Unused and Unknown — so callers can enumerate the whole
    // grid without worrying about missing keys.
    public static readonly IReadOnlyDictionary<(int row, int col), Slot> All = BuildAll();

    private static Dictionary<(int row, int col), Slot> BuildAll()
    {
        var m = new Dictionary<(int, int), Slot>(Rows * Cols);

        // ============================================================
        // Row 0 — RETURN, mode keys, punctuation cluster.
        // ============================================================
        Put(m, 0, 0, SlotKind.Enter, "CR");
        Put(m, 0, 1, SlotKind.Char,  "COLON", ":", "*");
        Put(m, 0, 2, SlotKind.Char,  "SEMI",  ";", "+");
        // (0, 3) — empty in the scan matrix per owner's manual
        // 2026-06-13. MzKeyboardLayout previously had the @ key wired
        // here, which was wrong — @ actually lives at (1, 5).
        Put(m, 0, 3, SlotKind.Unused, "row0col3");
        Put(m, 0, 4, SlotKind.Mode,  "ALPHA");
        // POUND key shows ↓ unshifted (the MZ display-↓ glyph, not the
        // cursor-down VK at (7, 4)) and £ shifted, per MzKeyboardLayout.
        Put(m, 0, 5, SlotKind.Char,  "POUND", "↓", "£");
        Put(m, 0, 6, SlotKind.Mode,  "GRAPH");
        // (0, 7) — physical "blank" dummy filler cap on the QWERTY row,
        // no MZ function out of the box. Confirmed against owner's
        // manual 2026-06-13. NOTE: this slot IS a real, scannable matrix
        // position — repurposable if a PC→MZ mapping ever needs an
        // unused-but-reachable slot for a custom binding.
        Put(m, 0, 7, SlotKind.Blank, "BLANK");

        // ============================================================
        // Row 1 — Y, Z, square brackets, and the @ key. Confirmed
        // against owner's manual 2026-06-13. NOTE: (1, 5) = @ supersedes
        // the prior (0, 3) assumption baked into MzKeyboardLayout —
        // that file needs reconciling.
        // ============================================================
        Put(m, 1, 0, SlotKind.Unused, "row1col0");
        Put(m, 1, 1, SlotKind.Unused, "row1col1");
        Put(m, 1, 2, SlotKind.Unused, "row1col2");
        // Brackets: ] at (1, 3), [ at (1, 4) per owner's manual
        // 2026-06-13. CharMap.Defaults has them the other way around —
        // another drift to reconcile when wiring.
        Put(m, 1, 3, SlotKind.Char,   "RBRK", "]", "}");
        Put(m, 1, 4, SlotKind.Char,   "LBRK", "[", "{");
        // (1, 5) shifted is the REVERSED-apostrophe glyph at bank 0 code
        // $A4 — a different character from the conventional ' at (5, 1)
        // shifted (bank 0 $67). They render similarly in the editor but
        // CharMap maps PC ' to (5, 1); (1, 5) shifted has no PC binding
        // by design.
        Put(m, 1, 5, SlotKind.Char,   "AT",   "@", "'");
        Put(m, 1, 6, SlotKind.Char,   "Z",    "Z");
        Put(m, 1, 7, SlotKind.Char,   "Y",    "Y");

        // ============================================================
        // Row 2 — QWERTYUWX (col 7..0).
        // ============================================================
        Put(m, 2, 0, SlotKind.Char, "X", "X");
        Put(m, 2, 1, SlotKind.Char, "W", "W");
        Put(m, 2, 2, SlotKind.Char, "V", "V");
        Put(m, 2, 3, SlotKind.Char, "U", "U");
        Put(m, 2, 4, SlotKind.Char, "T", "T");
        Put(m, 2, 5, SlotKind.Char, "S", "S");
        Put(m, 2, 6, SlotKind.Char, "R", "R");
        Put(m, 2, 7, SlotKind.Char, "Q", "Q");

        // ============================================================
        // Row 3 — IJKLMNOP (col 7..0).
        // ============================================================
        Put(m, 3, 0, SlotKind.Char, "P", "P");
        Put(m, 3, 1, SlotKind.Char, "O", "O");
        Put(m, 3, 2, SlotKind.Char, "N", "N");
        Put(m, 3, 3, SlotKind.Char, "M", "M");
        Put(m, 3, 4, SlotKind.Char, "L", "L");
        Put(m, 3, 5, SlotKind.Char, "K", "K");
        Put(m, 3, 6, SlotKind.Char, "J", "J");
        Put(m, 3, 7, SlotKind.Char, "I", "I");

        // ============================================================
        // Row 4 — ABCDEFGH (col 7..0). "E" sits at (4, 3) — slightly
        // out of alphabetical order to match the ROM table.
        // ============================================================
        Put(m, 4, 0, SlotKind.Char, "H", "H");
        Put(m, 4, 1, SlotKind.Char, "G", "G");
        Put(m, 4, 2, SlotKind.Char, "F", "F");
        Put(m, 4, 3, SlotKind.Char, "E", "E");
        Put(m, 4, 4, SlotKind.Char, "D", "D");
        Put(m, 4, 5, SlotKind.Char, "C", "C");
        Put(m, 4, 6, SlotKind.Char, "B", "B");
        Put(m, 4, 7, SlotKind.Char, "A", "A");

        // ============================================================
        // Row 5 — digits 1..8 with their shifted symbols.
        // ============================================================
        Put(m, 5, 0, SlotKind.Char, "D8", "8", "(");
        Put(m, 5, 1, SlotKind.Char, "D7", "7", "'");
        Put(m, 5, 2, SlotKind.Char, "D6", "6", "&");
        Put(m, 5, 3, SlotKind.Char, "D5", "5", "%");
        Put(m, 5, 4, SlotKind.Char, "D4", "4", "$");
        Put(m, 5, 5, SlotKind.Char, "D3", "3", "#");
        Put(m, 5, 6, SlotKind.Char, "D2", "2", "\"");
        Put(m, 5, 7, SlotKind.Char, "D1", "1", "!");

        // ============================================================
        // Row 6 — 9, 0, comma, dot, space, -, ↑/~, \.
        // Shifted glyphs for 0 and \ aren't currently encoded in CharMap
        // — manual to confirm whether the MZ has them.
        // ============================================================
        Put(m, 6, 0, SlotKind.Char,  "DOT",     ".", ">");
        Put(m, 6, 1, SlotKind.Char,  "COMMA",   ",", "<");
        Put(m, 6, 2, SlotKind.Char,  "D9",      "9", ")");
        // (6, 3) shifted = π (bank 0 code $60). Outside the PC→MZ char
        // model — reachable via the click-to-type font viewer rather
        // than via a typed character (confirmed 2026-06-13).
        Put(m, 6, 3, SlotKind.Char,  "D0",      "0", "π");
        Put(m, 6, 4, SlotKind.Space, "SPACE");
        Put(m, 6, 5, SlotKind.Char,  "MINUS",   "-", "=");
        // (6, 6) up-arrow DISPLAY glyph (the printable ↑ character),
        // distinct from the cursor-up arrow at (7, 5).
        Put(m, 6, 6, SlotKind.Char,  "UPARROW", "↑", "~");
        // (6, 7) shifted produces a vertical bar on real hardware but
        // there's no obvious font-sheet match — parked 2026-06-13.
        Put(m, 6, 7, SlotKind.Char,  "BSLASH",  "\\");

        // ============================================================
        // Row 7 — / ? + cursor cluster + DEL/INST.
        // /, ? are on separate keys at cols 0 and 1 (not shift-paired).
        // ============================================================
        Put(m, 7, 0, SlotKind.Char,   "SLASH",  "/");
        Put(m, 7, 1, SlotKind.Char,   "QMARK",  "?");
        Put(m, 7, 2, SlotKind.Cursor, "CLEFT",  "←");
        Put(m, 7, 3, SlotKind.Cursor, "CRIGHT", "→");
        Put(m, 7, 4, SlotKind.Cursor, "CDOWN",  "↓");
        Put(m, 7, 5, SlotKind.Cursor, "CUP",    "↑");
        Put(m, 7, 6, SlotKind.Edit,   "DEL");
        Put(m, 7, 7, SlotKind.Edit,   "INST");

        // ============================================================
        // Row 8 — SHIFT, CTRL, BREAK only; cols 1-5 are empty in the
        // scan matrix (confirmed against owner's manual 2026-06-13).
        // ============================================================
        Put(m, 8, 0, SlotKind.Modifier, "SHIFT");
        Put(m, 8, 1, SlotKind.Unused,   "row8col1");
        Put(m, 8, 2, SlotKind.Unused,   "row8col2");
        Put(m, 8, 3, SlotKind.Unused,   "row8col3");
        Put(m, 8, 4, SlotKind.Unused,   "row8col4");
        Put(m, 8, 5, SlotKind.Unused,   "row8col5");
        Put(m, 8, 6, SlotKind.Modifier, "CTRL");
        Put(m, 8, 7, SlotKind.Edit,     "BREAK");

        // ============================================================
        // Row 9 — function keys F1..F5 in cols 7..3; cols 0-2 are empty
        // in the scan matrix (confirmed against owner's manual
        // 2026-06-13). (9, 2)'s aliased-shifted-F5 behaviour discovered
        // 2026-06-12 in S-BASIC is consistent with it being an unused
        // scan position the ROM decoder happens to fold onto F5.
        // ============================================================
        Put(m, 9, 0, SlotKind.Unused,   "row9col0");
        Put(m, 9, 1, SlotKind.Unused,   "row9col1");
        Put(m, 9, 2, SlotKind.Unused,   "row9col2");
        Put(m, 9, 3, SlotKind.Function, "F5");
        Put(m, 9, 4, SlotKind.Function, "F4");
        Put(m, 9, 5, SlotKind.Function, "F3");
        Put(m, 9, 6, SlotKind.Function, "F2");
        Put(m, 9, 7, SlotKind.Function, "F1");

        return m;
    }

    private static void Put(
        Dictionary<(int, int), Slot> m,
        int row, int col, SlotKind kind, string id,
        string? unshifted = null, string? shifted = null)
        => m[(row, col)] = new Slot(row, col, kind, id, unshifted, shifted);

    /// <summary>Look up a slot by coordinates. Returns null only if out of range.</summary>
    public static Slot? Get(int row, int col)
        => All.TryGetValue((row, col), out var s) ? s : null;

    /// <summary>All slots of a given kind, in (row, col) order.</summary>
    public static IEnumerable<Slot> OfKind(SlotKind kind)
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (All[(r, c)].Kind == kind) yield return All[(r, c)];
    }

    /// <summary>
    /// Self-check: every cell present, no duplicate Ids, and a report
    /// of any <see cref="SlotKind.Unknown"/> cells still awaiting
    /// confirmation. Returns a list of human-readable complaints; an
    /// empty list means the reference is fully populated.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                if (!All.ContainsKey((r, c)))
                    complaints.Add($"Missing cell ({r}, {c})");
            }
        }
        var ids = new HashSet<string>();
        foreach (var s in All.Values)
        {
            if (!ids.Add(s.Id))
                complaints.Add($"Duplicate Slot.Id '{s.Id}' at ({s.Row}, {s.Col})");
        }
        int unknownCount = 0;
        foreach (var s in All.Values)
            if (s.Kind == SlotKind.Unknown) unknownCount++;
        if (unknownCount > 0)
            complaints.Add($"{unknownCount} cell(s) still SlotKind.Unknown — confirm against owner's manual");
        return complaints;
    }
}
