using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-700 adapter for <see cref="IKeyboardEditorContext"/>. Wraps the
/// MZ-700 statics (<see cref="CharMap"/>, <see cref="SpecialKeyMap"/>,
/// <see cref="MzGlyphCatalog"/>, <see cref="MatrixCoverage"/>) and the
/// caller-supplied mutable override layers so the shared editor UI can
/// consume them through the interface. Introduced in v1.1 Phase 5.5a.
/// </summary>
public sealed class Mz700KeyboardEditorContext : IKeyboardEditorContext
{
    private readonly MZ700 _machine;
    private readonly CharMapOverrides _charOverrides;

    public Mz700KeyboardEditorContext(
        MZ700 machine,
        CharMapOverrides charOverrides,
        KeyOverride keyOverrides)
    {
        _machine = machine;
        _charOverrides = charOverrides;
        KeyOverrides = keyOverrides;
        CharOverrides = new Adapter(charOverrides);
        CharDefaults = CharMap.Defaults.ToDictionary(
            kv => kv.Key,
            kv => new MatrixPress(kv.Value.Row, kv.Value.Col, kv.Value.MzShift));
        SpecialKeyMap = SpecialKeyMap_Load();
    }

    public string MachineLabel => "MZ-700";

    public byte PeekMatrixRow(int strobe) => _machine.Keyboard.PeekMatrixRow(strobe);

    public IReadOnlyDictionary<char, MatrixPress> CharDefaults { get; }

    public ICharMapOverridesView CharOverrides { get; }

    public IReadOnlyDictionary<Keys, (int Row, int Col)> SpecialKeyMap { get; }

    public IReadOnlyDictionary<Keys, string> SpecialKeyLabels =>
        Hardware.SpecialKeyMap.Labels;

    public IReadOnlyDictionary<(int Row, int Col), string> SlotLabels =>
        Mz700MatrixReference.SpecialLabels;

    public KeyOverride KeyOverrides { get; }

    public (int Row, int Col) ShiftSlot => (8, 0);

    public IMatrixReference MatrixReference => Mz700MatrixReference.View;
    public IReadOnlyList<MzKeyboardLayout.MzKey> LayoutKeys => MzKeyboardLayout.Keys;
    public IEnumerable<MzKeyboardLayout.MzKey> EssentialLayoutKeys => MzKeyboardLayout.EssentialKeys;

    public char? FindGlyphAt(int row, int col, bool mzShift) =>
        MzGlyphCatalog.FindByPrintableSlot(row, col, mzShift);

    public string? FindSpecialLabelAt(int row, int col) =>
        MzGlyphCatalog.FindSpecialLabel(row, col);

    public IReadOnlyList<MatrixReferenceSlot> FindUnboundSlots()
    {
        var bound = CollectBoundSlots();
        var unbound = MatrixCoverage.FindUnbound(Mz700MatrixReference.View, bound);
        // MatrixReferenceCell doesn't carry Kind — look each cell's Kind
        // up in the native reference so the AdvancedKeyboardForm's
        // grouped rendering keeps working.
        return unbound.Select(c =>
        {
            var kind = Mz700MatrixReference.All[(c.Row, c.Col)].Kind;
            return new MatrixReferenceSlot(c.Row, c.Col, MapKind(kind), c.Id, c.UnshiftedGlyph, c.ShiftedGlyph);
        }).ToList();
    }

    private IEnumerable<(int Row, int Col)> CollectBoundSlots()
    {
        foreach (var kv in Hardware.SpecialKeyMap.Map)
            yield return (kv.Value.row, kv.Value.col);
        foreach (var kv in CharMap.Defaults)
        {
            // Suppressed defaults don't fire at runtime; treating them as
            // "bound" would hide a slot the user has deliberately
            // unwired via the slot editor.
            if (_charOverrides.IsSuppressed(kv.Key)) continue;
            yield return (kv.Value.Row, kv.Value.Col);
        }
        foreach (var kv in _charOverrides.All)
            yield return (kv.Value.Row, kv.Value.Col);
        foreach (var kv in KeyOverrides.All)
            yield return (kv.Value.Row, kv.Value.Col);
    }

    // SpecialKeyMap in Hardware is Dictionary<Keys,(int row,int col)>;
    // rewrap with Row/Col-cased tuple to match the interface's shape
    // (avoids leaking the lowercase-cased tuple through the abstraction).
    private static IReadOnlyDictionary<Keys, (int Row, int Col)> SpecialKeyMap_Load() =>
        Hardware.SpecialKeyMap.Map.ToDictionary(kv => kv.Key,
            kv => ((int Row, int Col))(kv.Value.row, kv.Value.col));

    private static MatrixSlotKind MapKind(Mz700MatrixReference.SlotKind k) => k switch
    {
        Mz700MatrixReference.SlotKind.Char     => MatrixSlotKind.Char,
        Mz700MatrixReference.SlotKind.Function => MatrixSlotKind.Function,
        Mz700MatrixReference.SlotKind.Modifier => MatrixSlotKind.Modifier,
        Mz700MatrixReference.SlotKind.Mode     => MatrixSlotKind.Mode,
        Mz700MatrixReference.SlotKind.Edit     => MatrixSlotKind.Edit,
        Mz700MatrixReference.SlotKind.Cursor   => MatrixSlotKind.Cursor,
        Mz700MatrixReference.SlotKind.Enter    => MatrixSlotKind.Enter,
        Mz700MatrixReference.SlotKind.Space    => MatrixSlotKind.Space,
        Mz700MatrixReference.SlotKind.Unused   => MatrixSlotKind.Unused,
        Mz700MatrixReference.SlotKind.Blank    => MatrixSlotKind.Blank,
        _                                      => MatrixSlotKind.Unknown,
    };

    /// <summary>
    /// Wraps <see cref="CharMapOverrides"/> in the machine-agnostic
    /// <see cref="ICharMapOverridesView"/> shape. Every read converts
    /// <see cref="CharMap.Press"/> → <see cref="MatrixPress"/>; every
    /// write converts back.
    /// </summary>
    private sealed class Adapter : ICharMapOverridesView
    {
        private readonly CharMapOverrides _inner;
        public Adapter(CharMapOverrides inner) => _inner = inner;

        public bool TryLookup(char c, out MatrixPress press)
        {
            if (_inner.TryLookup(c, out var p))
            {
                press = new MatrixPress(p.Row, p.Col, p.MzShift);
                return true;
            }
            press = default;
            return false;
        }

        public void Set(char c, MatrixPress press) =>
            _inner.Set(c, new CharMap.Press(press.Row, press.Col, press.MzShift));

        public void Remove(char c) => _inner.Remove(c);
        public void Suppress(char c) => _inner.Suppress(c);
        public void Unsuppress(char c) => _inner.Unsuppress(c);
        public bool IsSuppressed(char c) => _inner.IsSuppressed(c);

        public IEnumerable<KeyValuePair<char, MatrixPress>> All =>
            _inner.All.Select(kv => new KeyValuePair<char, MatrixPress>(
                kv.Key, new MatrixPress(kv.Value.Row, kv.Value.Col, kv.Value.MzShift)));
    }
}
