using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-80A adapter for <see cref="IKeyboardEditorContext"/>. Wraps the
/// MZ-80A statics (<see cref="Mz80aCharMap"/>,
/// <see cref="Mz80aSpecialKeyMap"/>, <see cref="Mz80aMatrixReference"/>)
/// and the caller-supplied mutable override layers so the shared
/// editor UI can consume them through the interface. Introduced in
/// v1.1 Phase 5.5b.
///
/// Slot-label lookup (used by the matrix grid and the VK editor)
/// derives from <see cref="Mz80aMatrixReference.All"/> rather than
/// a separate glyph catalog — see 5.5b design decision Q1.
/// </summary>
public sealed class Mz80aKeyboardEditorContext : IKeyboardEditorContext
{
    private readonly MZ80A _machine;
    private readonly Mz80aCharMapOverrides _charOverrides;
    private readonly IReadOnlyDictionary<(int Row, int Col), string> _slotLabels;

    public Mz80aKeyboardEditorContext(
        MZ80A machine,
        Mz80aCharMapOverrides charOverrides,
        KeyOverride keyOverrides)
    {
        _machine = machine;
        _charOverrides = charOverrides;
        KeyOverrides = keyOverrides;
        CharOverrides = new Adapter(charOverrides);
        CharDefaults = Mz80aCharMap.Defaults.ToDictionary(
            kv => kv.Key,
            kv => new MatrixPress(kv.Value.Strobe, kv.Value.Bit, kv.Value.MzShift));
        SpecialKeyMap = Mz80aSpecialKeyMap.Map.ToDictionary(
            kv => kv.Key,
            kv => ((int Row, int Col))(kv.Value.Strobe, kv.Value.Bit));
        // Slot labels derived from Mz80aMatrixReference: every non-Char /
        // non-Unused / non-Unknown slot exposes its Id string
        // (e.g. "SHIFT", "GRPH", "BREAK_CTRL", "CR", "CURSOR_UP").
        _slotLabels = Mz80aMatrixReference.All.Values
            .Where(s => s.Kind != Mz80aMatrixReference.SlotKind.Char
                     && s.Kind != Mz80aMatrixReference.SlotKind.Unused
                     && s.Kind != Mz80aMatrixReference.SlotKind.Unknown)
            .ToDictionary(s => ((int Row, int Col))(s.Strobe, s.Bit), s => s.Id);
    }

    public string MachineLabel => "MZ-80A";

    public byte PeekMatrixRow(int strobe) => _machine.Keyboard.PeekMatrixRow(strobe);

    public IReadOnlyDictionary<char, MatrixPress> CharDefaults { get; }

    public ICharMapOverridesView CharOverrides { get; }

    public IReadOnlyDictionary<Keys, (int Row, int Col)> SpecialKeyMap { get; }

    // Mz80aSpecialKeyMap has Labels but no per-slot labels. The MZ-700
    // sibling exposes SpecialKeyMap.Labels for VK captions on the
    // matrix grid — same purpose here.
    public IReadOnlyDictionary<Keys, string> SpecialKeyLabels =>
        Mz80aSpecialKeyMap.Labels;

    public IReadOnlyDictionary<(int Row, int Col), string> SlotLabels => _slotLabels;

    public KeyOverride KeyOverrides { get; }

    public (int Row, int Col) ShiftSlot => (0, 0);

    /// <summary>
    /// Canonical printable glyph at (row, col, mzShift) via
    /// <see cref="Mz80aMatrixReference"/>. First char of the slot's
    /// glyph string; null if the slot has no printable glyph on this
    /// side (unshifted-only slots, non-Char slots, etc.).
    /// </summary>
    public char? FindGlyphAt(int row, int col, bool mzShift)
    {
        if (!Mz80aMatrixReference.All.TryGetValue((row, col), out var slot)) return null;
        var g = mzShift ? slot.ShiftedGlyph : slot.UnshiftedGlyph;
        return string.IsNullOrEmpty(g) ? null : g![0];
    }

    public string? FindSpecialLabelAt(int row, int col) =>
        _slotLabels.TryGetValue((row, col), out var s) ? s : null;

    /// <summary>
    /// Reference cells nothing currently reaches. Mirrors the MZ-700
    /// <see cref="MatrixCoverage.FindUnbound"/> approach but built
    /// inline against MZ-80A data — <see cref="MatrixCoverage"/>
    /// itself is MZ-700-specific and can't be reused. Considers a
    /// slot "bound" if it appears in <see cref="Mz80aCharMap.Defaults"/>
    /// (minus suppressed chars), the char-override layer, the
    /// special-key map, or the VK-override layer.
    /// </summary>
    public IReadOnlyList<MatrixReferenceSlot> FindUnboundSlots()
    {
        var bound = new HashSet<(int, int)>();
        // MZ SHIFT (0, 0) is driven directly by Mz80aKeyboard.OnKeyDown's
        // ApplyShiftState in response to PC shift — no explicit
        // SpecialKeyMap entry, but always reachable while PC Shift is held.
        bound.Add((0, 0));
        foreach (var kv in Mz80aSpecialKeyMap.Map)
            bound.Add((kv.Value.Strobe, kv.Value.Bit));
        foreach (var kv in Mz80aCharMap.Defaults)
        {
            if (_charOverrides.IsSuppressed(kv.Key)) continue;
            bound.Add((kv.Value.Strobe, kv.Value.Bit));
        }
        foreach (var kv in _charOverrides.All)
            bound.Add((kv.Value.Strobe, kv.Value.Bit));
        foreach (var kv in KeyOverrides.All)
            bound.Add((kv.Value.Row, kv.Value.Col));

        var unbound = new List<MatrixReferenceSlot>();
        for (int r = 0; r < Mz80aMatrixReference.Strobes; r++)
        {
            for (int c = 0; c < Mz80aMatrixReference.Bits; c++)
            {
                var slot = Mz80aMatrixReference.All[(r, c)];
                if (!IsBindable(slot.Kind)) continue;
                if (!bound.Contains((r, c)))
                    unbound.Add(new MatrixReferenceSlot(
                        r, c, MapKind(slot.Kind), slot.Id, slot.UnshiftedGlyph, slot.ShiftedGlyph));
            }
        }
        return unbound;
    }

    // Bindability rule — mirrors the MZ-700 MatrixCoverage.IsBindable
    // logic. Unused / Unknown cells are excluded (Unused has no wired
    // key; Unknown is surfaced by the reference's own Validate).
    private static bool IsBindable(Mz80aMatrixReference.SlotKind kind) => kind switch
    {
        Mz80aMatrixReference.SlotKind.Char     => true,
        Mz80aMatrixReference.SlotKind.Modifier => true,
        Mz80aMatrixReference.SlotKind.Mode     => true,
        Mz80aMatrixReference.SlotKind.Edit     => true,
        Mz80aMatrixReference.SlotKind.Cursor   => true,
        Mz80aMatrixReference.SlotKind.Enter    => true,
        Mz80aMatrixReference.SlotKind.Space    => true,
        _ => false,
    };

    private static MatrixSlotKind MapKind(Mz80aMatrixReference.SlotKind k) => k switch
    {
        Mz80aMatrixReference.SlotKind.Char     => MatrixSlotKind.Char,
        Mz80aMatrixReference.SlotKind.Modifier => MatrixSlotKind.Modifier,
        Mz80aMatrixReference.SlotKind.Mode     => MatrixSlotKind.Mode,
        Mz80aMatrixReference.SlotKind.Edit     => MatrixSlotKind.Edit,
        Mz80aMatrixReference.SlotKind.Cursor   => MatrixSlotKind.Cursor,
        Mz80aMatrixReference.SlotKind.Enter    => MatrixSlotKind.Enter,
        Mz80aMatrixReference.SlotKind.Space    => MatrixSlotKind.Space,
        Mz80aMatrixReference.SlotKind.Unused   => MatrixSlotKind.Unused,
        _                                      => MatrixSlotKind.Unknown,
    };

    /// <summary>
    /// Wraps <see cref="Mz80aCharMapOverrides"/> in the machine-
    /// agnostic <see cref="ICharMapOverridesView"/> shape. Every read
    /// converts <see cref="Mz80aCharMap.Press"/> → <see cref="MatrixPress"/>;
    /// every write converts back.
    /// </summary>
    private sealed class Adapter : ICharMapOverridesView
    {
        private readonly Mz80aCharMapOverrides _inner;
        public Adapter(Mz80aCharMapOverrides inner) => _inner = inner;

        public bool TryLookup(char c, out MatrixPress press)
        {
            if (_inner.TryLookup(c, out var p))
            {
                press = new MatrixPress(p.Strobe, p.Bit, p.MzShift);
                return true;
            }
            press = default;
            return false;
        }

        public void Set(char c, MatrixPress press) =>
            _inner.Set(c, new Mz80aCharMap.Press(press.Row, press.Col, press.MzShift));

        public void Remove(char c) => _inner.Remove(c);
        public void Suppress(char c) => _inner.Suppress(c);
        public void Unsuppress(char c) => _inner.Unsuppress(c);
        public bool IsSuppressed(char c) => _inner.IsSuppressed(c);

        public IEnumerable<KeyValuePair<char, MatrixPress>> All =>
            _inner.All.Select(kv => new KeyValuePair<char, MatrixPress>(
                kv.Key, new MatrixPress(kv.Value.Strobe, kv.Value.Bit, kv.Value.MzShift)));
    }
}
