using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-800 adapter for <see cref="IKeyboardEditorContext"/>. Wraps the
/// MZ-800 statics (<see cref="Mz800CharMap"/>,
/// <see cref="Mz800SpecialKeyMap"/>, <see cref="Mz800MatrixReference"/>,
/// <see cref="Mz800KeyboardLayout"/>) and the caller-supplied mutable
/// override layers so the shared editor UI (matrix grid, diagram,
/// per-slot and per-binding editors) serves the MZ-800 unchanged.
/// Modelled on <see cref="Mz80aKeyboardEditorContext"/>; slot labels
/// come from <see cref="Mz800MatrixReference.SpecialLabels"/>.
/// </summary>
public sealed class Mz800KeyboardEditorContext : IKeyboardEditorContext
{
    private readonly MZ800 _machine;
    private readonly Mz800CharMapOverrides _charOverrides;
    private readonly IReadOnlyDictionary<(int Row, int Col), string> _slotLabels;

    public Mz800KeyboardEditorContext(
        MZ800 machine,
        Mz800CharMapOverrides charOverrides,
        KeyOverride keyOverrides)
    {
        _machine = machine;
        _charOverrides = charOverrides;
        KeyOverrides = keyOverrides;
        CharOverrides = new Adapter(charOverrides);
        CharDefaults = Mz800CharMap.Defaults.ToDictionary(
            kv => kv.Key,
            kv => new MatrixPress(kv.Value.Strobe, kv.Value.Bit, kv.Value.MzShift));
        SpecialKeyMap = Mz800SpecialKeyMap.Map.ToDictionary(
            kv => kv.Key,
            kv => ((int Row, int Col))(kv.Value.Strobe, kv.Value.Bit));
        _slotLabels = Mz800MatrixReference.SpecialLabels.ToDictionary(
            kv => ((int Row, int Col))(kv.Key.strobe, kv.Key.bit), kv => kv.Value);
    }

    public string MachineLabel => "MZ-800";

    public byte PeekMatrixRow(int strobe) => _machine.Keyboard.PeekMatrixRow(strobe);

    public IReadOnlyDictionary<char, MatrixPress> CharDefaults { get; }

    public ICharMapOverridesView CharOverrides { get; }

    public IReadOnlyDictionary<Keys, (int Row, int Col)> SpecialKeyMap { get; }

    public IReadOnlyDictionary<Keys, string> SpecialKeyLabels => Mz800SpecialKeyMap.Labels;

    public IReadOnlyDictionary<(int Row, int Col), string> SlotLabels => _slotLabels;

    public KeyOverride KeyOverrides { get; }

    public (int Row, int Col) ShiftSlot => (8, 0);

    public IMatrixReference MatrixReference => Mz800MatrixReference.View;
    public IReadOnlyList<MzKeyboardLayout.MzKey> LayoutKeys => Mz800KeyboardLayout.Keys;
    public IEnumerable<MzKeyboardLayout.MzKey> EssentialLayoutKeys => Mz800KeyboardLayout.EssentialKeys;

    public char? FindGlyphAt(int row, int col, bool mzShift) =>
        Mz800MatrixReference.FindGlyph(row, col, mzShift);

    public string? FindSpecialLabelAt(int row, int col) =>
        _slotLabels.TryGetValue((row, col), out var s) ? s : null;

    public IReadOnlyList<MatrixReferenceSlot> FindUnboundSlots()
    {
        var unbound = MatrixCoverage.FindUnbound(Mz800MatrixReference.View, CollectBoundSlots());
        return unbound.Select(c =>
        {
            var kind = Mz800MatrixReference.All[(c.Row, c.Col)].Kind;
            return new MatrixReferenceSlot(c.Row, c.Col, MapKind(kind), c.Id, c.UnshiftedGlyph, c.ShiftedGlyph);
        }).ToList();
    }

    private IEnumerable<(int Row, int Col)> CollectBoundSlots()
    {
        foreach (var kv in Mz800SpecialKeyMap.Map)
            yield return (kv.Value.Strobe, kv.Value.Bit);
        foreach (var kv in Mz800CharMap.Defaults)
        {
            if (_charOverrides.IsSuppressed(kv.Key)) continue;
            yield return (kv.Value.Strobe, kv.Value.Bit);
        }
        foreach (var kv in _charOverrides.All)
            yield return (kv.Value.Strobe, kv.Value.Bit);
        foreach (var kv in KeyOverrides.All)
            yield return (kv.Value.Row, kv.Value.Col);
    }

    private static MatrixSlotKind MapKind(Mz800MatrixReference.SlotKind k) => k switch
    {
        Mz800MatrixReference.SlotKind.Char     => MatrixSlotKind.Char,
        Mz800MatrixReference.SlotKind.Function => MatrixSlotKind.Function,
        Mz800MatrixReference.SlotKind.Modifier => MatrixSlotKind.Modifier,
        Mz800MatrixReference.SlotKind.Mode     => MatrixSlotKind.Mode,
        Mz800MatrixReference.SlotKind.Edit     => MatrixSlotKind.Edit,
        Mz800MatrixReference.SlotKind.Cursor   => MatrixSlotKind.Cursor,
        Mz800MatrixReference.SlotKind.Enter    => MatrixSlotKind.Enter,
        Mz800MatrixReference.SlotKind.Space    => MatrixSlotKind.Space,
        Mz800MatrixReference.SlotKind.Unused   => MatrixSlotKind.Unused,
        Mz800MatrixReference.SlotKind.Blank    => MatrixSlotKind.Blank,
        _                                      => MatrixSlotKind.Unknown,
    };

    /// <summary>
    /// Wraps <see cref="Mz800CharMapOverrides"/> in the machine-agnostic
    /// <see cref="ICharMapOverridesView"/> shape.
    /// </summary>
    private sealed class Adapter : ICharMapOverridesView
    {
        private readonly Mz800CharMapOverrides _inner;
        public Adapter(Mz800CharMapOverrides inner) => _inner = inner;

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
            _inner.Set(c, new Mz800CharMap.Press(press.Row, press.Col, press.MzShift));

        public void Remove(char c) => _inner.Remove(c);
        public void Suppress(char c) => _inner.Suppress(c);
        public void Unsuppress(char c) => _inner.Unsuppress(c);
        public bool IsSuppressed(char c) => _inner.IsSuppressed(c);

        public IEnumerable<KeyValuePair<char, MatrixPress>> All =>
            _inner.All.Select(kv => new KeyValuePair<char, MatrixPress>(
                kv.Key, new MatrixPress(kv.Value.Strobe, kv.Value.Bit, kv.Value.MzShift)));
    }
}
