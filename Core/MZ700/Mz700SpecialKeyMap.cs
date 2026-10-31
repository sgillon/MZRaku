using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Built-in defaults: PC virtual-key → MZ-700 matrix position for keys
/// that don't produce a printable character (cursor keys, function keys,
/// Enter, Esc, Backspace, Insert, MZ Ctrl, ALPHA / GRAPH mode keys).
/// These are handled directly in <see cref="Mz700Keyboard.OnKeyDown"/> — they
/// don't fire WinForms KeyPress, so the char-driven path can't see them.
///
/// Users can override any of these via <see cref="KeyOverride"/>, which
/// is consulted first. Mz700SpecialKeyMap entries are bare VKs only — modifier
/// combinations live in the override layer.
/// </summary>
public static class Mz700SpecialKeyMap
{
    public static readonly Dictionary<HostKey, (int row, int col)> Map = new()
    {
        [HostKey.Enter]       = (0, 0),
        [HostKey.Left]        = (7, 2),
        [HostKey.Right]       = (7, 3),
        [HostKey.Down]        = (7, 4),
        [HostKey.Up]          = (7, 5),
        [HostKey.Back]        = (7, 6),
        [HostKey.Delete]      = (7, 6),
        [HostKey.Insert]      = (7, 7),
        // BREAK lives on row-8 bit 7 (not bit 5 as previously guessed).
        // Discovered 2026-05-30 by tracing which row-8 reads BASIC acts
        // on during RUN: code at $04A9 does LD A,($E001); AND $81;
        // RET Z — masking bits 0 (SHIFT) and 7. The user manual notes
        // shifted BREAK is required to stop a program, which matches the
        // bit-0 + bit-7 combination exactly.
        [HostKey.Escape]      = (8, 7),
        // MZ Ctrl on either PC Ctrl. WinForms normalises Left/Right Ctrl
        // KeyDowns to the generic ControlKey in KeyEventArgs.KeyCode
        // (the lParam extended-key bit is the only way to tell them apart,
        // and OnKeyDown above masks via `keyData & HostKey.KeyCode`), so the
        // bare-VK lookup must use HostKey.ControlKey. Earlier code used
        // HostKey.LControlKey and never matched — MZ Ctrl was unreachable
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
        [HostKey.ControlKey]  = (8, 6),
        [HostKey.F1]          = (9, 7),
        [HostKey.F2]          = (9, 6),
        [HostKey.F3]          = (9, 5),
        [HostKey.F4]          = (9, 4),
        // F5 → (9, 3), confirmed against Owner's Manual 2026-06-12. The
        // diagram-drawing code (Mz700KeyboardLayout.cs) had inferred this
        // slot from row-9 symmetry months earlier but it was never wired
        // up here, so PC F5 had no MZ-side effect until now.
        [HostKey.F5]          = (9, 3),
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
        [HostKey.F11]         = (0, 6),  // F11 → GRAPH
        [HostKey.F12]         = (0, 4),  // F12 → ALPHA
    };

    /// <summary>
    /// Friendly labels for the PC virtual keys above — used by the
    /// keyboard-map editor's capture dialog to describe what was pressed.
    /// </summary>
    public static readonly IReadOnlyDictionary<HostKey, string> Labels = new Dictionary<HostKey, string>
    {
        [HostKey.Enter]       = "Enter",
        [HostKey.Left]        = "cursor left",
        [HostKey.Right]       = "cursor right",
        [HostKey.Down]        = "cursor down",
        [HostKey.Up]          = "cursor up",
        [HostKey.Back]        = "Backspace",
        [HostKey.Delete]      = "Delete",
        [HostKey.Insert]      = "Insert",
        [HostKey.Escape]      = "Esc (BREAK)",
        [HostKey.ControlKey]  = "Ctrl",
        // Kept for completeness; not reached at runtime — WinForms
        // normalises both to ControlKey before KeyDown fires.
        [HostKey.LControlKey] = "Left Ctrl",
        [HostKey.RControlKey] = "Right Ctrl",
        [HostKey.F1]          = "F1",
        [HostKey.F2]          = "F2",
        [HostKey.F3]          = "F3",
        [HostKey.F4]          = "F4",
        [HostKey.F5]          = "F5",
        [HostKey.F11]         = "F11",
        [HostKey.F12]         = "F12",
    };

    /// <summary>
    /// Cross-checks <see cref="Map"/> against
    /// <see cref="Mz700MatrixReference"/>. Returns a list of
    /// human-readable complaints; an empty list means the map
    /// points only at non-printable slots the reference agrees
    /// exist. Slot-label validity is now covered by
    /// <see cref="Mz700MatrixReference.SpecialLabels"/> living
    /// alongside the reference itself — see v1.2 audit F-024.
    /// </summary>
    internal static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var kv in Map)
        {
            var slot = Mz700MatrixReference.Get(kv.Value.row, kv.Value.col);
            if (slot is null)
            {
                complaints.Add($"Map[{kv.Key}] → ({kv.Value.row}, {kv.Value.col}) is out of matrix range");
                continue;
            }
            var k = slot.Value.Kind;
            if (k == Mz700MatrixReference.SlotKind.Char ||
                k == Mz700MatrixReference.SlotKind.Unused ||
                k == Mz700MatrixReference.SlotKind.Blank ||
                k == Mz700MatrixReference.SlotKind.Unknown)
            {
                complaints.Add($"Map[{kv.Key}] → ({kv.Value.row}, {kv.Value.col}) is {k} in the reference; SpecialKeyMap is for non-printable slots only");
            }
        }
        return complaints;
    }
}
