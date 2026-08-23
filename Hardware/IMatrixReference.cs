using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Machine-agnostic view over one machine's keyboard matrix reference,
/// exposing the small surface <see cref="MatrixCoverage"/> and the
/// diagram-side helpers need to iterate the "cells the user is
/// expected to reach from the PC keyboard."
///
/// Both <see cref="Mz700MatrixReference"/> and
/// <see cref="Mz80aMatrixReference"/> expose a <c>.View</c> singleton
/// implementing this interface. The interface intentionally
/// stays narrow — the concrete references still own the per-machine
/// slot kinds, glyphs, and everything else; the shared helpers only
/// need this bit.
/// </summary>
public interface IMatrixReference
{
    /// <summary>Matrix row count (MZ-700 calls these rows, MZ-80A calls them strobes — same shape).</summary>
    int Rows { get; }

    /// <summary>Matrix column count (MZ-700 col; MZ-80A bit).</summary>
    int Cols { get; }

    /// <summary>
    /// Cells that are expected to be reachable from the PC keyboard —
    /// character slots, modifiers, mode toggles, edit keys, cursors,
    /// enter, space. Excludes Unused / Blank / Unknown (nothing wired
    /// there; no user expectation of reachability). Consumed by the
    /// unbound-slot finder so it only complains about cells that
    /// SHOULD have a binding.
    /// </summary>
    IEnumerable<MatrixReferenceCell> BindableCells { get; }

    /// <summary>
    /// Matrix coordinates of MZ SHIFT on this machine. Always
    /// considered "bound" — SHIFT is wired via the modifier path
    /// (Keyboard.OnKeyDown / OnKeyUp updating <c>_pcShift</c>) not
    /// through any override layer, so the unbound-slot finder must
    /// treat it as reachable. MZ-700 is (8, 0); MZ-80A is (0, 0).
    /// </summary>
    (int Row, int Col) ShiftSlot { get; }
}

/// <summary>
/// Minimal shared shape for a matrix cell surfaced through
/// <see cref="IMatrixReference"/>. Carries just what the unbound-slot
/// UI renders (coords + Id + glyph strings) — the concrete slot Kind
/// stays on each machine's native reference for callers that need it.
/// </summary>
public readonly record struct MatrixReferenceCell(
    int Row,
    int Col,
    string Id,
    string? UnshiftedGlyph = null,
    string? ShiftedGlyph = null);
