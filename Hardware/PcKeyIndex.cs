using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Reverse-lookup index from the MZ-700 matrix back to the PC keystrokes
/// that currently produce each slot. Combines the four binding layers
/// — <see cref="CharMap.Defaults"/> + <see cref="CharMapOverrides"/> on
/// the character side, <see cref="SpecialKeyMap.Map"/> +
/// <see cref="KeyOverride"/> on the VK side — with the override layer
/// winning per the same precedence applied at runtime.
///
/// Two views are exposed:
/// - <see cref="BuildLabelsByMzKey"/> returns a per-MzKey label string
///   that the diagram control renders on each cap.
/// - <see cref="BuildSlotByPcLabel"/> is the inverse — given a friendly
///   PC label, what matrix slot does it produce? Used by the conflict
///   detector when the editor captures a new keystroke.
/// </summary>
public static class PcKeyIndex
{
    /// <summary>
    /// For each <see cref="MzKeyboardLayout.MzKey"/>, the joined string
    /// of PC keys that produce the slot it represents. Suitable as the
    /// <c>PcKeyLabels</c> property on the diagram.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildLabelsByMzKey(
        CharMapOverrides? charOverrides,
        KeyOverride? keyOverrides)
    {
        var slotLabels = new Dictionary<(int row, int col), List<string>>();
        AccumulateChars(slotLabels, charOverrides);
        AccumulateVks(slotLabels, keyOverrides);
        // MZ Shift (8, 0) is driven from PC Shift through
        // Keyboard.OnKeyDown / OnKeyUp + ApplyShiftState(), not via
        // SpecialKeyMap or the override layer. Inject a synthetic label
        // so the diagram badge and the safety gate both reflect that
        // the slot is reachable.
        AddLabel(slotLabels, 8, 0, "Shift");

        var result = new Dictionary<string, string>();
        foreach (var k in MzKeyboardLayout.Keys)
        {
            if (k.Row is null || k.Col is null) continue;
            if (!slotLabels.TryGetValue((k.Row.Value, k.Col.Value), out var list)) continue;
            result[k.Id] = string.Join(" ", list);
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="BuildLabelsByMzKey"/> but keyed by
    /// <c>(row, col, shift)</c> — needed by the safety gate (P2-9) so
    /// it can tell the unshifted and shifted halves of a character key
    /// apart. A character key with two glyphs needs coverage in both
    /// shift states to count as fully reachable;
    /// <see cref="BuildLabelsByMzKey"/> can't see that because it
    /// aggregates per (row, col).
    ///
    /// Shift-state accounting:
    /// - <see cref="CharMapOverrides"/> / <see cref="CharMap.Defaults"/>:
    ///   each entry has a definite <c>MzShift</c> bool — counted for
    ///   exactly that shift state.
    /// - <see cref="KeyOverride"/>: <c>MzShift</c> is tri-state. null
    ///   (pass-through) covers both states; true/false covers that one.
    /// - <see cref="SpecialKeyMap.Map"/>: shift-agnostic — pressing the
    ///   VK produces the slot under either shift state, so both count.
    /// </summary>
    public static IReadOnlyDictionary<(int row, int col, bool shift), IReadOnlyList<string>>
        BuildLabelsBySlotShift(
            CharMapOverrides? charOverrides,
            KeyOverride? keyOverrides)
    {
        var slotLabels = new Dictionary<(int row, int col, bool shift), List<string>>();

        var overriddenChars = new HashSet<char>();
        if (charOverrides != null)
        {
            foreach (var kv in charOverrides.All)
            {
                overriddenChars.Add(kv.Key);
                AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, kv.Value.MzShift, CharToLabel(kv.Key));
            }
        }
        foreach (var kv in CharMap.Defaults)
        {
            if (overriddenChars.Contains(kv.Key)) continue;
            if (charOverrides != null && charOverrides.IsSuppressed(kv.Key)) continue;
            AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, kv.Value.MzShift, CharToLabel(kv.Key));
        }

        var overriddenVks = new HashSet<Keys>();
        if (keyOverrides != null)
        {
            foreach (var kv in keyOverrides.All)
            {
                overriddenVks.Add(kv.Key);
                var label = VkToLabel(kv.Key);
                var s = kv.Value.MzShift;
                if (s is null or false)
                    AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, false, label);
                if (s is null or true)
                    AddShiftLabel(slotLabels, kv.Value.Row, kv.Value.Col, true, label);
            }
        }
        foreach (var kv in SpecialKeyMap.Map)
        {
            if (overriddenVks.Contains(kv.Key)) continue;
            var label = VkToLabel(kv.Key);
            AddShiftLabel(slotLabels, kv.Value.row, kv.Value.col, false, label);
            AddShiftLabel(slotLabels, kv.Value.row, kv.Value.col, true, label);
        }
        // See BuildLabelsByMzKey: MZ Shift is wired via the modifier path,
        // not the per-VK map. Cover both shift states so the safety gate
        // doesn't falsely flag the SHIFT caps as unreachable.
        AddShiftLabel(slotLabels, 8, 0, false, "Shift");
        AddShiftLabel(slotLabels, 8, 0, true, "Shift");

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

    /// <summary>
    /// Inverse view: friendly PC label → matrix slot. Used by the
    /// conflict detector (P2-5).
    /// </summary>
    public static IReadOnlyDictionary<string, (int row, int col)> BuildSlotByPcLabel(
        CharMapOverrides? charOverrides,
        KeyOverride? keyOverrides)
    {
        var result = new Dictionary<string, (int, int)>();

        // Character layer.
        var overriddenChars = new HashSet<char>();
        if (charOverrides != null)
        {
            foreach (var kv in charOverrides.All)
            {
                overriddenChars.Add(kv.Key);
                result[CharToLabel(kv.Key)] = (kv.Value.Row, kv.Value.Col);
            }
        }
        foreach (var kv in CharMap.Defaults)
        {
            if (overriddenChars.Contains(kv.Key)) continue;
            if (charOverrides != null && charOverrides.IsSuppressed(kv.Key)) continue;
            var label = CharToLabel(kv.Key);
            // Don't overwrite an earlier entry — first-wins matches the
            // dual-glyph case where 'A' and 'a' share a slot ('A' wins).
            if (!result.ContainsKey(label))
                result[label] = (kv.Value.Row, kv.Value.Col);
        }

        // VK layer.
        var overriddenVks = new HashSet<Keys>();
        if (keyOverrides != null)
        {
            foreach (var kv in keyOverrides.All)
            {
                overriddenVks.Add(kv.Key);
                result[VkToLabel(kv.Key)] = (kv.Value.Row, kv.Value.Col);
            }
        }
        foreach (var kv in SpecialKeyMap.Map)
        {
            if (overriddenVks.Contains(kv.Key)) continue;
            var label = VkToLabel(kv.Key);
            if (!result.ContainsKey(label))
                result[label] = (kv.Value.row, kv.Value.col);
        }

        return result;
    }

    private static void AccumulateChars(
        Dictionary<(int row, int col), List<string>> slotLabels,
        CharMapOverrides? overrides)
    {
        var overriddenChars = new HashSet<char>();
        if (overrides != null)
        {
            foreach (var kv in overrides.All)
            {
                overriddenChars.Add(kv.Key);
                AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, CharToLabel(kv.Key));
            }
        }
        foreach (var kv in CharMap.Defaults)
        {
            if (overriddenChars.Contains(kv.Key)) continue;
            if (overrides != null && overrides.IsSuppressed(kv.Key)) continue;
            AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, CharToLabel(kv.Key));
        }
    }

    private static void AccumulateVks(
        Dictionary<(int row, int col), List<string>> slotLabels,
        KeyOverride? overrides)
    {
        var overriddenVks = new HashSet<Keys>();
        if (overrides != null)
        {
            foreach (var kv in overrides.All)
            {
                overriddenVks.Add(kv.Key);
                AddLabel(slotLabels, kv.Value.Row, kv.Value.Col, VkToLabel(kv.Key));
            }
        }
        foreach (var kv in SpecialKeyMap.Map)
        {
            if (overriddenVks.Contains(kv.Key)) continue;
            AddLabel(slotLabels, kv.Value.row, kv.Value.col, VkToLabel(kv.Key));
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
    /// Friendly diagram-overlay text for a PC virtual key — reuses the
    /// labels defined in <see cref="SpecialKeyMap.Labels"/>, falling
    /// back to the enum name when the VK isn't catalogued.
    /// </summary>
    private static string VkToLabel(Keys k) =>
        SpecialKeyMap.Labels.TryGetValue(k, out var s) ? s : k.ToString();
}
