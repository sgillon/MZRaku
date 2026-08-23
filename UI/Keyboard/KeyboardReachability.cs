using System.Collections.Generic;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Machine-agnostic reachability check for the safety gate on
/// Settings → Apply. Answers "is every essential MZ key producible
/// from at least one PC binding today?" using
/// <see cref="PcKeyIndex.BuildLabelsBySlotShift"/>'s per-shift map
/// as evidence and the machine's own
/// <see cref="IMatrixReference.IsKnownUnreachableFromPc"/> for the
/// by-design exemptions.
///
/// Split out of <c>SettingsForm.cs</c> in v1.2 audit F-052 so both
/// MZ-700 and MZ-80A safety gates run through one implementation
/// (the MZ-80A gate was previously silent — see F-036 for the
/// diagram-side half of the fix and F-052 for the extraction
/// rationale). Sits in UI/Keyboard because it consumes
/// <see cref="IKeyboardEditorContext"/>, not because the concept is
/// UI-shaped — a v2.0 Avalonia port would still call this helper.
/// </summary>
internal static class KeyboardReachability
{
    /// <summary>
    /// A character key with both unshifted and shifted glyphs is
    /// "fully reachable" only if both halves can be produced from
    /// the host keyboard — losing just the shifted half (e.g. PC '1'
    /// rebound but Shift+1 still maps to MZ '!') still leaves the
    /// unshifted half unreachable, which the gate must surface.
    ///
    /// Fixed-label keys (CR, GRAPH, ALPHA, CTRL, SHIFT, BREAK, INST,
    /// DEL, cursors) are shift-agnostic — any binding in either
    /// shift state is enough.
    ///
    /// Glyphs flagged by
    /// <see cref="IMatrixReference.IsKnownUnreachableFromPc"/>
    /// (MZ-700 reverse-apostrophe at AT-shifted, ↓ and £ at POUND;
    /// MZ-80A currently has no such exemptions) count as reachable
    /// here — they're not on a PC keyboard by design, so the gate
    /// shouldn't nag every Apply.
    /// </summary>
    public static bool IsKeyFullyReachable(
        MzKeyboardLayout.MzKey k,
        IReadOnlyDictionary<(int row, int col, bool shift), IReadOnlyList<string>> labels,
        IKeyboardEditorContext context)
    {
        if (!k.Row.HasValue || !k.Col.HasValue) return true;
        int row = k.Row.Value, col = k.Col.Value;

        if (!string.IsNullOrEmpty(k.FixedLabel))
            return labels.ContainsKey((row, col, false))
                || labels.ContainsKey((row, col, true));

        bool hasUnshifted = !string.IsNullOrEmpty(k.UnshiftedLabel)
            || context.FindGlyphAt(row, col, false).HasValue;
        bool hasShifted = !string.IsNullOrEmpty(k.ShiftedLabel)
            || context.FindGlyphAt(row, col, true).HasValue;

        if (hasUnshifted
            && !labels.ContainsKey((row, col, false))
            && !context.MatrixReference.IsKnownUnreachableFromPc(row, col, false))
            return false;
        if (hasShifted
            && !labels.ContainsKey((row, col, true))
            && !context.MatrixReference.IsKnownUnreachableFromPc(row, col, true))
            return false;
        return true;
    }

    /// <summary>
    /// Renders one <see cref="MzKeyboardLayout.MzKey"/> as the short
    /// label the safety-gate confirm dialog lists. Preference order:
    /// fixed label > unshifted layout override > shifted layout
    /// override > canonical unshifted glyph > canonical shifted
    /// glyph > synthetic "(row,col)" coord fallback. Glyph lookups
    /// go through the context adapter so MZ-700 reads
    /// <see cref="MzGlyphCatalog"/> and MZ-80A reads
    /// <see cref="Mz80aMatrixReference.FindGlyph"/>.
    /// </summary>
    public static string DescribeKeyForGate(MzKeyboardLayout.MzKey k, IKeyboardEditorContext context)
    {
        if (!string.IsNullOrEmpty(k.FixedLabel)) return k.FixedLabel!;
        if (!string.IsNullOrEmpty(k.UnshiftedLabel)) return k.UnshiftedLabel!;
        if (!string.IsNullOrEmpty(k.ShiftedLabel)) return k.ShiftedLabel!;
        if (k.Row.HasValue && k.Col.HasValue)
        {
            var c = context.FindGlyphAt(k.Row.Value, k.Col.Value, false)
                  ?? context.FindGlyphAt(k.Row.Value, k.Col.Value, true);
            if (c.HasValue) return c.Value.ToString();
            return $"({k.Row}, {k.Col})";
        }
        return k.Id;
    }
}
