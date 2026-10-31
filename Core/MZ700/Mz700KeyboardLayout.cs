using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Physical layout of the MZ-700 keyboard. Drives the
/// <c>MzKeyboardDiagram</c> control and (via reverse lookup) the conflict
/// detector. Single source of truth for "which key sits where" — the
/// renderer is a pure function of this spec.
///
/// Coordinate system:
/// - X / Y / W / H are in "key units" where 1.0 unit = standard
///   alphabetic key. Top-left origin.
/// - The renderer scales the whole layout to fit its viewport; the spec
///   is resolution-independent.
///
/// Left-edge modifier stack (per real MZ-700 hardware):
///   GRAPH  (1.00 wide)  on the digit row
///   ALPHA  (1.50 wide)  on the QWERTY row
///   CTRL   (1.75 wide)  on the ASDF row
///   SHIFT  (2.00 wide)  on the ZXCV row
/// MZ-700 has only ONE SHIFT key — no right SHIFT.
///
/// Cursor cluster (INST / DEL / arrows) sits in its own block to the
/// right of the main typewriter area, separated by a visual gap.
///
/// Labels:
/// - For character keys, the unshifted / shifted glyphs are looked up at
///   render time from <see cref="Mz700GlyphCatalog"/>. No need to duplicate
///   them here.
/// - Function and modifier keys carry <see cref="MzKey.FixedLabel"/>.
///
/// Multiple keys may share one matrix slot. The reverse lookup treats
/// this naturally: a PC keystroke maps to one matrix slot, and the
/// diagram lights every physical key bound to it.
///
/// LAYOUT SOURCE: best-effort from existing hardware knowledge plus
/// general MZ-700 photographs. Two positions are still unverified (the
/// TODOs on the right SHIFT and the SPACE row below); check them against
/// the Owner's Manual p.114. They affect only how the diagram looks.
/// </summary>
public static class Mz700KeyboardLayout
{
    // Key records and kinds are the shared MzKey / MzKeyKind
    // (Core/Keyboard/MzKey.cs).

    // Total diagram extent. Width carries the main keyboard (15.5 units)
    // plus a 0.5-unit gap and the 4-unit cursor cluster.
    public const float Width  = 20f;
    public const float Height = 6f;

    // Convenience constants for row positions/sizes we tune most often.
    private const float FnRowY     = 0f;
    private const float FnRowH     = 0.5f;
    private const float DigitRowY  = 1f;
    private const float QwertyRowY = 2f;
    private const float AsdfRowY   = 3f;
    private const float ZxcvRowY   = 4f;
    private const float SpaceRowY  = 5f;
    private const float Std        = 1f;
    private const float StdH       = 1f;

    // Cursor-cluster geometry:
    // - INST / DEL sit at the function-row level, half-height, w=2 each.
    // - ← / → are adjacent at vertical centre of rows 1-4 (y = 2.5).
    // - ↑ centred above ← / →, ↓ centred below — cross layout.
    // - Cluster vertical span: y = 1.5 .. 4.5 (centred on rows 1-4).
    private const float CursorX    = 16f;
    private const float CursorKeyW = 2f;
    private const float CursorUpY  = 1.5f;
    private const float CursorMidY = 2.5f;
    private const float CursorDnY  = 3.5f;

    public static readonly IReadOnlyList<MzKey> Keys = new MzKey[]
    {
        // ============================================================
        // Function row — F1 to F5 along the top, half-height, labelled
        // "F1" etc. Left-justified labels handled by the renderer based
        // on MzKeyKind.Function.
        // PF5 → matrix (9, 3). Confirmed against Owner's Manual
        // 2026-06-12 (alongside CTRL=(8,6) correction). Wired in
        // Mz700SpecialKeyMap → HostKey.F5.
        // ============================================================
        new("PF1", 9, 7, X: 0f,   Y: FnRowY, W: 1.5f, H: FnRowH, MzKeyKind.Function, "F1"),
        new("PF2", 9, 6, X: 1.5f, Y: FnRowY, W: 1.5f, H: FnRowH, MzKeyKind.Function, "F2"),
        new("PF3", 9, 5, X: 3f,   Y: FnRowY, W: 1.5f, H: FnRowH, MzKeyKind.Function, "F3"),
        new("PF4", 9, 4, X: 4.5f, Y: FnRowY, W: 1.5f, H: FnRowH, MzKeyKind.Function, "F4"),
        new("PF5", 9, 3, X: 6f,   Y: FnRowY, W: 1.5f, H: FnRowH, MzKeyKind.Function, "F5"),

        // ============================================================
        // Digit row — GRAPH (1.0) + 1 2 3 4 5 6 7 8 9 0 - ↑/~ \ + BREAK (1.5).
        // ↑/~ key is the MZ-700 up-arrow display char (unshifted) /
        // tilde (shifted) — slot (6, 6).
        // ============================================================
        new("GRAPH",   0, 6, X: 0f,    Y: DigitRowY, W: 1f,   H: StdH, MzKeyKind.Mode,      "GRAPH"),
        new("D1",      5, 7, X: 1f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D2",      5, 6, X: 2f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D3",      5, 5, X: 3f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D4",      5, 4, X: 4f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D5",      5, 3, X: 5f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D6",      5, 2, X: 6f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D7",      5, 1, X: 7f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D8",      5, 0, X: 8f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D9",      6, 2, X: 9f,    Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("D0",      6, 3, X: 10f,   Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("MINUS",   6, 5, X: 11f,   Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("UPARROW", 6, 6, X: 12f,   Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("BSLSH",   6, 7, X: 13f,   Y: DigitRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("BREAK",   8, 7, X: 14f,   Y: DigitRowY, W: 1.5f, H: StdH, MzKeyKind.Edit,      "BREAK"),

        // ============================================================
        // QWERTY row — ALPHA (1.5) + Q W E R T Y U I O P @/' [/{ £/↓
        // + blank dummy (visual filler that keeps the row length uniform
        // with the rest of the keyboard).
        //
        // @ key: matrix slot (1, 5) per Owner's Manual (reconciled
        //   2026-06-13 via Mz700MatrixReference). Earlier code put @ at
        //   (0, 3) which was wrong; (0, 3) is empty in the scan matrix.
        //   Shifted side overridden because ' is already mapped to
        //   (5,1,true) shift-7 in CharMap and can't be reused.
        // £ key: matrix slot (0, 5), confirmed against Owner's Manual p.114.
        // BLANK: physical dummy cap; matrix slot (0, 7) on real
        //   hardware. Kept as MzKeyKind.Blank so EssentialKeys excludes
        //   it from the safety gate.
        // ============================================================
        new("ALPHA", 0, 4, X: 0f,    Y: QwertyRowY, W: 1.5f, H: StdH, MzKeyKind.Mode,      "ALPHA"),
        new("Q",     2, 7, X: 1.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("W",     2, 1, X: 2.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("E",     4, 3, X: 3.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("R",     2, 6, X: 4.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("T",     2, 4, X: 5.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("Y",     1, 7, X: 6.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("U",     2, 3, X: 7.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("I",     3, 7, X: 8.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("O",     3, 1, X: 9.5f,  Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("P",     3, 0, X: 10.5f, Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("AT",    1, 5, X: 11.5f, Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null,
             UnshiftedLabel: "@", ShiftedLabel: "'"),
        new("LBRK",  1, 4, X: 12.5f, Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("POUND", 0, 5, X: 13.5f, Y: QwertyRowY, W: Std,  H: StdH, MzKeyKind.Character, null,
             UnshiftedLabel: "↓", ShiftedLabel: "£"),
        new("BLANK", 0, 7,    X: 14.5f, Y: QwertyRowY, W: Std, H: StdH, MzKeyKind.Blank, null),

        // ============================================================
        // ASDF row — CTRL (1.75) + A S D F G H J K L ; : ] + RETURN.
        // RETURN is rendered as a wide rectangle; real hardware is
        // L-shaped. A future revision can introduce an LShape key shape
        // without changing the data layer.
        // ============================================================
        // CTRL matrix slot = (8, 6) per Owner's Manual (verified
        // 2026-06-12). Earlier code placed CTRL at (9, 2) but that slot
        // is actually a shifted-F5 alias / unused — pressing it produced
        // CHR$( instead of the CTRL modifier.
        new("CTRL",   8, 6, X: 0f,     Y: AsdfRowY, W: 1.75f, H: StdH, MzKeyKind.Modifier, "CTRL"),
        new("A",      4, 7, X: 1.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("S",      2, 5, X: 2.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("D",      4, 4, X: 3.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("F",      4, 2, X: 4.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("G",      4, 1, X: 5.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("H",      4, 0, X: 6.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("J",      3, 6, X: 7.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("K",      3, 5, X: 8.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("L",      3, 4, X: 9.75f,  Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("SEMI",   0, 2, X: 10.75f, Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("COLON",  0, 1, X: 11.75f, Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("RBRK",   1, 3, X: 12.75f, Y: AsdfRowY, W: Std,   H: StdH, MzKeyKind.Character, null),
        new("RETURN", 0, 0, X: 13.75f, Y: AsdfRowY, W: 1.75f, H: StdH, MzKeyKind.Enter,    "CR"),

        // ============================================================
        // ZXCV row — SHIFT (2.0) + Z X C V B N M , . / ? + right SHIFT.
        // Both SHIFT keys share matrix slot (8, 0).
        // TODO: confirm right-SHIFT width once row alignment settles.
        // ============================================================
        new("LSHIFT", 8, 0, X: 0f,    Y: ZxcvRowY, W: 2f,   H: StdH, MzKeyKind.Modifier, "SHIFT"),
        new("Z",      1, 6, X: 2f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("X",      2, 0, X: 3f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("C",      4, 5, X: 4f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("V",      2, 2, X: 5f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("B",      4, 6, X: 6f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("N",      3, 2, X: 7f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("M",      3, 3, X: 8f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("COMMA",  6, 1, X: 9f,    Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("DOT",    6, 0, X: 10f,   Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("SLASH",  7, 0, X: 11f,   Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("QMARK",  7, 1, X: 12f,   Y: ZxcvRowY, W: Std,  H: StdH, MzKeyKind.Character, null),
        new("RSHIFT", 8, 0, X: 13f,   Y: ZxcvRowY, W: 2.5f, H: StdH, MzKeyKind.Modifier, "SHIFT"),

        // ============================================================
        // Bottom row — SPACE bar (centred under the typewriter area).
        // TODO: verify against Owner's Manual p.114 — real MZ-700 may
        // integrate SPACE into the ZXCV row rather than a separate row.
        // ============================================================
        // SPACE bar — 8.0 wide, centred under the main keyboard area
        // (x = 0 .. 15.5, centre 7.75). Left edge therefore at 3.75.
        new("SPACE", 6, 4, X: 3.75f, Y: SpaceRowY, W: 8f, H: StdH, MzKeyKind.Space, null),

        // ============================================================
        // Cursor cluster — separate block to the right of the main
        // keyboard. INST / DEL sit at the function-row level (half-
        // height); arrows form a cross below, centred vertically on
        // rows 1-4 to the cluster's left. Layout:
        //
        //   [   INST   ][   DEL    ]      <- y = 0, h = 0.5 (F-row level)
        //
        //               [    ↑     ]      <- y = 1.5
        //   [    ←     ][    →     ]      <- y = 2.5
        //               [    ↓     ]      <- y = 3.5
        //
        // ============================================================
        new("INST",   7, 7, X: CursorX,                   Y: FnRowY,     W: CursorKeyW, H: FnRowH, MzKeyKind.Edit,   "INST"),
        new("DEL",    7, 6, X: CursorX + CursorKeyW,      Y: FnRowY,     W: CursorKeyW, H: FnRowH, MzKeyKind.Edit,   "DEL"),
        new("CUP",    7, 5, X: CursorX + CursorKeyW / 2f, Y: CursorUpY,  W: CursorKeyW, H: StdH,   MzKeyKind.Cursor, "↑"),
        new("CLEFT",  7, 2, X: CursorX,                   Y: CursorMidY, W: CursorKeyW, H: StdH,   MzKeyKind.Cursor, "←"),
        new("CRIGHT", 7, 3, X: CursorX + CursorKeyW,      Y: CursorMidY, W: CursorKeyW, H: StdH,   MzKeyKind.Cursor, "→"),
        new("CDOWN",  7, 4, X: CursorX + CursorKeyW / 2f, Y: CursorDnY,  W: CursorKeyW, H: StdH,   MzKeyKind.Cursor, "↓"),
    };

    /// <summary>
    /// Keys the safety gate (P2-9) requires to have at least one PC
    /// binding before Settings → Apply / OK will save without a confirm
    /// prompt. Every key that corresponds to a real MZ-700 matrix slot
    /// qualifies — the only excluded entries are layout-only fillers
    /// like <see cref="MzKeyKind.Blank"/>, which the user doesn't need
    /// to bind even though (since 2026-06-13) we know the BLANK cap
    /// occupies real matrix slot (0, 7).
    /// </summary>
    public static IEnumerable<MzKey> EssentialKeys
    {
        get
        {
            foreach (var k in Keys)
                if (k.Row.HasValue && k.Col.HasValue && k.Kind != MzKeyKind.Blank)
                    yield return k;
        }
    }

    /// <summary>
    /// Cross-checks every key in <see cref="Keys"/> against
    /// <see cref="Mz700MatrixReference"/>. Returns a list of complaints;
    /// empty means each MzKey's (Row, Col) lands on a matrix slot of
    /// the matching kind.
    /// </summary>
    internal static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var k in Keys)
        {
            if (!k.Row.HasValue || !k.Col.HasValue) continue;
            var slot = Mz700MatrixReference.Get(k.Row.Value, k.Col.Value);
            if (slot is null)
            {
                complaints.Add($"Key '{k.Id}' → ({k.Row}, {k.Col}) is out of matrix range");
                continue;
            }
            var expected = ExpectedSlotKind(k.Kind);
            if (slot.Value.Kind != expected)
            {
                complaints.Add($"Key '{k.Id}' is {k.Kind} but ({k.Row}, {k.Col}) is {slot.Value.Kind} in the reference (expected {expected})");
            }
        }
        return complaints;
    }

    private static Mz700MatrixReference.SlotKind ExpectedSlotKind(MzKeyKind k) => k switch
    {
        MzKeyKind.Character => Mz700MatrixReference.SlotKind.Char,
        MzKeyKind.Function  => Mz700MatrixReference.SlotKind.Function,
        MzKeyKind.Modifier  => Mz700MatrixReference.SlotKind.Modifier,
        MzKeyKind.Mode      => Mz700MatrixReference.SlotKind.Mode,
        MzKeyKind.Cursor    => Mz700MatrixReference.SlotKind.Cursor,
        MzKeyKind.Edit      => Mz700MatrixReference.SlotKind.Edit,
        MzKeyKind.Enter     => Mz700MatrixReference.SlotKind.Enter,
        MzKeyKind.Space     => Mz700MatrixReference.SlotKind.Space,
        MzKeyKind.Blank     => Mz700MatrixReference.SlotKind.Blank,
        _                 => Mz700MatrixReference.SlotKind.Unknown,
    };
}
