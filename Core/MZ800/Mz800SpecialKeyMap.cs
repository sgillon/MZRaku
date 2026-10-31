using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// MZ-800 analogue of <see cref="Mz700SpecialKeyMap"/>. PC virtual-key →
/// MZ-800 matrix (strobe, bit) for keys that don't produce a printable
/// character (cursor arrows, function keys, Enter, Esc/BREAK, Insert /
/// Delete / Backspace, MZ CTRL, ALPHA / GRAPH mode toggles, TAB).
///
/// Consulted by <see cref="Mz800Keyboard.OnKeyDown"/> before deferring
/// to the char-driven path. Non-printables never reach OnKeyPress and
/// would otherwise fall through unhandled.
///
/// Coordinates are drawn from <see cref="Mz800MatrixReference"/>. The
/// MZ-800 keyboard is almost identical to MZ-700's (per user
/// walk-through 2026-08-28 — same 10×8 shape, same slot positions for
/// nearly every key), so this map's structure mirrors
/// <see cref="Mz700SpecialKeyMap"/> exactly. The one MZ-800 addition is
/// <see cref="HostKey.Tab"/> → the (0, 3) TAB slot, which MZ-700 leaves
/// unused.
/// </summary>
public static class Mz800SpecialKeyMap
{
    public readonly record struct Entry(int Strobe, int Bit, bool? ExplicitMzShift);

    public static readonly Dictionary<HostKey, Entry> Map = new()
    {
        [HostKey.Enter]      = new(0, 0, null),   // CR
        [HostKey.Tab]        = new(0, 3, null),   // TAB (MZ-800 only)
        [HostKey.Left]       = new(7, 2, null),   // cursor ←
        [HostKey.Right]      = new(7, 3, null),   // cursor →
        [HostKey.Down]       = new(7, 4, null),   // cursor ↓
        [HostKey.Up]         = new(7, 5, null),   // cursor ↑
        [HostKey.Back]       = new(7, 6, null),   // DEL (Backspace mirror)
        [HostKey.Delete]     = new(7, 6, null),   // DEL
        [HostKey.Insert]     = new(7, 7, null),   // INST
        // BREAK sits at (8, 7) — same slot as MZ-700. Esc gives the
        // conventional PC "abort" muscle memory. SHIFT+Esc reaches
        // BREAK's shifted variant naturally.
        [HostKey.Escape]     = new(8, 7, null),   // BREAK
        // WinForms normalises Left/Right Ctrl KeyDowns to
        // ControlKey in KeyEventArgs.KeyCode, so match on the
        // generic form. Same pattern as MZ-700's Mz700SpecialKeyMap.
        [HostKey.ControlKey] = new(8, 6, null),   // CTRL
        [HostKey.F1]         = new(9, 7, null),
        [HostKey.F2]         = new(9, 6, null),
        [HostKey.F3]         = new(9, 5, null),
        [HostKey.F4]         = new(9, 4, null),
        [HostKey.F5]         = new(9, 3, null),
        // F11/F12 = GRAPH/ALPHA, same policy as MZ-700's Mz700SpecialKeyMap.
        // AltGr and RCtrl were rejected there because WinForms folds
        // them and AltGr's synthetic LCtrl briefly asserts MZ CTRL.
        [HostKey.F11]        = new(0, 6, null),   // GRAPH
        [HostKey.F12]        = new(0, 4, null),   // ALPHA
    };

    public static readonly IReadOnlyDictionary<HostKey, string> Labels = new Dictionary<HostKey, string>
    {
        [HostKey.Enter]       = "Enter",
        [HostKey.Tab]         = "Tab",
        [HostKey.Left]        = "cursor left",
        [HostKey.Right]       = "cursor right",
        [HostKey.Down]        = "cursor down",
        [HostKey.Up]          = "cursor up",
        [HostKey.Back]        = "Backspace (DEL)",
        [HostKey.Delete]      = "Delete",
        [HostKey.Insert]      = "Insert",
        [HostKey.Escape]      = "Esc (BREAK)",
        [HostKey.ControlKey]  = "Ctrl",
        [HostKey.LControlKey] = "Left Ctrl",
        [HostKey.RControlKey] = "Right Ctrl",
        [HostKey.F1]          = "F1",
        [HostKey.F2]          = "F2",
        [HostKey.F3]          = "F3",
        [HostKey.F4]          = "F4",
        [HostKey.F5]          = "F5",
        [HostKey.F11]         = "F11 (GRAPH)",
        [HostKey.F12]         = "F12 (ALPHA)",
    };

    /// <summary>
    /// Cross-checks <see cref="Map"/> against
    /// <see cref="Mz800MatrixReference"/>. Complains if any entry points
    /// at an out-of-range or Char/Unused/Blank/Unknown slot — this map
    /// is for non-printable slots only.
    /// </summary>
    internal static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var kv in Map)
        {
            var slot = Mz800MatrixReference.Get(kv.Value.Strobe, kv.Value.Bit);
            if (slot is null)
            {
                complaints.Add($"Mz800SpecialKeyMap[{kv.Key}] → ({kv.Value.Strobe}, {kv.Value.Bit}) is out of matrix range");
                continue;
            }
            var k = slot.Value.Kind;
            if (k == Mz800MatrixReference.SlotKind.Char ||
                k == Mz800MatrixReference.SlotKind.Unused ||
                k == Mz800MatrixReference.SlotKind.Blank ||
                k == Mz800MatrixReference.SlotKind.Unknown)
            {
                complaints.Add($"Mz800SpecialKeyMap[{kv.Key}] → ({kv.Value.Strobe}, {kv.Value.Bit}) is {k}; must point at a non-printable slot");
            }
        }
        return complaints;
    }
}
