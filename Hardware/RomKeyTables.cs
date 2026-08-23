using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Reverse lookup from MZ-700 display code to the (row, col, MzShift)
/// slot that produces it on the keyboard. Built by reading the monitor
/// ROM's four key-translation tables, one per mode/shift combination:
/// <list type="bullet">
///   <item>$0BEA — ALPHA unshifted (bank 0)</item>
///   <item>$0C2A — ALPHA shifted   (bank 0)</item>
///   <item>$0C6A — GRAPH unshifted (bank 1) — discovered 2026-06-07</item>
///   <item>$0CAA — GRAPH shifted   (bank 1) — discovered 2026-06-07</item>
/// </list>
///
/// Each table is 64 bytes covering rows 0–7. Scan formula from the ROM's
/// scan routine at $0A50: <c>index = row*8 + (7 - col)</c>. Rows 8 and 9
/// hold modifier and function keys that don't produce display codes
/// directly. The four tables are dispatched at runtime via
/// (mode_flag &lt;&lt; 6) offset from the base — the same mode byte at
/// $1170 the ROM uses for the keyboard handler's lookup.
///
/// Unshifted entries win on duplicates so the simpler keystroke is the
/// canonical way to reach a given glyph. Banks are independent.
/// </summary>
public sealed class RomKeyTables
{
    public const int AlphaBank = 0;
    public const int GraphBank = 1;

    private const int AlphaUnshiftedOffset = 0x0BEA;
    private const int AlphaShiftedOffset   = 0x0C2A;
    private const int GraphUnshiftedOffset = 0x0C6A;
    private const int GraphShiftedOffset   = 0x0CAA;
    private const int TableLength = 64;

    private readonly Dictionary<(byte Code, int Bank), (int Row, int Col, bool MzShift)> _byCode = new();

    /// <summary>
    /// (Re)populate the inverse map from a freshly-loaded monitor ROM.
    /// Safe to call repeatedly; clears any prior state first.
    ///
    /// Skips slots that are mode / control keys (ALPHA, GRAPH, Enter,
    /// cursors, etc. — see <see cref="SpecialKeyMap.SlotLabels"/>):
    /// their bytes in the table are scan-side markers the ROM's
    /// keyboard handler intercepts, not display codes that reach VRAM.
    /// Without this filter the inverse map happily reports e.g. "$C9 is
    /// at slot (0,4)" — but pressing (0,4) just toggles ALPHA mode.
    /// </summary>
    public void Build(byte[] monitorRom)
    {
        _byCode.Clear();
        LoadTable(monitorRom, AlphaUnshiftedOffset, AlphaBank, mzShift: false);
        LoadTable(monitorRom, AlphaShiftedOffset,   AlphaBank, mzShift: true);
        LoadTable(monitorRom, GraphUnshiftedOffset, GraphBank, mzShift: false);
        LoadTable(monitorRom, GraphShiftedOffset,   GraphBank, mzShift: true);
    }

    private void LoadTable(byte[] rom, int offset, int bank, bool mzShift)
    {
        for (int i = 0; i < TableLength && offset + i < rom.Length; i++)
        {
            int row = i / 8;
            int col = 7 - (i % 8);
            if (SpecialKeyMap.SlotLabels.ContainsKey((row, col))) continue;
            byte code = rom[offset + i];
            var key = (code, bank);
            if (!_byCode.ContainsKey(key)) _byCode[key] = (row, col, mzShift);
        }
    }

    /// <summary>
    /// Returns the slot that produces the given display code in the
    /// requested bank (0 = ALPHA, 1 = GRAPH), or null if no slot does.
    /// Bank matters because the same matrix slot produces different
    /// display codes in ALPHA vs GRAPH mode.
    /// </summary>
    public (int Row, int Col, bool MzShift)? FindByDisplayCode(byte code, int bank = AlphaBank) =>
        _byCode.TryGetValue((code, bank), out var slot) ? slot : null;
}
