using System;
using System.Drawing;

namespace MZRaku;

/// <summary>
/// Shared helpers for concrete <see cref="IPhysicalKeyboardLayout"/>
/// adapters. Both Mz700PhysicalKeyboardLayout and
/// Mz80aPhysicalKeyboardLayout used to carry byte-identical copies of
/// these two methods; any future MzKeyKind addition or palette-tint
/// tweak had to land in both. Keeping them here means one source of
/// truth per operation.
/// </summary>
internal static class PhysicalKeyboardLayoutHelpers
{
    /// <summary>
    /// Canonical <see cref="MzKeyKind"/> →
    /// <see cref="PhysicalKeyKind"/> mapping. All three machines'
    /// layouts use the shared MzKeyKind enum, so one switch
    /// expression serves every machine.
    /// A machine that doesn't emit a given kind simply never passes
    /// it here.
    /// </summary>
    public static PhysicalKeyKind MapKind(MzKeyKind k) => k switch
    {
        MzKeyKind.Character => PhysicalKeyKind.Character,
        MzKeyKind.Modifier  => PhysicalKeyKind.Modifier,
        MzKeyKind.Mode      => PhysicalKeyKind.Mode,
        MzKeyKind.Function  => PhysicalKeyKind.Function,
        MzKeyKind.Cursor    => PhysicalKeyKind.Cursor,
        MzKeyKind.Edit      => PhysicalKeyKind.Edit,
        MzKeyKind.Enter     => PhysicalKeyKind.Enter,
        MzKeyKind.Space     => PhysicalKeyKind.Space,
        MzKeyKind.Blank     => PhysicalKeyKind.Blank,
        _                                  => PhysicalKeyKind.Character,
    };

    /// <summary>
    /// Adds <paramref name="delta"/> to each RGB channel, clamped to
    /// 0-255, preserving the source alpha. Positive delta lightens,
    /// negative darkens. Used by the hover-highlight tint.
    /// </summary>
    public static Color LightenOrDarken(Color c, int delta) =>
        Color.FromArgb(c.A,
            Math.Clamp(c.R + delta, 0, 255),
            Math.Clamp(c.G + delta, 0, 255),
            Math.Clamp(c.B + delta, 0, 255));
}
