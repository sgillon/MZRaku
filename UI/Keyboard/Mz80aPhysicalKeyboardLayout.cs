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
                Kind: MapKind(k.Kind),
                FixedLabel: k.FixedLabel,
                UnshiftedLabel: k.UnshiftedLabel,
                ShiftedLabel: k.ShiftedLabel))
            .ToList();
    }

    public float Width => Mz80aKeyboardLayout.Width;
    public float Height => Mz80aKeyboardLayout.Height;
    public IReadOnlyList<PhysicalKey> Keys { get; }

    /// <summary>
    /// Look up the printable glyph at (row, col, mzShift) via
    /// <see cref="Mz80aMatrixReference"/>. Returns the first character
    /// of the slot's UnshiftedGlyph / ShiftedGlyph (both are stored as
    /// strings in the reference for cases like "00" on the NP_00 slot;
    /// the diagram's per-cell rendering only ever wants one char).
    /// </summary>
    public char? FindGlyphAt(int row, int col, bool mzShift)
    {
        if (!Mz80aMatrixReference.All.TryGetValue((row, col), out var slot)) return null;
        var g = mzShift ? slot.ShiftedGlyph : slot.UnshiftedGlyph;
        return string.IsNullOrEmpty(g) ? null : g![0];
    }

    /// <summary>
    /// Friendly label for a non-printable slot on MZ-80A. Derived from
    /// the reference's <see cref="Mz80aMatrixReference.Slot.Id"/> for
    /// slots whose Kind isn't Char / Unused / Unknown. Char slots
    /// return null so the diagram falls back to the glyph render.
    /// </summary>
    public string? FindSpecialLabelAt(int row, int col)
    {
        if (!Mz80aMatrixReference.All.TryGetValue((row, col), out var slot)) return null;
        return slot.Kind switch
        {
            Mz80aMatrixReference.SlotKind.Char    => null,
            Mz80aMatrixReference.SlotKind.Unused  => null,
            Mz80aMatrixReference.SlotKind.Unknown => null,
            _                                     => slot.Id,
        };
    }

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
        if (hovered) fill = LightenOrDarken(fill, -18);
        Color border = hovered ? Color.FromArgb(60, 60, 70) : Color.FromArgb(140, 140, 145);
        return (fill, border, GlyphDark);
    }

    // MZ-80A has no Function or Blank kinds — those slots simply
    // aren't emitted by Mz80aKeyboardLayout. The switch still handles
    // them for enum completeness; if a future layout adds one, the
    // fallback keeps the diagram rendering rather than blowing up.
    private static PhysicalKeyKind MapKind(MzKeyboardLayout.KeyKind k) => k switch
    {
        MzKeyboardLayout.KeyKind.Character => PhysicalKeyKind.Character,
        MzKeyboardLayout.KeyKind.Modifier  => PhysicalKeyKind.Modifier,
        MzKeyboardLayout.KeyKind.Mode      => PhysicalKeyKind.Mode,
        MzKeyboardLayout.KeyKind.Cursor    => PhysicalKeyKind.Cursor,
        MzKeyboardLayout.KeyKind.Edit      => PhysicalKeyKind.Edit,
        MzKeyboardLayout.KeyKind.Enter     => PhysicalKeyKind.Enter,
        MzKeyboardLayout.KeyKind.Space     => PhysicalKeyKind.Space,
        MzKeyboardLayout.KeyKind.Function  => PhysicalKeyKind.Function,
        MzKeyboardLayout.KeyKind.Blank     => PhysicalKeyKind.Blank,
        _                                  => PhysicalKeyKind.Character,
    };

    private static Color LightenOrDarken(Color c, int delta)
    {
        int r = System.Math.Clamp(c.R + delta, 0, 255);
        int gC = System.Math.Clamp(c.G + delta, 0, 255);
        int b = System.Math.Clamp(c.B + delta, 0, 255);
        return Color.FromArgb(c.A, r, gC, b);
    }
}
