using System.Collections.Generic;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Reverse-lookup index from a machine's keyboard matrix back to the
/// PC keystrokes that currently produce each slot. Combines the four
/// binding layers a machine exposes through <see cref="IKeyboardEditorContext"/>:
/// built-in character defaults, user char-overrides, built-in
/// special-key map, and user VK-overrides — with the override layer
/// winning per the same precedence applied at runtime.
///
/// The output feeds two consumers:
/// - <see cref="BuildLabelsByMzKey"/> returns a per-MzKey label
///   string the diagram control renders on each cap.
/// - <see cref="BuildLabelsBySlotShift"/> is the fine-grained view
///   used by the safety gate to check that a character key's
///   unshifted AND shifted halves are both reachable.
///
/// Parameterised on <see cref="IKeyboardEditorContext"/> in v1.2
/// audit F-036 so both MZ-700 and MZ-80A run through the same code
/// paths. Previously read MZ-700 statics directly and the MZ-80A
/// diagram rendered label-less as a result.
/// </summary>
public static class PcKeyIndex
{
    /// <summary>
    /// For each <see cref="MzKeyboardLayout.MzKey"/> in
    /// <paramref name="layoutKeys"/>, the joined string of PC keys
    /// that produce the slot it represents. Suitable as the
    /// <c>PcKeyLabels</c> property on the diagram.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildLabelsByMzKey(
        IEnumerable<MzKeyboardLayout.MzKey> layoutKeys,
        IKeyboardEditorContext context)
    {
        var slotLabels = new Dictionary<(int row, int col), List<string>>();
        AccumulateChars(slotLabels, context);
        AccumulateVks(slotLabels, context);
        // MZ SHIFT is driven directly from PC Shift through the
        // keyboard modifier path (Keyboard.OnKeyDown / OnKeyUp
        // updating _pcShift, then ApplyShiftState writing the
        // effective bit). It doesn't appear in SpecialKeyMap or the
        // override layer, so inject a synthetic label at the
        // reference's ShiftSlot.
        var shiftSlot = context.ShiftSlot;
        AddLabel(slotLabels, shiftSlot.Row, shiftSlot.Col, "Shift");

        var result = new Dictionary<string, string>();
        foreach (var k in layoutKeys)
        {
            if (k.Row is null || k.Col is null) continue;
            if (!slotLabels.TryGetValue((k.Row.Value, k.Col.Value), out var list)) continue;
            result[k.Id] = string.Join(" ", list);
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="BuildLabelsByMzKey"/> but keyed by
    /// <c>(row, col, shift)</c> — needed by the safety gate so it
    /// can tell the unshifted and shifted halves of a character key
    /// apart. A character key with two glyphs needs coverage in both
    /// shift states to count as fully reachable;
    /// <see cref="BuildLabelsByMzKey"/> can't see that because it
    /// aggregates per (row, col).
    ///
    /// Shift-state accounting:
    /// - <see cref="IKeyboardEditorContext.CharOverrides"/> /
    ///   <see cref="IKeyboardEditorContext.CharDefaults"/>: each
    ///   entry has a definite <c>MzShift</c> bool — counted for
    ///   exactly that shift state.
    /// - <see cref="IKeyboardEditorContext.KeyOverrides"/>:
    ///   <c>MzShift</c> is tri-state. null (pass-through) covers
    ///   both states; true/false covers that one.
    /// - <see cref="IKeyboardEditorContext.SpecialKeyMap"/>:
    ///   shift-agnostic — pressing the VK produces the slot under
    ///   either shift state, so both count.
    /// </summary>
    public static IReadOnlyDictionary<(int row, int col, bool shift), IReadOnlyList<string>>
        BuildLabelsBySlotShift(IKeyboardEditorContext context)
    {
        var slotLabels = new Dictionary<(int row, int col, bool shift), List<string>>();

        var overriddenChars = new HashSet<char>();
        foreach (var kv in context.CharOverrides.All)
        {
            overriddenChars.Add(kv.Key);
            AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, kv.Value.MzShift, CharToLabel(kv.Key));
        }
        foreach (var kv in context.CharDefaults)
        {
            if (overriddenChars.Contains(kv.Key)) continue;
            if (context.CharOverrides.IsSuppressed(kv.Key)) continue;
            AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, kv.Value.MzShift, CharToLabel(kv.Key));
        }

        var overriddenVks = new HashSet<Keys>();
        foreach (var kv in context.KeyOverrides.All)
        {
            overriddenVks.Add(kv.Key);
            var label = VkToLabel(kv.Key, context);
            var s = kv.Value.MzShift;
            if (s is null or false)
                AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, false, label);
            if (s is null or true)
                AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, true, label);
        }
        foreach (var kv in context.SpecialKeyMap)
        {
            if (overriddenVks.Contains(kv.Key)) continue;
            var label = VkToLabel(kv.Key, context);
            AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, false, label);
            AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, true, label);
        }
        // See BuildLabelsByMzKey: MZ Shift is wired via the modifier
        // path, not the per-VK map. Cover both shift states so the
        // safety gate doesn't falsely flag the SHIFT cap as unreachable.
        var shiftSlot = context.ShiftSlot;
        AddShiftLabel(slotLabels, shiftSlot.Row, shiftSlot.Col, false, "Shift");
        AddShiftLabel(slotLabels, shiftSlot.Row, shiftSlot.Col, true, "Shift");

        var result = new Dictionary<(int, int, bool), IReadOnlyList<string>>();
        foreach (var kv in slotLabels)
            result[kv.Key] = kv.Value;
        return result;
    }

    private static void AddShiftLabel(
        Dictionary<(int row, int col, bool shift), List<string>> slotLabels,
        int row, int col, bool shift, string label)
    {
        var key = (row, col, shift);
        if (!slotLabels.TryGetValue(key, out var list))
        {
            list = new List<string>();
            slotLabels[key] = list;
        }
        if (!list.Contains(label)) list.Add(label);
    }

    private static void AccumulateChars(
        Dictionary<(int row, int col), List<string>> slotLabels,
        IKeyboardEditorContext context)
    {
        var overriddenChars = new HashSet<char>();
        foreach (var kv in context.CharOverrides.All)
        {
            overriddenChars.Add(kv.Key);
            AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, CharToLabel(kv.Key));
        }
        foreach (var kv in context.CharDefaults)
        {
            if (overriddenChars.Contains(kv.Key)) continue;
            if (context.CharOverrides.IsSuppressed(kv.Key)) continue;
            AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, CharToLabel(kv.Key));
        }
    }

    private static void AccumulateVks(
        Dictionary<(int row, int col), List<string>> slotLabels,
        IKeyboardEditorContext context)
    {
        var overriddenVks = new HashSet<Keys>();
        foreach (var kv in context.KeyOverrides.All)
        {
            overriddenVks.Add(kv.Key);
            AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, VkToLabel(kv.Key, context));
        }
        foreach (var kv in context.SpecialKeyMap)
        {
            if (overriddenVks.Contains(kv.Key)) continue;
            AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, VkToLabel(kv.Key, context));
        }
    }

    private static void AddLabel(
        Dictionary<(int row, int col), List<string>> slotLabels,
        int row, int col, string label)
    {
        if (!slotLabels.TryGetValue((row, col), out var list))
        {
            list = new List<string>();
            slotLabels[(row, col)] = list;
        }
        if (!list.Contains(label)) list.Add(label);
    }

    /// <summary>
    /// Friendly diagram-overlay text for a PC character. Letters
    /// canonicalise to uppercase so 'A' / 'a' (which share a matrix
    /// slot) collapse to one label; space becomes "Space"; everything
    /// else is rendered as the literal char.
    /// </summary>
    private static string CharToLabel(char c)
    {
        if (char.IsLetter(c)) return c.ToString().ToUpperInvariant();
        if (c == ' ') return "Space";
        return c.ToString();
    }

    /// <summary>
    /// Friendly diagram-overlay text for a PC virtual key — reads
    /// from the machine's own <see cref="IKeyboardEditorContext.SpecialKeyLabels"/>
    /// dictionary, falling back to the enum name when the VK isn't
    /// catalogued.
    /// </summary>
    private static string VkToLabel(Keys k, IKeyboardEditorContext context) =>
        context.SpecialKeyLabels.TryGetValue(k, out var s) ? s : k.ToString();
}
