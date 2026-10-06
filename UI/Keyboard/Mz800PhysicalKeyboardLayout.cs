using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-800 adapter for <see cref="IPhysicalKeyboardLayout"/>. Wraps
/// <see cref="Mz800KeyboardLayout"/>'s keycap table and reads glyphs /
/// labels from <see cref="Mz800MatrixReference"/>.
///
/// Palette: light grey character caps with darker grey special keys,
/// following the Owner's Manual p. 4-4 figure, which shades the special
/// keys (GRAPH, TAB, CTRL, SHIFT, ALPHA, BREAK, CR).
/// </summary>
public sealed class Mz800PhysicalKeyboardLayout : IPhysicalKeyboardLayout
{
    public Mz800PhysicalKeyboardLayout()
    {
        Keys = Mz800KeyboardLayout.Keys
            .Select(k => new PhysicalKey(
                Id: k.Id,
                Row: k.Row,
                Col: k.Col,
                X: k.X, Y: k.Y, W: k.W, H: k.H,
                Kind: PhysicalKeyboardLayoutHelpers.MapKind(k.Kind),
                FixedLabel: k.FixedLabel,
                UnshiftedLabel: k.UnshiftedLabel,
                ShiftedLabel: k.ShiftedLabel))
            .ToList();
    }

    public float Width => Mz800KeyboardLayout.Width;
    public float Height => Mz800KeyboardLayout.Height;
    public IReadOnlyList<PhysicalKey> Keys { get; }

    public char? FindGlyphAt(int row, int col, bool mzShift) =>
        Mz800MatrixReference.FindGlyph(row, col, mzShift);

    public string? FindSpecialLabelAt(int row, int col) =>
        Mz800MatrixReference.FindSpecialLabel(row, col);

    private static readonly Color CapLight   = Color.FromArgb(236, 236, 234);
    private static readonly Color CapDark    = Color.FromArgb(92, 96, 104);
    private static readonly Color GlyphDark  = Color.FromArgb(30, 30, 35);
    private static readonly Color GlyphLight = Color.FromArgb(245, 245, 245);

    public (Color fill, Color border, Color text) ColorsForKind(PhysicalKeyKind kind, bool hovered)
    {
        bool light = kind is PhysicalKeyKind.Character or PhysicalKeyKind.Space or PhysicalKeyKind.Blank;
        Color fill = light ? CapLight : CapDark;
        Color text = light ? GlyphDark : GlyphLight;
        if (hovered) fill = PhysicalKeyboardLayoutHelpers.LightenOrDarken(fill, -18);
        Color border = hovered ? Color.FromArgb(30, 30, 40) : Color.FromArgb(110, 110, 115);
        return (fill, border, text);
    }
}
