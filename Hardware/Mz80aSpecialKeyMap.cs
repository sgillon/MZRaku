using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-80A analogue of <see cref="SpecialKeyMap"/>. Built-in defaults for
/// PC virtual-keys that don't produce a printable character (Enter,
/// cursor arrows, INST/DEL, CLR/HOME, BREAK/CTRL, GRPH). Consulted by
/// <see cref="Mz80aKeyboard.OnKeyDown"/> before deferring to the
/// char-driven path — non-printables never reach OnKeyPress and would
/// otherwise fall through unhandled.
/// </summary>
public static class Mz80aSpecialKeyMap
{
    public readonly record struct Entry(int Strobe, int Bit, bool? ExplicitMzShift);

    public static readonly Dictionary<Keys, Entry> Map = new()
    {
        [Keys.Enter]      = new(7, 3, null),   // CR
        [Keys.Up]         = new(7, 4, null),   // CURSOR_UP
        [Keys.Right]      = new(7, 5, null),   // CURSOR_RIGHT
        // MZ-80A has no dedicated LEFT or DOWN slots — real hardware
        // uses Shift+RIGHT and Shift+UP respectively. Force-shift so
        // PC users get muscle-memory arrow behaviour without holding
        // Shift themselves.
        [Keys.Left]       = new(7, 5, true),   // Shift+RIGHT → ←
        [Keys.Down]       = new(7, 4, true),   // Shift+UP    → ↓
        // INST/DEL slot at (1, 2): unshifted = DELETE, shifted = INSERT
        // (user-confirmed 2026-07-12; polarity opposite of the pre-audit
        // hypothesis). Force-shift-off for the erase keys so PC
        // Shift+Delete/Backspace can't accidentally INSERT; PC Insert
        // gets the shifted variant.
        [Keys.Delete]     = new(1, 2, false),  // DELETE
        [Keys.Back]       = new(1, 2, false),  // DELETE (mirror Backspace)
        [Keys.Insert]     = new(1, 2, true),   // INSERT
        [Keys.Home]       = new(7, 7, null),   // CLR/HOME (HOME unshifted, CLR shifted)
        [Keys.Escape]     = new(0, 7, null),   // CTRL unshifted; BREAK = Shift+Esc — authentic
        [Keys.ControlKey] = new(0, 7, null),   // CTRL unshifted
        [Keys.F11]        = new(0, 1, null),   // GRPH mode toggle
    };

    public static readonly IReadOnlyDictionary<Keys, string> Labels = new Dictionary<Keys, string>
    {
        [Keys.Enter]      = "Enter",
        [Keys.Up]         = "cursor up",
        [Keys.Right]      = "cursor right",
        [Keys.Left]       = "cursor left / ←",
        [Keys.Delete]     = "Delete (INST/DEL)",
        [Keys.Back]       = "Backspace",
        [Keys.Home]       = "Home (CLR/HOME)",
        [Keys.Escape]     = "Esc (BREAK)",
        [Keys.ControlKey] = "Ctrl",
        [Keys.F11]        = "F11 (GRPH)",
    };

    /// <summary>
    /// Cross-checks <see cref="Map"/> against <see cref="Mz80aMatrixReference"/>.
    /// Complains if any entry points at an out-of-range or Unused/Unknown slot.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();
        foreach (var kv in Map)
        {
            if (!Mz80aMatrixReference.All.TryGetValue((kv.Value.Strobe, kv.Value.Bit), out var slot))
            {
                complaints.Add($"Mz80aSpecialKeyMap[{kv.Key}] → ({kv.Value.Strobe}, {kv.Value.Bit}) is out of matrix range");
                continue;
            }
            if (slot.Kind == Mz80aMatrixReference.SlotKind.Unused ||
                slot.Kind == Mz80aMatrixReference.SlotKind.Unknown)
            {
                complaints.Add($"Mz80aSpecialKeyMap[{kv.Key}] → ({kv.Value.Strobe}, {kv.Value.Bit}) is {slot.Kind}; must point at a wired slot");
            }
        }
        return complaints;
    }
}
