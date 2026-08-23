using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// MZ-700 adapter for <see cref="IPhysicalKeyboardLayout"/>. Wraps
/// <see cref="MzKeyboardLayout"/>'s static keycap table and supplies
/// the MZ-700-specific cap colour palette (white / amber / blue by
/// function group, per the real hardware). Introduced in v1.1 Phase
/// 5.5a — no user-visible behaviour change.
/// </summary>
public sealed class Mz700PhysicalKeyboardLayout : IPhysicalKeyboardLayout
{
    public Mz700PhysicalKeyboardLayout()
    {
        Keys = MzKeyboardLayout.Keys
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

    public float Width => MzKeyboardLayout.Width;
    public float Height => MzKeyboardLayout.Height;
    public IReadOnlyList<PhysicalKey> Keys { get; }

    public char? FindGlyphAt(int row, int col, bool mzShift) =>
        MzGlyphCatalog.FindByPrintableSlot(row, col, mzShift);

    public string? FindSpecialLabelAt(int row, int col) =>
        MzGlyphCatalog.FindSpecialLabel(row, col);

    // Real MZ-700 cap colours (preserved verbatim from the pre-Phase-5.5a
    // MzKeyboardDiagram.ColorsForKind):
    //   Blue  cap + white glyph: function keys.
    //   Amber cap + white glyph: mode / modifier / edit / enter / cursor / blank.
    //   White cap + black glyph: character keys + space.
    private static readonly Color CapBlue    = Color.FromArgb(38, 68, 144);
    private static readonly Color CapAmber   = Color.FromArgb(205, 120, 40);
    private static readonly Color CapWhite   = Color.FromArgb(252, 252, 250);
    private static readonly Color GlyphWhite = Color.FromArgb(252, 252, 250);
    private static readonly Color GlyphBlack = Color.FromArgb(20, 20, 20);

    public (Color fill, Color border, Color text) ColorsForKind(PhysicalKeyKind kind, bool hovered)
    {
        Color fill, text;
        switch (kind)
        {
            case PhysicalKeyKind.Function:
                fill = CapBlue;  text = GlyphWhite; break;
            case PhysicalKeyKind.Mode:
            case PhysicalKeyKind.Modifier:
            case PhysicalKeyKind.Edit:
            case PhysicalKeyKind.Enter:
            case PhysicalKeyKind.Cursor:
            case PhysicalKeyKind.Blank:
                fill = CapAmber; text = GlyphWhite; break;
            case PhysicalKeyKind.Character:
            case PhysicalKeyKind.Space:
            default:
                fill = CapWhite; text = GlyphBlack; break;
        }
        if (hovered) fill = PhysicalKeyboardLayoutHelpers.LightenOrDarken(fill, -18);
        Color border = hovered ? Color.FromArgb(30, 30, 40) : Color.FromArgb(110, 110, 115);
        return (fill, border, text);
    }
}
