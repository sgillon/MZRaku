using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Canonical MZ-800 keyboard matrix reference — the single source of
/// truth for "which physical key sits at which scan-matrix slot" on the
/// 1Z-016B monitor's keyboard. Modelled on
/// <see cref="Mz80aMatrixReference"/> and <see cref="Mz700MatrixReference"/>.
///
/// SOURCE: Sharp MZ-800 Technical Reference Manual p. 24-25 (chapter 5,
/// Keyboard). The manual documents the electrical matrix as a 10-strobe
/// × 8-bit grid: PA3..PA0 of the 8255 → LS145 3-to-10 decoder → 10 row
/// strobes; PB7..PB0 → 8 column inputs, active-low (a pressed key
/// clears its bit). Manual is not in the repo (Sharp copyright — see
/// [[reference-docs]]); the user holds a local copy.
///
/// COMPARISON TO MZ-700:
/// - Same 10×8 shape and same active-low PPI wiring.
/// - Different key placement. MZ-800 adds a column 10 (strobe 9)
///   entirely for F1..F5 function keys, and column 1 (strobe 0) is
///   rearranged to hold GRAPH / £ / ALPHA / TAB / CR / ; / : plus a
///   spare "blank" cap on D7.
/// - Cursor arrows and INST/DEL live in column 8 (strobe 7).
/// - SHIFT is at strobe 8, D0 (MZ-700 has it at strobe 8, D0 in the
///   code's 0-based numbering; the manual's column-9-D0 = strobe 8 D0).
///
/// STROBE NUMBERING: this class uses 0-based strobes (0..9) throughout.
/// The tech-ref calls them 1..10; add 1 when cross-referencing the
/// diagram.
///
/// CONFIDENCE: transcribed from the p. 25 diagram as reproduced in
/// `_mz800info/MZ800-RESEARCH-2026-08-29.md`, then walked against the
/// tech-ref and owner's manual with the user 2026-08-28 (see
/// [[canonical-reference-pattern]] step 2). Nine ambiguous cells
/// confirmed: POUND / brackets / digit-0 / UPARROW / BSLASH /
/// ALPHA / GRAPH all identical to MZ-700; TAB is an Edit key (owner's
/// manual p. 4-4: "advances the cursor to the next tab stop"), not a
/// mode key; strobe 9 D0-D2 confirmed blank. BSLASH shifted has no
/// modern PC analogue — same handling as MZ-700 (ignored). Cells
/// currently marked <see cref="SlotKind.Unknown"/>, if any, are what
/// <see cref="Validate"/> flags for further confirmation.
/// </summary>
public static class Mz800MatrixReference
{
    public enum SlotKind
    {
        /// <summary>Produces a glyph. Has unshifted + shifted display characters.</summary>
        Char,
        /// <summary>F1..F5.</summary>
        Function,
        /// <summary>SHIFT, CTRL — held while another key is pressed.</summary>
        Modifier,
        /// <summary>GRAPH, ALPHA, TAB — mode / tab keys.</summary>
        Mode,
        /// <summary>BREAK, INST, DEL.</summary>
        Edit,
        /// <summary>Cursor arrows (up / down / left / right).</summary>
        Cursor,
        /// <summary>CR — carriage return.</summary>
        Enter,
        /// <summary>SPACE bar.</summary>
        Space,
        /// <summary>Cell exists in the scan grid but no physical key sits there.</summary>
        Unused,
        /// <summary>Physical key cap with no MZ function out of the box.</summary>
        Blank,
        /// <summary>Not yet confirmed against tech-ref p. 25. Flagged at startup.</summary>
        Unknown,
    }

    public readonly record struct Slot(
        int Strobe,
        int Bit,
        SlotKind Kind,
        string Id,
        string? UnshiftedGlyph = null,
        string? ShiftedGlyph = null);

    public const int Strobes = 10;
    public const int Bits = 8;

    public static readonly IReadOnlyDictionary<(int strobe, int bit), Slot> All = BuildAll();

    /// <summary>
    /// Machine-agnostic view for the shared <see cref="MatrixCoverage"/>
    /// and diagram helpers. Static classes can't implement interfaces
    /// directly, so this singleton delegates back to
    /// <see cref="Mz800MatrixReference"/>'s statics.
    /// </summary>
    public static IMatrixReference View { get; } = new ViewImpl();

    private sealed class ViewImpl : IMatrixReference
    {
        public int Rows => Mz800MatrixReference.Strobes;
        public int Cols => Mz800MatrixReference.Bits;
        // SHIFT is at strobe 8 (manual column 9), D0.
        public (int Row, int Col) ShiftSlot => (8, 0);

        public bool IsKnownUnreachableFromPc(int row, int col, bool mzShift) =>
            Mz800MatrixReference.IsKnownUnreachableFromPc(row, col, mzShift);

        public IEnumerable<MatrixReferenceCell> BindableCells
        {
            get
            {
                foreach (var s in All.Values)
                {
                    if (!IsBindable(s.Kind)) continue;
                    yield return new MatrixReferenceCell(s.Strobe, s.Bit, s.Id, s.UnshiftedGlyph, s.ShiftedGlyph);
                }
            }
        }

        private static bool IsBindable(SlotKind kind) => kind switch
        {
            SlotKind.Char     => true,
            SlotKind.Function => true,
            SlotKind.Modifier => true,
            SlotKind.Mode     => true,
            SlotKind.Edit     => true,
            SlotKind.Cursor   => true,
            SlotKind.Enter    => true,
            SlotKind.Space    => true,
            _ => false,
        };
    }

    private static Dictionary<(int strobe, int bit), Slot> BuildAll()
    {
        var m = new Dictionary<(int, int), Slot>(Strobes * Bits);

        // ============================================================
        // Strobe 0 (manual col 1) — CR, ALPHA/GRAPH/TAB mode keys,
        // £, punctuation. All the "left-edge" keys.
        //
        // Draft interpretation of the multi-glyph cells:
        //   D5 "£↓": ↓ unshifted, £ shifted — mirrors MZ-700's POUND
        //     key at (0, 5) which is Put(SlotKind.Char, "POUND", "↓", "£").
        // ============================================================
        Put(m, 0, 0, SlotKind.Enter,   "CR");
        Put(m, 0, 1, SlotKind.Char,    "COLON", ":", "*");
        Put(m, 0, 2, SlotKind.Char,    "SEMI",  ";", "+");
        // TAB is an EDIT key on MZ-800 — owner's manual p. 4-4:
        // "advances the cursor to the next tab stop position on the
        // display screen." Not a modal toggle like ALPHA / GRAPH.
        // Confirmed with user 2026-08-28.
        Put(m, 0, 3, SlotKind.Edit,    "TAB");
        Put(m, 0, 4, SlotKind.Mode,    "ALPHA");
        Put(m, 0, 5, SlotKind.Char,    "POUND", "↓", "£");
        Put(m, 0, 6, SlotKind.Mode,    "GRAPH");
        // (0, 7) is a physical "blank" cap on the QWERTY row — same
        // pattern as MZ-700's BLANK at (0, 7).
        Put(m, 0, 7, SlotKind.Blank,   "BLANK");

        // ============================================================
        // Strobe 1 (manual col 2) — Y, Z, @, brackets. Rows D0..D2
        // are empty in the diagram.
        //
        // Bracket polarity ([ vs ]) transcribed from the p. 25 table
        // as-drawn: D4 = "[", D3 = "]". MZ-700 has the reverse; the
        // MZ-800 diagram may or may not agree — walk-through needed.
        // ============================================================
        Put(m, 1, 0, SlotKind.Unused,  "s1b0");
        Put(m, 1, 1, SlotKind.Unused,  "s1b1");
        Put(m, 1, 2, SlotKind.Unused,  "s1b2");
        Put(m, 1, 3, SlotKind.Char,    "RBRK", "]", "}");
        Put(m, 1, 4, SlotKind.Char,    "LBRK", "[", "{");
        Put(m, 1, 5, SlotKind.Char,    "AT",   "@", "'");
        Put(m, 1, 6, SlotKind.Char,    "Z",    "Z");
        Put(m, 1, 7, SlotKind.Char,    "Y",    "Y");

        // ============================================================
        // Strobe 2 (manual col 3) — QRSTUVWX from D7 down to D0.
        // ============================================================
        Put(m, 2, 0, SlotKind.Char,    "X", "X");
        Put(m, 2, 1, SlotKind.Char,    "W", "W");
        Put(m, 2, 2, SlotKind.Char,    "V", "V");
        Put(m, 2, 3, SlotKind.Char,    "U", "U");
        Put(m, 2, 4, SlotKind.Char,    "T", "T");
        Put(m, 2, 5, SlotKind.Char,    "S", "S");
        Put(m, 2, 6, SlotKind.Char,    "R", "R");
        Put(m, 2, 7, SlotKind.Char,    "Q", "Q");

        // ============================================================
        // Strobe 3 (manual col 4) — IJKLMNOP from D7 down to D0.
        // ============================================================
        Put(m, 3, 0, SlotKind.Char,    "P", "P");
        Put(m, 3, 1, SlotKind.Char,    "O", "O");
        Put(m, 3, 2, SlotKind.Char,    "N", "N");
        Put(m, 3, 3, SlotKind.Char,    "M", "M");
        Put(m, 3, 4, SlotKind.Char,    "L", "L");
        Put(m, 3, 5, SlotKind.Char,    "K", "K");
        Put(m, 3, 6, SlotKind.Char,    "J", "J");
        Put(m, 3, 7, SlotKind.Char,    "I", "I");

        // ============================================================
        // Strobe 4 (manual col 5) — ABCDEFGH from D7 down to D0.
        // Alphabetical order matches the ROM table (same shape as
        // MZ-700's row 4).
        // ============================================================
        Put(m, 4, 0, SlotKind.Char,    "H", "H");
        Put(m, 4, 1, SlotKind.Char,    "G", "G");
        Put(m, 4, 2, SlotKind.Char,    "F", "F");
        Put(m, 4, 3, SlotKind.Char,    "E", "E");
        Put(m, 4, 4, SlotKind.Char,    "D", "D");
        Put(m, 4, 5, SlotKind.Char,    "C", "C");
        Put(m, 4, 6, SlotKind.Char,    "B", "B");
        Put(m, 4, 7, SlotKind.Char,    "A", "A");

        // ============================================================
        // Strobe 5 (manual col 6) — digits 1..8 (D7..D0).
        // Shifted glyphs transcribed from the standard MZ layout;
        // walk-through will confirm the MZ-800 didn't relocate any.
        // ============================================================
        Put(m, 5, 0, SlotKind.Char,    "D8", "8", "(");
        Put(m, 5, 1, SlotKind.Char,    "D7", "7", "'");
        Put(m, 5, 2, SlotKind.Char,    "D6", "6", "&");
        Put(m, 5, 3, SlotKind.Char,    "D5", "5", "%");
        Put(m, 5, 4, SlotKind.Char,    "D4", "4", "$");
        Put(m, 5, 5, SlotKind.Char,    "D3", "3", "#");
        Put(m, 5, 6, SlotKind.Char,    "D2", "2", "\"");
        Put(m, 5, 7, SlotKind.Char,    "D1", "1", "!");

        // ============================================================
        // Strobe 6 (manual col 7) — 9, 0, ., ,, SPACE, -, ↑/~, and
        // the "~" cell on D7.
        //
        // Draft interpretation of the multi-glyph cells:
        //   D7 "~":  currently drafted as a single-glyph BSLASH slot
        //     following MZ-700's (6, 7) BSLASH. Actual glyph pair on
        //     MZ-800 needs walk-through — could be '\' unshifted / '~'
        //     shifted, or '~' alone, or something else.
        //   D6 "~↑": ↑ unshifted, ~ shifted — mirrors MZ-700's
        //     UPARROW at (6, 6): "↑", "~".
        //   D3 "O":  transcribed from the research doc as-is, but the
        //     surrounding context (numeric row 9, ., , here) strongly
        //     suggests this is DIGIT ZERO ("0"), not letter O. The two
        //     glyphs look identical in the diagram font. Drafted as
        //     "0" with shifted "π" (mirroring MZ-700's D0 at (6, 3)).
        // ============================================================
        Put(m, 6, 0, SlotKind.Char,    "DOT",     ".", ">");
        Put(m, 6, 1, SlotKind.Char,    "COMMA",   ",", "<");
        Put(m, 6, 2, SlotKind.Char,    "D9",      "9", ")");
        Put(m, 6, 3, SlotKind.Char,    "D0",      "0", "π");
        Put(m, 6, 4, SlotKind.Space,   "SPACE");
        Put(m, 6, 5, SlotKind.Char,    "MINUS",   "-", "=");
        Put(m, 6, 6, SlotKind.Char,    "UPARROW", "↑", "~");
        Put(m, 6, 7, SlotKind.Char,    "BSLASH",  "\\");

        // ============================================================
        // Strobe 7 (manual col 8) — cursor arrows, INST/DEL, /?.
        //
        // Cursor cluster:
        //   D5 = ↑, D4 = ↓, D3 = →, D2 = ← (from p. 25 diagram).
        //   Note: on MZ-700 the ↑ glyph on the diagram is the printable
        //   display code; here D5 ↑ is the CURSOR arrow key (distinct
        //   from the display glyph at (6, 6)).
        // ============================================================
        Put(m, 7, 0, SlotKind.Char,    "SLASH", "/");
        Put(m, 7, 1, SlotKind.Char,    "QMARK", "?");
        Put(m, 7, 2, SlotKind.Cursor,  "CLEFT",  "←");
        Put(m, 7, 3, SlotKind.Cursor,  "CRIGHT", "→");
        Put(m, 7, 4, SlotKind.Cursor,  "CDOWN",  "↓");
        Put(m, 7, 5, SlotKind.Cursor,  "CUP",    "↑");
        Put(m, 7, 6, SlotKind.Edit,    "DEL");
        Put(m, 7, 7, SlotKind.Edit,    "INST");

        // ============================================================
        // Strobe 8 (manual col 9) — SHIFT, CTRL, BREAK. Rows D5..D1
        // are empty in the p. 25 diagram.
        //
        // NOTE: unlike MZ-700 (SHIFT at (8, 0), CTRL at (8, 6), BREAK
        // at (8, 7)), the MZ-800 keeps SHIFT at D0 but places CTRL at
        // D6 and BREAK at D7 — same shape as MZ-700, confirmed at the
        // top-of-column and bottom-of-column bits per the diagram.
        // ============================================================
        Put(m, 8, 0, SlotKind.Modifier, "SHIFT");
        Put(m, 8, 1, SlotKind.Unused,   "s8b1");
        Put(m, 8, 2, SlotKind.Unused,   "s8b2");
        Put(m, 8, 3, SlotKind.Unused,   "s8b3");
        Put(m, 8, 4, SlotKind.Unused,   "s8b4");
        Put(m, 8, 5, SlotKind.Unused,   "s8b5");
        Put(m, 8, 6, SlotKind.Modifier, "CTRL");
        Put(m, 8, 7, SlotKind.Edit,     "BREAK");

        // ============================================================
        // Strobe 9 (manual col 10) — F1..F5 function keys. D2..D0 are
        // empty in the p. 25 diagram.
        //
        // The research doc's transcription of D0 as "." is almost
        // certainly a diagram-artefact / empty-slot placeholder; walk-
        // through will confirm. Drafted as Unused pending confirmation.
        // ============================================================
        Put(m, 9, 0, SlotKind.Unused,   "s9b0");
        Put(m, 9, 1, SlotKind.Unused,   "s9b1");
        Put(m, 9, 2, SlotKind.Unused,   "s9b2");
        Put(m, 9, 3, SlotKind.Function, "F5");
        Put(m, 9, 4, SlotKind.Function, "F4");
        Put(m, 9, 5, SlotKind.Function, "F3");
        Put(m, 9, 6, SlotKind.Function, "F2");
        Put(m, 9, 7, SlotKind.Function, "F1");

        return m;
    }

    private static void Put(
        Dictionary<(int, int), Slot> m,
        int strobe, int bit, SlotKind kind, string id,
        string? unshifted = null, string? shifted = null)
    {
        m[(strobe, bit)] = new Slot(strobe, bit, kind, id, unshifted, shifted);
    }

    /// <summary>Look up a slot by coordinates. Returns null only if out of range.</summary>
    public static Slot? Get(int strobe, int bit)
        => All.TryGetValue((strobe, bit), out var s) ? s : null;

    /// <summary>
    /// MZ glyph positions that have no equivalent on a PC keyboard by
    /// design — the safety gate treats them as reachable so it doesn't
    /// nag every Apply. Mirrors
    /// <see cref="Mz80aMatrixReference.IsKnownUnreachableFromPc"/>.
    ///
    /// Initial pass: no exemptions catalogued. Add as walk-through
    /// surfaces glyph-only cells (e.g. π on shifted-D0) that PC layouts
    /// can't reach directly.
    /// </summary>
    public static bool IsKnownUnreachableFromPc(int strobe, int bit, bool mzShift) => false;

    private static readonly IReadOnlyDictionary<(int strobe, int bit), string> _specialLabels =
        new Dictionary<(int strobe, int bit), string>
        {
            [(0, 0)] = "Enter",
            [(0, 3)] = "TAB",
            [(0, 4)] = "ALPHA",
            [(0, 6)] = "GRAPH",
            [(6, 4)] = "SPACE",
            [(7, 2)] = "←",
            [(7, 3)] = "→",
            [(7, 4)] = "↓",
            [(7, 5)] = "↑",
            [(7, 6)] = "DEL",
            [(7, 7)] = "INST",
            [(8, 0)] = "SHIFT",
            [(8, 6)] = "CTRL",
            [(8, 7)] = "BREAK",
            [(9, 3)] = "F5",
            [(9, 4)] = "F4",
            [(9, 5)] = "F3",
            [(9, 6)] = "F2",
            [(9, 7)] = "F1",
        };

    public static string? FindSpecialLabel(int strobe, int bit) =>
        _specialLabels.TryGetValue((strobe, bit), out var s) ? s : null;

    public static IReadOnlyDictionary<(int strobe, int bit), string> SpecialLabels => _specialLabels;

    /// <summary>All slots of a given kind, in (strobe, bit) order.</summary>
    public static IEnumerable<Slot> OfKind(SlotKind kind)
    {
        for (int s = 0; s < Strobes; s++)
            for (int b = 0; b < Bits; b++)
                if (All[(s, b)].Kind == kind) yield return All[(s, b)];
    }

    /// <summary>
    /// Self-check: every cell present, no duplicate Ids, and a report
    /// of any <see cref="SlotKind.Unknown"/> cells still awaiting
    /// confirmation. Mirrors
    /// <see cref="Mz700MatrixReference.Validate"/> and
    /// <see cref="Mz80aMatrixReference.Validate"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        for (int s = 0; s < Strobes; s++)
        {
            for (int b = 0; b < Bits; b++)
            {
                if (!All.ContainsKey((s, b)))
                    complaints.Add($"Missing cell ({s}, {b})");
            }
        }
        var ids = new HashSet<string>();
        foreach (var slot in All.Values)
        {
            if (!ids.Add(slot.Id))
                complaints.Add($"Duplicate Slot.Id '{slot.Id}' at ({slot.Strobe}, {slot.Bit})");
        }
        int unknownCount = 0;
        foreach (var slot in All.Values)
            if (slot.Kind == SlotKind.Unknown) unknownCount++;
        if (unknownCount > 0)
            complaints.Add($"{unknownCount} cell(s) still SlotKind.Unknown — confirm against tech-ref p. 25");
        return complaints;
    }
}
