using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Physical layout of the MZ-80A keyboard. Drives the shared
/// <c>MzKeyboardDiagram</c> via <c>Mz80aPhysicalKeyboardLayout</c>.
/// Sibling to <see cref="MzKeyboardLayout"/> — same coordinate system
/// (1.0 unit = standard alphabetic key, top-left origin), same
/// resolution-independent scaling.
///
/// LAYOUT SOURCE: photograph at <c>_mz80info/SharpMZ80A.jpg</c> and
/// Sharp MZ-80A Owner's Manual (kept locally, not in repo per
/// [[reference-docs]]). Cross-referenced against
/// <see cref="Mz80aMatrixReference"/> for matrix (strobe, bit)
/// bindings — every non-modifier key here maps to a slot the manual's
/// Fig 3.6 spells out and the user has empirically confirmed.
///
/// MAIN BLOCK vs MZ-700:
/// - No function-key row (MZ-80A doesn't have F1-F5).
/// - Combined caps: BREAK/CTRL, INST/DEL, CLR/HOME are each ONE
///   physical key (one matrix slot) with two labels visible on the
///   cap — the second label is the shifted function, per real
///   hardware. Rendered here with a single <see cref="MzKey.FixedLabel"/>
///   ("BREAK/CTRL" etc.); a future diagram enhancement could split
///   these onto two lines to match the physical cap more closely.
/// - Cursor keys: MZ-80A has only two physical cursor caps —
///   CURSOR-UP (up unshifted, down shifted) and CURSOR-RIGHT (right
///   unshifted, left shifted). LEFT and DOWN reached via SHIFT+
///   the sibling key. This is authentic hardware behaviour; the
///   char-driven side of the emulator already handles the shifting
///   in <see cref="Mz80aSpecialKeyMap"/>.
/// - Both SHIFT caps share matrix slot (0, 0), same pattern as
///   MZ-700's LSHIFT / RSHIFT sharing (8, 0).
///
/// NUMERIC KEYPAD: MZ-80A has one; MZ-700 doesn't. Sits to the
/// right of the main block, separated by a small visual gap, and
/// aligns vertically with rows 2-5 of the main block (top of pad =
/// QWERTY row, bottom of pad = SPACE row). Pad "ENT" shares matrix
/// slot (7, 3) with the main-block CR — pressing either produces
/// carriage return.
///
/// TOTAL EXTENT: 20.5 × 5 units.
/// </summary>
public static class Mz80aKeyboardLayout
{
    // Reuse the shared KeyKind + MzKey types from MzKeyboardLayout so
    // the Mz80aPhysicalKeyboardLayout adapter can map to
    // PhysicalKeyKind through the same switch. Structural sibling, not
    // a rename.
    public static readonly IReadOnlyList<MzKeyboardLayout.MzKey> Keys = BuildKeys();

    public const float Width  = 20.5f;
    public const float Height = 5f;

    // Row Y positions — MZ-80A has no function row, so the digit
    // row starts at Y=0 (unlike MZ-700 where it's at Y=1 with F-keys
    // above).
    private const float DigitRowY  = 0f;
    private const float QwertyRowY = 1f;
    private const float AsdfRowY   = 2f;
    private const float ZxcvRowY   = 3f;
    private const float SpaceRowY  = 4f;
    private const float StdW       = 1f;
    private const float StdH       = 1f;

    // Numeric-pad geometry — sits to the right of the main 16-unit
    // block with a 0.5-unit visual gap. 4 columns × 4 rows, each cell
    // 1×1 except NP_ENT which spans two rows.
    private const float PadX0     = 16.5f;
    private const float PadColW   = 1f;
    private const float PadRow1Y  = QwertyRowY;  // 7 8 9 +
    private const float PadRow2Y  = AsdfRowY;    // 4 5 6 -
    private const float PadRow3Y  = ZxcvRowY;    // 1 2 3 [ENT top half]
    private const float PadRow4Y  = SpaceRowY;   // 0 00 . [ENT bottom half]

    private static IReadOnlyList<MzKeyboardLayout.MzKey> BuildKeys()
    {
        var list = new List<MzKeyboardLayout.MzKey>();

        // ============================================================
        // Row 1 — BREAK/CTRL + 1..0 -^\ + CLR/HOME
        // Digits and top-row punctuation follow Mz80aMatrixReference
        // Fig 3.6 exactly (D1..D0 on strobes 1-5 bits 6-7; MINUS/CARET/
        // PIPE on strobes 6-7).
        // ============================================================
        list.Add(new("BREAK_CTRL", 0, 7, X: 0f,    Y: DigitRowY, W: 1.5f, H: StdH, MzKeyboardLayout.KeyKind.Edit,      "BREAK/CTRL"));
        list.Add(new("D1",         1, 6, X: 1.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D2",         1, 7, X: 2.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D3",         2, 6, X: 3.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D4",         2, 7, X: 4.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D5",         3, 6, X: 5.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D6",         3, 7, X: 6.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D7",         4, 6, X: 7.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D8",         4, 7, X: 8.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D9",         5, 6, X: 9.5f,  Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D0",         5, 7, X: 10.5f, Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("MINUS",      6, 6, X: 11.5f, Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("CARET",      6, 7, X: 12.5f, Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("PIPE",       7, 6, X: 13.5f, Y: DigitRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("CLR_HOME",   7, 7, X: 14.5f, Y: DigitRowY, W: 1.5f, H: StdH, MzKeyboardLayout.KeyKind.Edit,      "CLR/HOME"));

        // ============================================================
        // Row 2 — GRPH + Q..P @ [ + CURSOR-UP + CURSOR-RIGHT
        // Char keys follow the strobe/bit layout in Mz80aMatrixReference
        // exactly (user-confirmed 2026-07-05).
        // Cursor caps carry both directions on the cap — MZ-80A gets
        // LEFT/DOWN via shifting the sibling cap, per real hardware.
        // ============================================================
        list.Add(new("GRPH",         0, 1, X: 0f,     Y: QwertyRowY, W: 1.5f,  H: StdH, MzKeyboardLayout.KeyKind.Mode,      "GRPH"));
        list.Add(new("Q",            1, 4, X: 1.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("W",            1, 5, X: 2.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("E",            2, 4, X: 3.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("R",            2, 5, X: 4.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("T",            3, 4, X: 5.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("Y",            3, 5, X: 6.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("U",            4, 4, X: 7.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("I",            4, 5, X: 8.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("O",            5, 4, X: 9.5f,   Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("P",            5, 5, X: 10.5f,  Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("AT",           6, 4, X: 11.5f,  Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("LBRK",         6, 5, X: 12.5f,  Y: QwertyRowY, W: StdW,  H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("CURSOR_UP",    7, 4, X: 13.5f,  Y: QwertyRowY, W: 1.25f, H: StdH, MzKeyboardLayout.KeyKind.Cursor,    "↑ ↓"));
        list.Add(new("CURSOR_RIGHT", 7, 5, X: 14.75f, Y: QwertyRowY, W: 1.25f, H: StdH, MzKeyboardLayout.KeyKind.Cursor,    "← →"));

        // ============================================================
        // Row 3 — INST/DEL + A..L ; : ] + CR
        // ============================================================
        list.Add(new("INST_DEL", 1, 2, X: 0f,     Y: AsdfRowY, W: 1.5f, H: StdH, MzKeyboardLayout.KeyKind.Edit,      "INST/DEL"));
        list.Add(new("A",        1, 3, X: 1.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("S",        2, 2, X: 2.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("D",        2, 3, X: 3.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("F",        3, 2, X: 4.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("G",        3, 3, X: 5.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("H",        4, 2, X: 6.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("J",        4, 3, X: 7.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("K",        5, 2, X: 8.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("L",        5, 3, X: 9.5f,   Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("SEMI",     6, 2, X: 10.5f,  Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("COLON",    6, 3, X: 11.5f,  Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("RBRK",     7, 2, X: 12.5f,  Y: AsdfRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("CR",       7, 3, X: 13.5f,  Y: AsdfRowY, W: 2.5f, H: StdH, MzKeyboardLayout.KeyKind.Enter,     "CR"));

        // ============================================================
        // Row 4 — LSHIFT + Z X C V B N M , . / ? + RSHIFT
        // The ? cap is the UPARROW slot (7, 0) — ? unshifted, ↑ shifted
        // per Mz80aMatrixReference. Both SHIFT caps share (0, 0), same
        // pattern as MZ-700.
        // ============================================================
        list.Add(new("LSHIFT",  0, 0, X: 0f,    Y: ZxcvRowY, W: 2f,   H: StdH, MzKeyboardLayout.KeyKind.Modifier,  "SHIFT"));
        list.Add(new("Z",       1, 0, X: 2f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("X",       2, 1, X: 3f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("C",       2, 0, X: 4f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("V",       3, 1, X: 5f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("B",       3, 0, X: 6f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("N",       4, 1, X: 7f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("M",       5, 0, X: 8f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("COMMA",   5, 1, X: 9f,    Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("DOT",     6, 0, X: 10f,   Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("SLASH",   6, 1, X: 11f,   Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("UPARROW", 7, 0, X: 12f,   Y: ZxcvRowY, W: StdW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("RSHIFT",  0, 0, X: 13f,   Y: ZxcvRowY, W: 3f,   H: StdH, MzKeyboardLayout.KeyKind.Modifier,  "SHIFT"));

        // ============================================================
        // Row 5 — SPACE bar. Centred under the main 16-unit block.
        // 8 units wide (matches MZ-700's convention).
        // ============================================================
        list.Add(new("SPACE", 4, 0, X: 4f, Y: SpaceRowY, W: 8f, H: StdH, MzKeyboardLayout.KeyKind.Space, null));

        // ============================================================
        // Numeric keypad — 4 cols × 4 rows to the right of the main
        // block, aligned to rows 2-5 (top of pad = QWERTY row, bottom
        // = SPACE row). ENT spans 2 rows (rows 3-4) in the rightmost
        // column and shares matrix slot (7, 3) with the main CR key.
        // ============================================================
        // Row 1: 7 8 9 +
        list.Add(new("NP7",      8, 6, X: PadX0,              Y: PadRow1Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP8",      8, 7, X: PadX0 + PadColW,    Y: PadRow1Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP9",      9, 6, X: PadX0 + 2*PadColW,  Y: PadRow1Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP_PLUS",  9, 7, X: PadX0 + 3*PadColW,  Y: PadRow1Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        // Row 2: 4 5 6 -
        list.Add(new("NP4",      8, 4, X: PadX0,              Y: PadRow2Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP5",      8, 5, X: PadX0 + PadColW,    Y: PadRow2Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP6",      9, 4, X: PadX0 + 2*PadColW,  Y: PadRow2Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP_MINUS", 9, 5, X: PadX0 + 3*PadColW,  Y: PadRow2Y, W: PadColW, H: StdH, MzKeyboardLayout.KeyKind.Character, null));
        // Row 3: 1 2 3 [ENT top half]
        list.Add(new("NP1",      8, 2, X: PadX0,              Y: PadRow3Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP2",      8, 3, X: PadX0 + PadColW,    Y: PadRow3Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP3",      9, 2, X: PadX0 + 2*PadColW,  Y: PadRow3Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));
        // ENT spans PadRow3Y..PadRow4Y (H=2). Matrix slot (7, 3) — same as main CR.
        list.Add(new("NP_ENT",   7, 3, X: PadX0 + 3*PadColW,  Y: PadRow3Y, W: PadColW, H: 2f,    MzKeyboardLayout.KeyKind.Enter,     "ENT"));
        // Row 4: 0 00 . [ENT bottom half already placed above]
        list.Add(new("NP0",      8, 0, X: PadX0,              Y: PadRow4Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP_00",    9, 1, X: PadX0 + PadColW,    Y: PadRow4Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));
        list.Add(new("NP_DOT",   9, 0, X: PadX0 + 2*PadColW,  Y: PadRow4Y, W: PadColW, H: StdH,  MzKeyboardLayout.KeyKind.Character, null));

        return list;
    }

    /// <summary>
    /// Cross-checks every key against <see cref="Mz80aMatrixReference"/>.
    /// Returns a list of complaints; empty means every (Row, Col)
    /// lands on a slot of the matching kind.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var k in Keys)
        {
            if (!k.Row.HasValue || !k.Col.HasValue) continue;
            if (!Mz80aMatrixReference.All.TryGetValue((k.Row.Value, k.Col.Value), out var slot))
            {
                complaints.Add($"Key '{k.Id}' → ({k.Row}, {k.Col}) is out of matrix range");
                continue;
            }
            var expected = ExpectedSlotKind(k.Kind);
            if (slot.Kind != expected)
                complaints.Add($"Key '{k.Id}' is {k.Kind} but ({k.Row}, {k.Col}) is {slot.Kind} in the reference (expected {expected})");
        }
        return complaints;
    }

    // MZ-80A has no Function slot kind; Blank isn't used either (no
    // dummy filler caps on this machine). All the other slot kinds
    // map 1:1 between MzKeyboardLayout.KeyKind and
    // Mz80aMatrixReference.SlotKind.
    private static Mz80aMatrixReference.SlotKind ExpectedSlotKind(MzKeyboardLayout.KeyKind k) => k switch
    {
        MzKeyboardLayout.KeyKind.Character => Mz80aMatrixReference.SlotKind.Char,
        MzKeyboardLayout.KeyKind.Modifier  => Mz80aMatrixReference.SlotKind.Modifier,
        MzKeyboardLayout.KeyKind.Mode      => Mz80aMatrixReference.SlotKind.Mode,
        MzKeyboardLayout.KeyKind.Cursor    => Mz80aMatrixReference.SlotKind.Cursor,
        MzKeyboardLayout.KeyKind.Edit      => Mz80aMatrixReference.SlotKind.Edit,
        MzKeyboardLayout.KeyKind.Enter     => Mz80aMatrixReference.SlotKind.Enter,
        MzKeyboardLayout.KeyKind.Space     => Mz80aMatrixReference.SlotKind.Space,
        _                                  => Mz80aMatrixReference.SlotKind.Unknown,
    };
}
