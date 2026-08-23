using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Given a machine's <see cref="IMatrixReference"/> and the set of
/// matrix slots that already have at least one PC-side binding,
/// returns the reference cells that no PC key currently reaches.
/// Feeds the "unbound slots" panel in the keyboard editor plus the
/// safety-gate check on Apply.
///
/// Motivation: F5 was unbound for months because nothing forced a
/// reachability check from the reference side. The reverse question
/// ("is every PC binding pointing at a real slot?") is asked at
/// startup by the per-consumer Validate() methods; this is its mirror.
///
/// Bindability is decided by the machine's own reference (via
/// <see cref="IMatrixReference.BindableCells"/>) — unused / blank /
/// unknown cells never appear in the search space.
/// </summary>
public static class MatrixCoverage
{
    /// <summary>
    /// Reference cells that no member of <paramref name="boundSlots"/>
    /// reaches. The reference's <see cref="IMatrixReference.ShiftSlot"/>
    /// is always considered bound (MZ SHIFT is wired via the modifier
    /// path, not through override layers). Result is emitted in the
    /// reference's iteration order so the caller can render it as a
    /// stable list.
    /// </summary>
    public static IReadOnlyList<MatrixReferenceCell> FindUnbound(
        IMatrixReference reference,
        IEnumerable<(int Row, int Col)> boundSlots)
    {
        var bound = new HashSet<(int, int)>();
        // MZ SHIFT: driven directly by PC Shift through the keyboard
        // modifier path, no override entry. Always reachable while the
        // user holds PC Shift.
        bound.Add(reference.ShiftSlot);
        foreach (var rc in boundSlots) bound.Add(rc);

        var unbound = new List<MatrixReferenceCell>();
        foreach (var cell in reference.BindableCells)
            if (!bound.Contains((cell.Row, cell.Col))) unbound.Add(cell);
        return unbound;
    }
}
