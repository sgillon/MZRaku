using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// MZ-80A analogue of <see cref="Mz700SpecialKeyMap"/>. Built-in defaults for
/// PC virtual-keys that don't produce a printable character (Enter,
/// cursor arrows, INST/DEL, CLR/HOME, BREAK/CTRL, GRPH). Consulted by
/// <see cref="Mz80aKeyboard.OnKeyDown"/> before deferring to the
/// char-driven path — non-printables never reach OnKeyPress and would
/// otherwise fall through unhandled.
/// </summary>
public static class Mz80aSpecialKeyMap
{
    public readonly record struct Entry(int Strobe, int Bit, bool? ExplicitMzShift);

    public static readonly Dictionary<HostKey, Entry> Map = new()
    {
        [HostKey.Enter]      = new(7, 3, null),   // CR
        [HostKey.Up]         = new(7, 4, null),   // CURSOR_UP
        [HostKey.Right]      = new(7, 5, null),   // CURSOR_RIGHT
        // MZ-80A has no dedicated LEFT or DOWN slots — real hardware
        // uses Shift+RIGHT and Shift+UP respectively. Force-shift so
        // PC users get muscle-memory arrow behaviour without holding
        // Shift themselves.
        [HostKey.Left]       = new(7, 5, true),   // Shift+RIGHT → ←
        [HostKey.Down]       = new(7, 4, true),   // Shift+UP    → ↓
        // INST/DEL slot at (1, 2): unshifted = DELETE, shifted = INSERT
        // (user-confirmed 2026-07-12; polarity opposite of the pre-audit
        // hypothesis). Force-shift-off for the erase keys so PC
        // Shift+Delete/Backspace can't accidentally INSERT; PC Insert
        // gets the shifted variant.
        [HostKey.Delete]     = new(1, 2, false),  // DELETE
        [HostKey.Back]       = new(1, 2, false),  // DELETE (mirror Backspace)
        [HostKey.Insert]     = new(1, 2, true),   // INSERT
        [HostKey.Home]       = new(7, 7, null),   // CLR/HOME (HOME unshifted, CLR shifted)
        [HostKey.Escape]     = new(0, 7, null),   // CTRL unshifted; BREAK = Shift+Esc — authentic
        [HostKey.ControlKey] = new(0, 7, null),   // CTRL unshifted
        [HostKey.F11]        = new(0, 1, null),   // GRPH mode toggle
    };

    public static readonly IReadOnlyDictionary<HostKey, string> Labels = new Dictionary<HostKey, string>
    {
        [HostKey.Enter]      = "Enter",
        [HostKey.Up]         = "cursor up",
        [HostKey.Right]      = "cursor right",
        [HostKey.Left]       = "cursor left / ←",
        [HostKey.Delete]     = "Delete (INST/DEL)",
        [HostKey.Back]       = "Backspace",
        [HostKey.Home]       = "Home (CLR/HOME)",
        [HostKey.Escape]     = "Esc (BREAK)",
        [HostKey.ControlKey] = "Ctrl",
        [HostKey.F11]        = "F11 (GRPH)",
    };

    /// <summary>
    /// Cross-checks <see cref="Map"/> against <see cref="Mz80aMatrixReference"/>.
    /// Complains if any entry points at an out-of-range or Unused/Unknown slot.
    /// </summary>
    internal static IReadOnlyList<string> Validate()
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
