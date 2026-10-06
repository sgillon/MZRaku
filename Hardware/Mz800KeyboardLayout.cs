using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Physical layout of the MZ-800 keyboard, for the keyboard diagram
/// (via <c>Mz800PhysicalKeyboardLayout</c>) and the editor's reachability
/// checks. Same coordinate system and <see cref="MzKeyboardLayout.MzKey"/>
/// record as <see cref="MzKeyboardLayout"/>: X / Y / W / H in key units
/// (1.0 = a standard alphabetic cap), top-left origin.
///
/// LAYOUT SOURCE: the MZ-800 Owner's Manual p. 4-4 "Special keys"
/// figure (not in the repo — Sharp copyright). The main block matches
/// the MZ-700's key for key except at the left edge:
///   GRAPH  on the digit row       (as MZ-700)
///   TAB    on the QWERTY row      (MZ-700 has ALPHA here)
///   CTRL   on the ASDF row        (as MZ-700)
///   SHIFT + ALPHA on the ZXCV row (MZ-700 has a wide SHIFT only)
/// with F1-F5 along the top and BREAK / CR / right SHIFT on the right,
/// as on the MZ-700. The manual has no figure of the editing cluster
/// (INST / DEL / cursors, "on the right-hand side of the computer",
/// p. 4-4); it uses the MZ-700's arrangement.
///
/// Matrix coordinates come from <see cref="Mz800MatrixReference"/>;
/// <see cref="Validate"/> cross-checks them at startup.
/// </summary>
public static class Mz800KeyboardLayout
{
    private const MzKeyboardLayout.KeyKind Character = MzKeyboardLayout.KeyKind.Character;
    private const MzKeyboardLayout.KeyKind Modifier  = MzKeyboardLayout.KeyKind.Modifier;
    private const MzKeyboardLayout.KeyKind Mode      = MzKeyboardLayout.KeyKind.Mode;
    private const MzKeyboardLayout.KeyKind Function  = MzKeyboardLayout.KeyKind.Function;
    private const MzKeyboardLayout.KeyKind Cursor    = MzKeyboardLayout.KeyKind.Cursor;
    private const MzKeyboardLayout.KeyKind Edit      = MzKeyboardLayout.KeyKind.Edit;
    private const MzKeyboardLayout.KeyKind Enter     = MzKeyboardLayout.KeyKind.Enter;
    private const MzKeyboardLayout.KeyKind Space     = MzKeyboardLayout.KeyKind.Space;
    private const MzKeyboardLayout.KeyKind Blank     = MzKeyboardLayout.KeyKind.Blank;

    // Main block 15.5 units wide, 0.5-unit gap, 4-unit editing cluster.
    public const float Width  = 20f;
    public const float Height = 6f;

    private const float FnRowY     = 0f;
    private const float FnRowH     = 0.5f;
    private const float DigitRowY  = 1f;
    private const float QwertyRowY = 2f;
    private const float AsdfRowY   = 3f;
    private const float ZxcvRowY   = 4f;
    private const float SpaceRowY  = 5f;
    private const float Std        = 1f;
    private const float StdH       = 1f;

    private const float CursorX    = 16f;
    private const float CursorKeyW = 2f;
    private const float CursorUpY  = 1.5f;
    private const float CursorMidY = 2.5f;
    private const float CursorDnY  = 3.5f;

    public static readonly IReadOnlyList<MzKeyboardLayout.MzKey> Keys = new MzKeyboardLayout.MzKey[]
    {
        // Function row — F1 to F5, half-height.
        new("PF1", 9, 7, X: 0f,   Y: FnRowY, W: 1.5f, H: FnRowH, Function, "F1"),
        new("PF2", 9, 6, X: 1.5f, Y: FnRowY, W: 1.5f, H: FnRowH, Function, "F2"),
        new("PF3", 9, 5, X: 3f,   Y: FnRowY, W: 1.5f, H: FnRowH, Function, "F3"),
        new("PF4", 9, 4, X: 4.5f, Y: FnRowY, W: 1.5f, H: FnRowH, Function, "F4"),
        new("PF5", 9, 3, X: 6f,   Y: FnRowY, W: 1.5f, H: FnRowH, Function, "F5"),

        // Digit row — GRAPH + 1 2 3 4 5 6 7 8 9 0 - ↑/~ \ + BREAK.
        new("GRAPH",   0, 6, X: 0f,  Y: DigitRowY, W: 1f,   H: StdH, Mode,      "GRAPH"),
        new("D1",      5, 7, X: 1f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D2",      5, 6, X: 2f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D3",      5, 5, X: 3f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D4",      5, 4, X: 4f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D5",      5, 3, X: 5f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D6",      5, 2, X: 6f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D7",      5, 1, X: 7f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D8",      5, 0, X: 8f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D9",      6, 2, X: 9f,  Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("D0",      6, 3, X: 10f, Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("MINUS",   6, 5, X: 11f, Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("UPARROW", 6, 6, X: 12f, Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("BSLASH",  6, 7, X: 13f, Y: DigitRowY, W: Std,  H: StdH, Character, null),
        new("BREAK",   8, 7, X: 14f, Y: DigitRowY, W: 1.5f, H: StdH, Edit,      "BREAK"),

        // QWERTY row — TAB + Q..P + @/' [/{ £/↓ + blank cap. @ and £
        // carry label overrides for the same reason as on the MZ-700
        // (their second glyph is mapped to another slot in the char map).
        new("TAB",   0, 3, X: 0f,    Y: QwertyRowY, W: 1.5f, H: StdH, Edit,      "TAB"),
        new("Q",     2, 7, X: 1.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("W",     2, 1, X: 2.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("E",     4, 3, X: 3.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("R",     2, 6, X: 4.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("T",     2, 4, X: 5.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("Y",     1, 7, X: 6.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("U",     2, 3, X: 7.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("I",     3, 7, X: 8.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("O",     3, 1, X: 9.5f,  Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("P",     3, 0, X: 10.5f, Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("AT",    1, 5, X: 11.5f, Y: QwertyRowY, W: Std,  H: StdH, Character, null,
             UnshiftedLabel: "@", ShiftedLabel: "'"),
        new("LBRK",  1, 4, X: 12.5f, Y: QwertyRowY, W: Std,  H: StdH, Character, null),
        new("POUND", 0, 5, X: 13.5f, Y: QwertyRowY, W: Std,  H: StdH, Character, null,
             UnshiftedLabel: "↓", ShiftedLabel: "£"),
        new("BLANK", 0, 7, X: 14.5f, Y: QwertyRowY, W: Std,  H: StdH, Blank,     null),

        // ASDF row — CTRL + A..L ; : ] + CR.
        new("CTRL",   8, 6, X: 0f,     Y: AsdfRowY, W: 1.75f, H: StdH, Modifier,  "CTRL"),
        new("A",      4, 7, X: 1.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("S",      2, 5, X: 2.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("D",      4, 4, X: 3.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("F",      4, 2, X: 4.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("G",      4, 1, X: 5.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("H",      4, 0, X: 6.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("J",      3, 6, X: 7.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("K",      3, 5, X: 8.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("L",      3, 4, X: 9.75f,  Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("SEMI",   0, 2, X: 10.75f, Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("COLON",  0, 1, X: 11.75f, Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("RBRK",   1, 3, X: 12.75f, Y: AsdfRowY, W: Std,   H: StdH, Character, null),
        new("CR",     0, 0, X: 13.75f, Y: AsdfRowY, W: 1.75f, H: StdH, Enter,     "CR"),

        // ZXCV row — SHIFT + ALPHA + Z..M , . / ? + right SHIFT. Both
        // SHIFT keys share matrix slot (8, 0).
        new("LSHIFT", 8, 0, X: 0f,     Y: ZxcvRowY, W: 1.25f, H: StdH, Modifier,  "SHIFT"),
        new("ALPHA",  0, 4, X: 1.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Mode,      "ALPHA"),
        new("Z",      1, 6, X: 2.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("X",      2, 0, X: 3.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("C",      4, 5, X: 4.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("V",      2, 2, X: 5.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("B",      4, 6, X: 6.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("N",      3, 2, X: 7.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("M",      3, 3, X: 8.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("COMMA",  6, 1, X: 9.25f,  Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("DOT",    6, 0, X: 10.25f, Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("SLASH",  7, 0, X: 11.25f, Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("QMARK",  7, 1, X: 12.25f, Y: ZxcvRowY, W: Std,   H: StdH, Character, null),
        new("RSHIFT", 8, 0, X: 13.25f, Y: ZxcvRowY, W: 2.25f, H: StdH, Modifier,  "SHIFT"),

        // Space bar, centred under the main block.
        new("SPACE", 6, 4, X: 3.75f, Y: SpaceRowY, W: 8f, H: StdH, Space, null),

        // Editing cluster (MZ-700 arrangement, see the class comment).
        new("INST",   7, 7, X: CursorX,                   Y: FnRowY,     W: CursorKeyW, H: FnRowH, Edit,   "INST"),
        new("DEL",    7, 6, X: CursorX + CursorKeyW,      Y: FnRowY,     W: CursorKeyW, H: FnRowH, Edit,   "DEL"),
        new("CUP",    7, 5, X: CursorX + CursorKeyW / 2f, Y: CursorUpY,  W: CursorKeyW, H: StdH,   Cursor, "↑"),
        new("CLEFT",  7, 2, X: CursorX,                   Y: CursorMidY, W: CursorKeyW, H: StdH,   Cursor, "←"),
        new("CRIGHT", 7, 3, X: CursorX + CursorKeyW,      Y: CursorMidY, W: CursorKeyW, H: StdH,   Cursor, "→"),
        new("CDOWN",  7, 4, X: CursorX + CursorKeyW / 2f, Y: CursorDnY,  W: CursorKeyW, H: StdH,   Cursor, "↓"),
    };

    /// <summary>
    /// Keys the safety gate requires to have at least one PC binding
    /// before Settings → Apply saves without a confirm prompt: every key
    /// on a real matrix slot except the blank filler cap. Mirrors
    /// <see cref="MzKeyboardLayout.EssentialKeys"/>.
    /// </summary>
    public static IEnumerable<MzKeyboardLayout.MzKey> EssentialKeys
    {
        get
        {
            foreach (var k in Keys)
                if (k.Row.HasValue && k.Col.HasValue && k.Kind != Blank)
                    yield return k;
        }
    }

    /// <summary>
    /// Cross-checks every key against <see cref="Mz800MatrixReference"/>.
    /// Returns a list of complaints; empty means every (Row, Col) lands
    /// on a slot of the matching kind.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var k in Keys)
        {
            if (!k.Row.HasValue || !k.Col.HasValue) continue;
            var slot = Mz800MatrixReference.Get(k.Row.Value, k.Col.Value);
            if (slot is null)
            {
                complaints.Add($"Key '{k.Id}' → ({k.Row}, {k.Col}) is out of matrix range");
                continue;
            }
            var expected = ExpectedSlotKind(k.Kind);
            if (slot.Value.Kind != expected)
                complaints.Add($"Key '{k.Id}' is {k.Kind} but ({k.Row}, {k.Col}) is {slot.Value.Kind} in the reference (expected {expected})");
        }
        return complaints;
    }

    private static Mz800MatrixReference.SlotKind ExpectedSlotKind(MzKeyboardLayout.KeyKind k) => k switch
    {
        Character => Mz800MatrixReference.SlotKind.Char,
        Function  => Mz800MatrixReference.SlotKind.Function,
        Modifier  => Mz800MatrixReference.SlotKind.Modifier,
        Mode      => Mz800MatrixReference.SlotKind.Mode,
        Cursor    => Mz800MatrixReference.SlotKind.Cursor,
        Edit      => Mz800MatrixReference.SlotKind.Edit,
        Enter     => Mz800MatrixReference.SlotKind.Enter,
        Space     => Mz800MatrixReference.SlotKind.Space,
        Blank     => Mz800MatrixReference.SlotKind.Blank,
        _         => Mz800MatrixReference.SlotKind.Unknown,
    };
}
