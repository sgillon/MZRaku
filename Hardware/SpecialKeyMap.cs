using System.Collections.Generic;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// Built-in defaults: PC virtual-key → MZ-700 matrix position for keys
/// that don't produce a printable character (cursor keys, function keys,
/// Enter, Esc, Backspace, Insert, MZ Ctrl, ALPHA / GRAPH mode keys).
/// These are handled directly in <see cref="Keyboard.OnKeyDown"/> — they
/// don't fire WinForms KeyPress, so the char-driven path can't see them.
///
/// Users can override any of these via <see cref="KeyOverride"/>, which
/// is consulted first. SpecialKeyMap entries are bare VKs only — modifier
/// combinations live in the override layer.
/// </summary>
public static class SpecialKeyMap
{
    public static readonly Dictionary<Keys, (int row, int col)> Map = new()
    {
        [Keys.Enter]       = (0, 0),
        [Keys.Left]        = (7, 2),
        [Keys.Right]       = (7, 3),
        [Keys.Down]        = (7, 4),
        [Keys.Up]          = (7, 5),
        [Keys.Back]        = (7, 6),
        [Keys.Delete]      = (7, 6),
        [Keys.Insert]      = (7, 7),
        // BREAK lives on row-8 bit 7 (not bit 5 as previously guessed).
        // Discovered 2026-05-30 by tracing which row-8 reads BASIC acts
        // on during RUN: code at $04A9 does LD A,($E001); AND $81;
        // RET Z — masking bits 0 (SHIFT) and 7. The user manual notes
        // shifted BREAK is required to stop a program, which matches the
        // bit-0 + bit-7 combination exactly.
        [Keys.Escape]      = (8, 7),
        // MZ Ctrl on either PC Ctrl. WinForms normalises Left/Right Ctrl
        // KeyDowns to the generic Keys.ControlKey in KeyEventArgs.KeyCode
        // (the lParam extended-key bit is the only way to tell them apart,
        // and OnKeyDown above masks via `keyData & Keys.KeyCode`), so the
        // bare-VK lookup must use Keys.ControlKey. Earlier code used
        // Keys.LControlKey and never matched — MZ Ctrl was unreachable
        // until 2026-06-12. Accepting both Ctrls fire MZ Ctrl is fine;
        // L/R distinction can be revisited if a user needs separate
        // bindings (would need a WndProc lParam-bit-24 read).
        //
        // MZ CTRL slot: per Owner's Manual the CTRL key lives at (8, 6),
        // NOT (9, 2) as earlier code assumed. (9, 2) appears to be unused
        // — pressing it in S-BASIC produces the same effect as shifted F5
        // (CHR$( output), suggesting either an alias or scan-decoder
        // quirk. Corrected 2026-06-12 after the user verified against
        // the owner's manual.
        [Keys.ControlKey]  = (8, 6),
        [Keys.F1]          = (9, 7),
        [Keys.F2]          = (9, 6),
        [Keys.F3]          = (9, 5),
        [Keys.F4]          = (9, 4),
        // F5 → (9, 3), confirmed against Owner's Manual 2026-06-12. The
        // diagram-drawing code (MzKeyboardLayout.cs) had inferred this
        // slot from row-9 symmetry months earlier but it was never wired
        // up here, so PC F5 had no MZ-side effect until now.
        [Keys.F5]          = (9, 3),
        // MZ-700 mode-toggle keys (page 142 of the owner's manual lists
        // GRAPH and ALPHA on row 1 — our row 0 — at D6 and D4 respectively).
        // GRAPH puts the machine in graphic-char input mode; ALPHA returns
        // to alphanumeric. Both are normal matrix keys from a hardware
        // perspective; the ROM holds the mode state and flips it on the
        // press edge.
        //
        // Initial plan was AltGr / Right Ctrl, but the diagnostic loop
        // revealed WinForms normalises Left/Right VKs to the generic form
        // (RMenu → Menu, RControlKey → ControlKey) — and AltGr emits a
        // synthetic LCtrl on Windows that would briefly assert MZ Ctrl.
        // Switched to F11/F12 as unambiguous defaults; the override layer
        // can rebind to anything. Note: F11 conflicts with the conventional
        // full-screen toggle — if/when full-screen lands we'll either move
        // it to Alt+Enter or rebind GRAPH.
        [Keys.F11]         = (0, 6),  // F11 → GRAPH
        [Keys.F12]         = (0, 4),  // F12 → ALPHA
    };

    /// <summary>
    /// Friendly labels for the PC virtual keys above — used by the
    /// keyboard-map editor's capture dialog to describe what was pressed.
    /// </summary>
    public static readonly IReadOnlyDictionary<Keys, string> Labels = new Dictionary<Keys, string>
    {
        [Keys.Enter]       = "Enter",
        [Keys.Left]        = "cursor left",
        [Keys.Right]       = "cursor right",
        [Keys.Down]        = "cursor down",
        [Keys.Up]          = "cursor up",
        [Keys.Back]        = "Backspace",
        [Keys.Delete]      = "Delete",
        [Keys.Insert]      = "Insert",
        [Keys.Escape]      = "Esc (BREAK)",
        [Keys.ControlKey]  = "Ctrl",
        // Kept for completeness; not reached at runtime — WinForms
        // normalises both to Keys.ControlKey before KeyDown fires.
        [Keys.LControlKey] = "Left Ctrl",
        [Keys.RControlKey] = "Right Ctrl",
        [Keys.F1]          = "F1",
        [Keys.F2]          = "F2",
        [Keys.F3]          = "F3",
        [Keys.F4]          = "F4",
        [Keys.F5]          = "F5",
        [Keys.F11]         = "F11",
        [Keys.F12]         = "F12",
    };

    /// <summary>
    /// Friendly labels for non-printable MZ-700 matrix slots — used by the
    /// keyboard-map editor's matrix grid to label cells that don't have a
    /// glyph (cursors, BREAK, GRAPH/ALPHA, MZ Ctrl, F1-F4, MZ Shift).
    /// </summary>
    public static readonly IReadOnlyDictionary<(int row, int col), string> SlotLabels = new Dictionary<(int row, int col), string>
    {
        [(0, 0)] = "Enter",
        [(0, 4)] = "ALPHA",
        [(0, 6)] = "GRAPH",
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
}
