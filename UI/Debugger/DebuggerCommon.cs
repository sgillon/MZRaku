using System;
using System.Globalization;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Shared helpers for the debugger-side windows
/// (<see cref="DebuggerForm"/>, <see cref="MemoryViewerForm"/>).
/// Both used to carry byte-identical copies of these three utilities;
/// pulling them here means any tweak lands once and the two panes
/// stay in step.
/// </summary>
internal static class DebuggerCommon
{
    /// <summary>
    /// Parses a hex address string as entered in the Debugger /
    /// Memory-Viewer address boxes. Accepts a leading "$" or "0x"
    /// prefix, requires the value fit in 16 bits.
    /// </summary>
    public static bool TryParseAddr(string s, out ushort addr)
    {
        addr = 0;
        s = s.Trim();
        if (s.StartsWith("$", StringComparison.Ordinal)) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        if (v < 0 || v > 0xFFFF) return false;
        addr = (ushort)v;
        return true;
    }

    /// <summary>
    /// Sets a Control's Text only if it actually changed. WinForms
    /// forces a full redraw on every Text assignment; per-frame
    /// UpdateStatus calls that write the same string flicker without
    /// this guard.
    /// </summary>
    public static void SetTextIfChanged(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }

    /// <summary>
    /// True if the address falls in the MZ-700 PPI/PIT I/O window
    /// ($E000-$E00F). Reads there have hardware side effects (PIT
    /// counter latches, keyboard scan); disassembly and raw byte
    /// display must report zero rather than disturb hardware state.
    /// </summary>
    public static bool IsMzIoWindow(ushort addr) => addr >= 0xE000 && addr <= 0xE00F;
}
