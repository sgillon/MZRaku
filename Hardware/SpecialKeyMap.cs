using System.Collections.Generic;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// PC virtual-key → MZ-700 matrix position for keys that don't produce a
/// printable character (cursor keys, function keys, Enter, Esc, Backspace,
/// Insert, MZ Ctrl). These are handled directly in OnKeyDown — they don't
/// fire WinForms KeyPress, so the char-driven path can't see them.
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
        [Keys.Escape]      = (8, 5),
        [Keys.LControlKey] = (9, 2),
        [Keys.RControlKey] = (9, 2),
        [Keys.F1]          = (9, 7),
        [Keys.F2]          = (9, 6),
        [Keys.F3]          = (9, 5),
        [Keys.F4]          = (9, 4),
    };
}
