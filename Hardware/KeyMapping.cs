using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// User-editable mapping from PC keyboard keys to MZ-700 matrix positions.
/// Loaded from keymap.json alongside the executable so all users on the
/// machine share the same mapping; falls back to a built-in default when
/// the file is missing or invalid.
/// </summary>
public sealed class KeyMapping
{
    public sealed class Entry
    {
        public string PcKey { get; set; } = "";
        public int Row { get; set; }
        public int Col { get; set; }
        /// <summary>True if this binding requires PC Shift held.</summary>
        public bool Shift { get; set; }
        /// <summary>
        /// Force MZ shift OFF when the binding fires (regardless of PC
        /// shift). Use to access UNSHIFTED MZ glyphs via shifted PC keys
        /// (e.g. UK Shift+; → MZ ':' at unshifted (0,1)).
        /// Defaults to true for back-compat with keymap.json files saved
        /// before this field existed (pre-v2 had implicit override).
        /// </summary>
        public bool OverrideShift { get; set; } = true;
        /// <summary>
        /// Force MZ shift ON when the binding fires (regardless of PC
        /// shift). Use to access SHIFTED MZ glyphs via unshifted PC keys
        /// (e.g. UK = → MZ '=' at shifted (6,5)). Takes priority over
        /// OverrideShift if both are true.
        /// </summary>
        public bool ForceShifted { get; set; }
    }

    public List<Entry> Entries { get; set; } = new();

    /// <summary>
    /// All MZ-700 matrix positions exposed in the keymap dialog, with
    /// human-readable labels. Labels are not persisted — they're derived
    /// from this list so the dialog can describe each row even if the
    /// user has rebound it.
    /// </summary>
    // MZ-700 display codes are NOT ASCII-aligned for punctuation. Confirmed
    // by examining the font glyphs at the cited codes:
    //   $2A='-' $2B='=' $2C=';' $2D='/' $2E='.' $2F=','
    //   $4F=':' $40='@' $6A='+'
    // So matrix-position labels here describe the GLYPH the MZ-700 actually
    // produces when that key is pressed (per the unshifted ROM table at
    // $0BEA), not what ASCII assumes.
    //
    // The 4th tuple element marks SHIFTED-glyph entries — when the user
    // binds a PC key to one of these, the dialog auto-sets ForceShifted=true
    // so MZ shift is held when the binding fires, producing the shifted
    // glyph from the $0C2A table. Multiple entries may share (row,col) with
    // different shifted flags — they represent the unshifted vs shifted
    // characters at the same matrix position.
    public static readonly (int row, int col, string label, bool shiftedGlyph)[] Positions =
    {
        (0, 0, "CR / Enter", false),
        (0, 1, ": (colon)", false),
        (0, 2, "; (semicolon)", false),
        (0, 2, "+ (plus, shifted ;)", true),
        (1, 6, "Z", false),
        (1, 7, "Y", false),
        (2, 0, "X", false), (2, 1, "W", false), (2, 2, "V", false), (2, 3, "U", false),
        (2, 4, "T", false), (2, 5, "S", false), (2, 6, "R", false), (2, 7, "Q", false),
        (3, 0, "P", false), (3, 1, "O", false), (3, 2, "N", false), (3, 3, "M", false),
        (3, 4, "L", false), (3, 5, "K", false), (3, 6, "J", false), (3, 7, "I", false),
        (4, 0, "H", false), (4, 1, "G", false), (4, 2, "F", false), (4, 3, "E", false),
        (4, 4, "D", false), (4, 5, "C", false), (4, 6, "B", false), (4, 7, "A", false),
        (5, 0, "8", false), (5, 1, "7", false), (5, 2, "6", false), (5, 3, "5", false),
        (5, 4, "4", false), (5, 5, "3", false), (5, 6, "2", false), (5, 7, "1", false),
        (6, 0, ".", false), (6, 1, ", (comma)", false), (6, 2, "9", false), (6, 3, "0", false),
        (6, 4, "Space", false), (6, 5, "-", false), (6, 5, "= (equals, shifted -)", true),
        (6, 7, "\\", false),
        (7, 0, "/", false), (7, 1, "?", false),
        (7, 2, "Cursor Left", false), (7, 3, "Cursor Right", false),
        (7, 4, "Cursor Down", false), (7, 5, "Cursor Up", false),
        (7, 6, "Delete / Backspace", false), (7, 7, "Insert", false),
        (8, 5, "Escape / Break", false),
        (9, 2, "MZ Ctrl", false),
        (9, 3, "@ (at-sign)", false),
        (9, 4, "F4", false), (9, 5, "F3", false), (9, 6, "F2", false), (9, 7, "F1", false),
    };

    public static string LabelFor(int row, int col, bool shiftedGlyph = false)
    {
        foreach (var p in Positions)
            if (p.row == row && p.col == col && p.shiftedGlyph == shiftedGlyph) return p.label;
        // fallback: any entry at that position
        foreach (var p in Positions)
            if (p.row == row && p.col == col) return p.label;
        return $"({row},{col})";
    }

    public static KeyMapping LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<KeyMapping>(json);
                if (loaded?.Entries != null && loaded.Entries.Count > 0)
                    return loaded;
            }
        }
        catch { /* fall through to default on any parse error */ }
        return CreateDefault();
    }

    public void Save(string path)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }

    /// <summary>
    /// Build a (Keys, shift) → (row, col, overrideShift, forceShifted) lookup.
    /// Entries whose PcKey doesn't parse as a valid Keys value are silently
    /// dropped.
    /// </summary>
    public Dictionary<(Keys key, bool shift), (int row, int col, bool overrideShift, bool forceShifted)> ToKeysDictionary()
    {
        var result = new Dictionary<(Keys, bool), (int, int, bool, bool)>();
        foreach (var e in Entries)
        {
            if (Enum.TryParse<Keys>(e.PcKey, out var k))
                result[(k, e.Shift)] = (e.Row, e.Col, e.OverrideShift, e.ForceShifted);
        }
        return result;
    }

    /// <summary>Built-in defaults — same layout as the previous hardcoded map.</summary>
    public static KeyMapping CreateDefault()
    {
        var m = new KeyMapping();
        void Add(string key, int row, int col) =>
            m.Entries.Add(new Entry { PcKey = key, Row = row, Col = col });

        Add("Enter", 0, 0);
        // PC ';:' key (Oem1 = VK_OEM_1). On UK/US the unshifted character is
        // ';' which on the MZ-700 lives at (0,2). Shift+Oem1 gives ':' on PC,
        // and matrix bit (0,2) under shift returns '+' on the MZ — not ':' —
        // because the MZ-700's shift layout for that position differs from
        // PC's. Users wanting unshifted ':' can rebind a separate PC key to
        // (0,1) via the dialog.
        Add("Oem1", 0, 2);
        Add("Y", 1, 7); Add("Z", 1, 6);
        Add("Q", 2, 7); Add("R", 2, 6); Add("S", 2, 5); Add("T", 2, 4);
        Add("U", 2, 3); Add("V", 2, 2); Add("W", 2, 1); Add("X", 2, 0);
        Add("I", 3, 7); Add("J", 3, 6); Add("K", 3, 5); Add("L", 3, 4);
        Add("M", 3, 3); Add("N", 3, 2); Add("O", 3, 1); Add("P", 3, 0);
        Add("A", 4, 7); Add("B", 4, 6); Add("C", 4, 5); Add("D", 4, 4);
        Add("E", 4, 3); Add("F", 4, 2); Add("G", 4, 1); Add("H", 4, 0);
        Add("D1", 5, 7); Add("D2", 5, 6); Add("D3", 5, 5); Add("D4", 5, 4);
        Add("D5", 5, 3); Add("D6", 5, 2); Add("D7", 5, 1); Add("D8", 5, 0);
        Add("OemPeriod", 6, 0);
        // PC ',<' key → MZ ',' lives at (6,1), NOT (0,2). The MZ-700 display
        // code at (0,2) is ';' — display codes for punctuation are not
        // ASCII-aligned on the MZ-700.
        Add("Oemcomma", 6, 1);
        Add("D9", 6, 2); Add("D0", 6, 3);
        Add("Space", 6, 4); Add("OemMinus", 6, 5);
        Add("OemPipe", 6, 7); Add("OemBackslash", 6, 7);
        Add("OemQuestion", 7, 0); Add("Oem7", 7, 1);
        Add("Left", 7, 2); Add("Right", 7, 3);
        Add("Down", 7, 4); Add("Up", 7, 5);
        Add("Back", 7, 6); Add("Delete", 7, 6);
        Add("Insert", 7, 7);
        Add("Escape", 8, 5);
        Add("LControlKey", 9, 2); Add("RControlKey", 9, 2);
        Add("Oem3", 9, 3);          // '`' / '~' key on US, '@' on some UK layouts → MZ-700 '@'
        Add("F1", 9, 7); Add("F2", 9, 6); Add("F3", 9, 5); Add("F4", 9, 4);
        return m;
    }
}
