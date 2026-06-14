using System.Collections.Generic;
using System.Linq;

namespace MZ700Emul.Hardware;

/// <summary>
/// Cross-references the canonical <see cref="Mz700MatrixReference"/>
/// against everything that can put a PC key on a matrix slot
/// (<see cref="SpecialKeyMap"/>, <see cref="CharMap.Defaults"/>, plus the
/// user's live <see cref="KeyOverride"/> and <see cref="CharMapOverrides"/>
/// layers) so we can surface bindable reference cells that nothing
/// currently reaches.
///
/// Motivation: F5 was unbound for months because nothing forced a
/// reachability check from the reference side. The reverse question
/// ("is every PC binding pointing at a real slot?") is asked at startup
/// by the per-consumer Validate() methods; this is its mirror.
/// </summary>
public static class MatrixCoverage
{
    /// <summary>
    /// Slot kinds we consider "bindable" — every cell of one of these
    /// kinds is expected to be reachable from at least one PC binding.
    /// Unused / Blank / Unknown are excluded: Unused has no key cap,
    /// Blank is the filler dummy at (0, 7), and Unknown cells are
    /// already surfaced by <see cref="Mz700MatrixReference.Validate"/>.
    /// </summary>
    public static bool IsBindable(Mz700MatrixReference.SlotKind kind) => kind switch
    {
        Mz700MatrixReference.SlotKind.Char     => true,
        Mz700MatrixReference.SlotKind.Function => true,
        Mz700MatrixReference.SlotKind.Modifier => true,
        Mz700MatrixReference.SlotKind.Mode     => true,
        Mz700MatrixReference.SlotKind.Edit     => true,
        Mz700MatrixReference.SlotKind.Cursor   => true,
        Mz700MatrixReference.SlotKind.Enter    => true,
        Mz700MatrixReference.SlotKind.Space    => true,
        _ => false,
    };

    /// <summary>
    /// Returns every reference cell that is bindable but has no incoming
    /// PC binding from any of the four layers. Result is in (row, col)
    /// order so the caller can render it as a stable list.
    /// </summary>
    public static IReadOnlyList<Mz700MatrixReference.Slot> FindUnbound(
        CharMapOverrides? charOverrides,
        KeyOverride? keyOverrides)
    {
        var bound = CollectBoundSlots(charOverrides, keyOverrides);
        var unbound = new List<Mz700MatrixReference.Slot>();
        for (int r = 0; r < Mz700MatrixReference.Rows; r++)
        {
            for (int c = 0; c < Mz700MatrixReference.Cols; c++)
            {
                var slot = Mz700MatrixReference.All[(r, c)];
                if (!IsBindable(slot.Kind)) continue;
                if (!bound.Contains((r, c))) unbound.Add(slot);
            }
        }
        return unbound;
    }

    private static HashSet<(int row, int col)> CollectBoundSlots(
        CharMapOverrides? charOverrides,
        KeyOverride? keyOverrides)
    {
        var bound = new HashSet<(int, int)>();
        // MZ SHIFT (8, 0) is driven directly by Keyboard.SetShift in
        // response to the PC shift modifier — it doesn't have an entry
        // in SpecialKeyMap, but it is always reachable while the user
        // holds PC Shift.
        bound.Add((8, 0));
        foreach (var rc in SpecialKeyMap.Map.Values) bound.Add(rc);
        foreach (var p in CharMap.Defaults.Values) bound.Add((p.Row, p.Col));
        if (charOverrides != null)
            foreach (var kv in charOverrides.All) bound.Add((kv.Value.Row, kv.Value.Col));
        if (keyOverrides != null)
            foreach (var kv in keyOverrides.All) bound.Add((kv.Value.Row, kv.Value.Col));
        return bound;
    }
}
