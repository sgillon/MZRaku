using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-80A adapter for <see cref="IPhysicalKeyboardLayout"/>. Wraps
/// <see cref="Mz80aKeyboardLayout"/>'s keycap table and derives glyph
/// / label lookups directly from <see cref="Mz80aMatrixReference"/>
/// (per 5.5b design decision Q1 — no separate glyph catalog needed;
/// the matrix reference already carries glyphs and IDs in an
/// adapter-friendly shape).
///
/// Cap colour palette is monochrome cream, matching the real MZ-80A.
/// A subtle tint separates modifier / mode / edit / enter / cursor
/// / space caps from character caps so the user can pick out the
/// non-typing keys at a glance without departing from the machine's
/// actual visual identity.
/// </summary>
public sealed class Mz80aPhysicalKeyboardLayout : IPhysicalKeyboardLayout
{
    public Mz80aPhysicalKeyboardLayout()
    {
        Keys = Mz80aKeyboardLayout.Keys
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

    public float Width => Mz80aKeyboardLayout.Width;
    public float Height => Mz80aKeyboardLayout.Height;
    public IReadOnlyList<PhysicalKey> Keys { get; }

    public char? FindGlyphAt(int row, int col, bool mzShift) =>
        Mz80aMatrixReference.FindGlyph(row, col, mzShift);

    public string? FindSpecialLabelAt(int row, int col) =>
        Mz80aMatrixReference.FindSpecialLabel(row, col);

    // Cream monochrome palette matching real MZ-80A caps. Character
    // keys stay pure cream; modifier/mode/edit/enter/cursor/space
    // caps get a subtle grey tint so they read as functionally
    // distinct without breaking the machine's actual appearance.
    private static readonly Color CapCharacter = Color.FromArgb(240, 235, 225);
    private static readonly Color CapFunction  = Color.FromArgb(215, 210, 200);
    private static readonly Color GlyphDark    = Color.FromArgb(40, 40, 45);

    public (Color fill, Color border, Color text) ColorsForKind(PhysicalKeyKind kind, bool hovered)
    {
        Color fill = kind == PhysicalKeyKind.Character ? CapCharacter : CapFunction;
        if (hovered) fill = PhysicalKeyboardLayoutHelpers.LightenOrDarken(fill, -18);
        Color border = hovered ? Color.FromArgb(60, 60, 70) : Color.FromArgb(140, 140, 145);
        return (fill, border, GlyphDark);
    }
}
