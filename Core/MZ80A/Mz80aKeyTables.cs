using System.Collections.Generic;

namespace MZRaku.Core;

/// <summary>
/// Reverse lookup from MZ-80A display code to the (strobe, bit, MzShift)
/// key that types it, in normal (ALPHA) or GRAPH mode. Feeds the Font
/// Sheet's click-to-type (v1.5 4.8), which works in SA-5510 only
/// (<see cref="BasicRunning"/>, v1.5 4.8a). GRPH toggles the mode both
/// ways.
///
/// SA-5510 has no key scan of its own, so SA-1510's GETKY ($08CA) types
/// for the monitor and BASIC alike. Found by disassembly:
/// <list type="bullet">
///   <item>The scan ($0A50) gives index <c>(strobe - 1)*8 + (7 - bit)</c>
///   for strobes 1–9 (strobe 0 holds SHIFT, CTRL, GRPH and BREAK).</item>
///   <item>Indexes $38 and up (strobes 8 and 9) read the normal table in
///   either mode, whatever SHIFT says.</item>
///   <item>Below that, normal mode reads $0BEA, or $0C32 with SHIFT;
///   when $118F is set SHIFT counts inverted. GRAPH mode ($1170 not
///   zero) reads $0C6A, or $0CA2 with SHIFT. CTRL reads $0CDA (control
///   codes, not used here).</item>
///   <item>The tables hold display codes, which reach the screen as
///   they are.</item>
/// </list>
/// A scratch sweep typed every character key in both modes, with and
/// without SHIFT, through the auto-typer, at the SA-1510 prompt and at
/// SA-5510's Ready: all 205 drew the glyph this model gives.
///
/// Only wired character keys (and the space bar) count, per
/// <see cref="Mz80aMatrixReference.All"/>: the tables also hold codes
/// for control keys and for scan cells with no key behind them.
/// Unshifted entries win on duplicates.
/// </summary>
public sealed class Mz80aKeyTables
{
    private const int NormalTable = 0x0BEA;
    private const int ShiftedTable = 0x0C32;
    private const int GraphTable = 0x0C6A;
    private const int GraphShiftedTable = 0x0CA2;
    private const int SingleTableFrom = 0x38;   // strobes 8 and 9
    private const int Indexes = 72;             // strobes 1-9
    private const ushort GraphModeFlag = 0x1170;
    private const ushort ShiftInvertFlag = 0x118F;
    private const byte NoKey = 0xF0;

    private readonly Mz80aMemory _mem;
    private readonly Dictionary<(byte Code, bool Graph, bool Inverted), (int Strobe, int Bit, bool MzShift)> _map = new();
    private bool _built;

    internal Mz80aKeyTables(Mz80aMemory mem) { _mem = mem; }

    /// <summary>Forget the built map (new monitor ROM).</summary>
    internal void Invalidate()
    {
        _map.Clear();
        _built = false;
    }

    // SA-5510 loads byte for byte at $11FD; its line relinker at $1AC7
    // (v1.5 2.4) marks BASIC in RAM.
    private const ushort Sa5510Signature = 0x1AC7;
    private static readonly byte[] Sa5510SignatureBytes = { 0x21, 0x5C, 0x50, 0x5E, 0x23, 0x56, 0x2B, 0x7B };

    /// <summary>
    /// True while SA-5510 BASIC is in RAM with SA-1510 mapped in, so
    /// GETKY reads the keyboard for BASIC. The Font Sheet types in BASIC
    /// only (v1.5 4.8a); a program that swaps SA-1510 out ($E00C) scans
    /// for itself.
    /// </summary>
    public bool BasicRunning
    {
        get
        {
            if (_mem.RomSwapped) return false;
            for (int i = 0; i < Sa5510SignatureBytes.Length; i++)
                if (_mem.Ram[Sa5510Signature + i] != Sa5510SignatureBytes[i]) return false;
            return true;
        }
    }

    /// <summary>SA-1510's GRAPH mode flag ($1170), as GETKY reads it.</summary>
    public bool InGraphMode => _mem.Ram[GraphModeFlag] != 0;

    /// <summary>
    /// Returns the key that types display code <paramref name="code"/> in
    /// normal (false) or GRAPH (true) mode, or null if none does. SHIFT
    /// follows SA-1510's current inverted-shift flag ($118F).
    /// </summary>
    public (int Strobe, int Bit, bool MzShift)? FindByDisplayCode(byte code, bool graphMode)
    {
        if (!_built) Build();
        bool inverted = _mem.Ram[ShiftInvertFlag] != 0;
        return _map.TryGetValue((code, graphMode, inverted), out var key) ? key : null;
    }

    private void Build()
    {
        foreach (bool graph in new[] { false, true })
        foreach (bool inverted in new[] { false, true })
        foreach (bool shift in new[] { false, true })
            for (int index = 0; index < Indexes; index++)
            {
                int strobe = index / 8 + 1, bit = 7 - index % 8;
                if (!Mz80aMatrixReference.All.TryGetValue((strobe, bit), out var slot)) continue;
                if (slot.Kind != Mz80aMatrixReference.SlotKind.Char
                    && slot.Kind != Mz80aMatrixReference.SlotKind.Space) continue;
                byte code = _mem.Rom[Table(index, graph, inverted ? !shift : shift, shift)];
                if (code == NoKey) continue;
                var key = (code, graph, inverted);
                if (!_map.ContainsKey(key)) _map[key] = (strobe, bit, shift);
            }
        _built = true;
    }

    // GETKY's table pick ($08FC..$093D). Normal mode uses SHIFT after the
    // inversion; GRAPH mode uses the key as pressed.
    private static int Table(int index, bool graph, bool normalShift, bool graphShift)
    {
        if (index >= SingleTableFrom) return NormalTable + index;
        if (!graph) return (normalShift ? ShiftedTable : NormalTable) + index;
        return (graphShift ? GraphShiftedTable : GraphTable) + index;
    }
}
