using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Canonical MZ-80A keyboard matrix reference — the single source of
/// truth for "which physical key sits at which scan-matrix slot" on the
/// SA-1510 monitor's keyboard. Modelled on the sibling
/// <see cref="Mz700MatrixReference"/>.
///
/// SOURCE: Sharp MZ-80A Owner's Manual, Figure 3.6 (printed p.167,
/// PDF p.157), "Key scanning strobe signal and bit data." The manual
/// documents the electrical matrix as a 10-strobe × 8-bit grid, with
/// the sample "strobe 2H, key data FBH ⇒ S key" spelling out the
/// encoding: strobes 0-9 map to <see cref="Slot.Strobe"/>, bits D0-D7
/// map to <see cref="Slot.Bit"/>, active-low (a pressed key clears
/// its bit).
///
/// PPI wiring: strobe value goes through PA3..PA0 of the 8255 → 74145
/// BCD-to-decimal decoder → one of 10 keyboard row lines. Key data
/// comes back on PB7..PB0 (Port B).
///
/// NOTE ON THE MANUAL "STROBE 2H" EXAMPLE: reading Figure 3.6 visually
/// places S at strobe 1 D2 (S is in the second-from-left column, whose
/// label is "1"). The manual's prose example ("strobe is 2H, key data
/// is FBH") disagrees by one — likely a typo, or a 1-based interpretation
/// of the strobe number in the accompanying text. Empirical verification
/// against the ROM's scan behaviour will settle this; cells marked
/// <see cref="SlotKind.Unknown"/> or with a comment flag are candidates
/// for that verification pass.
///
/// COMPARISON TO MZ-700: same 10×8 shape but the physical key placement
/// differs. MZ-700 groups QWERTYUVWX on a single strobe (2), whereas
/// MZ-80A distributes the alphabet down the D0-D5 rows within each of
/// several strobe columns.
/// </summary>
public static class Mz80aMatrixReference
{
    public enum SlotKind
    {
        /// <summary>Produces a glyph. Has unshifted + shifted display characters.</summary>
        Char,
        /// <summary>SHIFT, CTRL — held while another key is pressed.</summary>
        Modifier,
        /// <summary>GRPH — mode toggle (graphic vs normal).</summary>
        Mode,
        /// <summary>INST, DEL, CLR, HOME, BREAK.</summary>
        Edit,
        /// <summary>Cursor arrows (up / down / left / right).</summary>
        Cursor,
        /// <summary>CR/ENT — carriage return.</summary>
        Enter,
        /// <summary>Space bar.</summary>
        Space,
        /// <summary>Cell exists in the scan grid but is not wired to any physical key.</summary>
        Unused,
        /// <summary>Not yet confirmed against Figure 3.6. Flagged at startup validation.</summary>
        Unknown,
    }

    /// <summary>
    /// One cell in the scan matrix.
    /// <see cref="Strobe"/> is 0-9 (PA3..PA0 → 74145 line select).
    /// <see cref="Bit"/> is 0-7 (Port B bit position; 0 = pressed).
    /// For Char cells, <see cref="UnshiftedGlyph"/> and
    /// <see cref="ShiftedGlyph"/> carry the labels visible on the key
    /// cap.
    /// </summary>
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

    private static Dictionary<(int strobe, int bit), Slot> BuildAll()
    {
        var m = new Dictionary<(int, int), Slot>(Strobes * Bits);

        // ============================================================
        // Strobe 0 — modifiers + break/ctrl + INST/DEL. All the
        // "no-visible-glyph" control keys live in this column.
        // ============================================================
        Put(m, 0, 0, SlotKind.Modifier, "SHIFT");
        Put(m, 0, 1, SlotKind.Mode,     "GRPH");
        Put(m, 0, 2, SlotKind.Edit,     "INST_DEL");    // one physical key: INST unshifted / DEL shifted
        Put(m, 0, 3, SlotKind.Unused,   "s0b3");
        Put(m, 0, 4, SlotKind.Unused,   "s0b4");
        Put(m, 0, 5, SlotKind.Unused,   "s0b5");
        Put(m, 0, 6, SlotKind.Unused,   "s0b6");
        Put(m, 0, 7, SlotKind.Edit,     "BREAK_CTRL");  // BREAK shifted / CTRL unshifted

        // ============================================================
        // Strobe 1 — Q, W, A column confirmed working by user. D2 slot
        // in this strobe is NOT S (user confirmed S sits in strobe 2);
        // the actual key here is TBC pending empirical check.
        // ============================================================
        Put(m, 1, 0, SlotKind.Char,   "Z",  "Z");
        Put(m, 1, 1, SlotKind.Unused,  "s1b1");         // X shifted to (2,1) 2026-07-05
        Put(m, 1, 2, SlotKind.Unused,  "s1b2");         // was 'S' pre-audit; S now at (2,2)
        Put(m, 1, 3, SlotKind.Char,   "A",  "A");
        Put(m, 1, 4, SlotKind.Char,   "Q",  "Q");       // user-confirmed 2026-07-05
        Put(m, 1, 5, SlotKind.Char,   "W",  "W");       // user-confirmed 2026-07-05
        Put(m, 1, 6, SlotKind.Char,   "D1", "1", "!");
        Put(m, 1, 7, SlotKind.Char,   "D2", "2", "\"");

        // ============================================================
        // Strobe 2 — S confirmed at D2 (user 2026-07-05, matches the
        // manual's "strobe 2H, key data FBH ⇒ S" example). Other slots
        // TBC empirically; the D-row assignments below are the working
        // hypothesis pending verification.
        // ============================================================
        Put(m, 2, 0, SlotKind.Char,   "C",  "C");
        Put(m, 2, 1, SlotKind.Char,   "X",  "X");        // shifted from (1,1) 2026-07-05
        Put(m, 2, 2, SlotKind.Char,   "S",  "S");       // user-confirmed 2026-07-05
        Put(m, 2, 3, SlotKind.Char,   "D",  "D");
        Put(m, 2, 4, SlotKind.Char,   "E",  "E");
        Put(m, 2, 5, SlotKind.Char,   "R",  "R");
        Put(m, 2, 6, SlotKind.Char,   "D3", "3", "#");
        Put(m, 2, 7, SlotKind.Char,   "D4", "4", "$");

        // ============================================================
        // Strobe 3 — V and F at D1/D2 shifted right from initial
        // read. B, G, T, Y confirmed.
        // ============================================================
        Put(m, 3, 0, SlotKind.Char,   "B",  "B");        // user-confirmed 2026-07-05
        Put(m, 3, 1, SlotKind.Char,   "V",  "V");        // shifted from (2,1) 2026-07-05
        Put(m, 3, 2, SlotKind.Char,   "F",  "F");        // shifted from (2,2) 2026-07-05
        Put(m, 3, 3, SlotKind.Char,   "G",  "G");        // user-confirmed 2026-07-05
        Put(m, 3, 4, SlotKind.Char,   "T",  "T");        // user-confirmed 2026-07-05
        Put(m, 3, 5, SlotKind.Char,   "Y",  "Y");        // user-confirmed 2026-07-05
        Put(m, 3, 6, SlotKind.Char,   "D5", "5", "%");
        Put(m, 3, 7, SlotKind.Char,   "D6", "6", "&");

        // ============================================================
        // Strobe 4 — N and H at D1/D2 shifted right from initial read.
        // SPACE at D0 still confirmed via direct Space-key test.
        // ============================================================
        Put(m, 4, 0, SlotKind.Space,  "SPACE");
        Put(m, 4, 1, SlotKind.Char,   "N",     "N");     // shifted from (3,1) 2026-07-05
        Put(m, 4, 2, SlotKind.Char,   "H",     "H");     // shifted from (3,2) 2026-07-05
        Put(m, 4, 3, SlotKind.Char,   "J",     "J");     // user-confirmed 2026-07-05
        Put(m, 4, 4, SlotKind.Char,   "U",     "U");     // user-confirmed 2026-07-05
        Put(m, 4, 5, SlotKind.Char,   "I",     "I");     // user-confirmed 2026-07-05
        Put(m, 4, 6, SlotKind.Char,   "D7",    "7", "/");
        Put(m, 4, 7, SlotKind.Char,   "D8",    "8", "(");

        // ============================================================
        // Strobe 5 — COMMA and K at D1/D2 shifted right from initial
        // read (both confirmed via cross-typing: PC minus produced ',',
        // PC semicolon produced K).
        // ============================================================
        Put(m, 5, 0, SlotKind.Char,   "M",         "M");        // user-confirmed 2026-07-05
        Put(m, 5, 1, SlotKind.Char,   "COMMA",     ",", "<");   // shifted from (4,1) 2026-07-05
        Put(m, 5, 2, SlotKind.Char,   "K",         "K");        // shifted from (4,2) 2026-07-05
        Put(m, 5, 3, SlotKind.Char,   "L",         "L");        // user-confirmed 2026-07-05
        Put(m, 5, 4, SlotKind.Char,   "O",         "O");        // user-confirmed 2026-07-05
        Put(m, 5, 5, SlotKind.Char,   "P",         "P");        // user-confirmed 2026-07-05
        Put(m, 5, 6, SlotKind.Char,   "D9",        "9", ")");
        Put(m, 5, 7, SlotKind.Char,   "D0",        "0", ")");

        // ============================================================
        // Strobe 6 — DOT confirmed. D1/D2 slots hold MINUS and SEMI
        // (also shifted right one strobe from initial read). Other
        // slots retain the initial hypothesis pending more empirical
        // data.
        // ============================================================
        Put(m, 6, 0, SlotKind.Char,   "DOT",     ".", ">");     // user-confirmed 2026-07-05
        // (6, 1) is the SLASH key (not MINUS). Users reported PC '-'
        // produced '/' on screen — i.e. (6, 1) really decodes as '/'.
        Put(m, 6, 1, SlotKind.Char,   "SLASH",   "/");           // was MINUS, correction 2026-07-05
        Put(m, 6, 2, SlotKind.Char,   "SEMI",    ";", "+");      // shifted from (5,2) 2026-07-05
        Put(m, 6, 3, SlotKind.Char,   "COLON",   ":", "*");
        Put(m, 6, 4, SlotKind.Char,   "AT",      "@", "\\");
        Put(m, 6, 5, SlotKind.Char,   "LBRK",    "[");           // user-confirmed 2026-07-05
        // (6, 6) is the MINUS key (not EQUALS). Users reported PC '/'
        // produced '-' on screen; this is the other half of the swap.
        Put(m, 6, 6, SlotKind.Char,   "MINUS",   "-");           // was EQUALS, correction 2026-07-05
        Put(m, 6, 7, SlotKind.Char,   "CARET",   "^", "~");

        // ============================================================
        // Strobe 7 — cursor arrows, CR/ENT, CLR/HOME, pipe/backslash.
        // ============================================================
        Put(m, 7, 0, SlotKind.Char,   "UPARROW", "↑", "?");     // ↑? cell in Fig 3.6
        Put(m, 7, 1, SlotKind.Cursor, "LEFT");                    // shifted from (6,1) 2026-07-05
        Put(m, 7, 2, SlotKind.Char,   "RBRK",    "]");            // shifted from (6,2) 2026-07-05
        Put(m, 7, 3, SlotKind.Enter,  "CR");
        Put(m, 7, 4, SlotKind.Cursor, "CURSOR_UP");
        Put(m, 7, 5, SlotKind.Cursor, "CURSOR_RIGHT");
        Put(m, 7, 6, SlotKind.Char,   "PIPE",    "|", "\\");
        Put(m, 7, 7, SlotKind.Edit,   "CLR_HOME");   // CLR shifted / HOME unshifted

        // ============================================================
        // Strobe 8 — numeric pad LEFT half (0, 1, 2, 4, 5, 7, 8).
        // ============================================================
        Put(m, 8, 0, SlotKind.Char,   "NP0", "0");
        Put(m, 8, 1, SlotKind.Unused, "s8b1");
        Put(m, 8, 2, SlotKind.Char,   "NP1", "1");
        Put(m, 8, 3, SlotKind.Char,   "NP2", "2");
        Put(m, 8, 4, SlotKind.Char,   "NP4", "4");
        Put(m, 8, 5, SlotKind.Char,   "NP5", "5");
        Put(m, 8, 6, SlotKind.Char,   "NP7", "7");
        Put(m, 8, 7, SlotKind.Char,   "NP8", "8");

        // ============================================================
        // Strobe 9 — numeric pad RIGHT half (., 00, 3, 6, 9, -, +).
        // ============================================================
        Put(m, 9, 0, SlotKind.Char,   "NP_DOT", ".");
        Put(m, 9, 1, SlotKind.Char,   "NP_00",  "00");
        Put(m, 9, 2, SlotKind.Char,   "NP3",    "3");
        Put(m, 9, 3, SlotKind.Unused, "s9b3");
        Put(m, 9, 4, SlotKind.Char,   "NP6",    "6");
        Put(m, 9, 5, SlotKind.Char,   "NP_MINUS", "-");
        Put(m, 9, 6, SlotKind.Char,   "NP9",    "9");
        Put(m, 9, 7, SlotKind.Char,   "NP_PLUS", "+");

        return m;
    }

    private static void Put(
        Dictionary<(int, int), Slot> m,
        int strobe, int bit, SlotKind kind, string id,
        string? unshifted = null, string? shifted = null)
    {
        m[(strobe, bit)] = new Slot(strobe, bit, kind, id, unshifted, shifted);
    }
}
