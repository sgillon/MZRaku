using System;
using System.Drawing;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Shared helpers for concrete <see cref="IPhysicalKeyboardLayout"/>
/// adapters. Both Mz700PhysicalKeyboardLayout and
/// Mz80aPhysicalKeyboardLayout used to carry byte-identical copies of
/// these two methods; any future KeyKind addition or palette-tint
/// tweak had to land in both. Keeping them here means one source of
/// truth per operation.
/// </summary>
internal static class PhysicalKeyboardLayoutHelpers
{
    /// <summary>
    /// Canonical <see cref="MzKeyboardLayout.KeyKind"/> →
    /// <see cref="PhysicalKeyKind"/> mapping. MZ-700 and MZ-80A both
    /// derive their layouts from the shared MZ-700 KeyKind enum
    /// (Mz80aKeyboardLayout deliberately reuses it for exactly this
    /// symmetry), so one switch expression serves both machines.
    /// A machine that doesn't emit a given kind simply never passes
    /// it here.
    /// </summary>
    public static PhysicalKeyKind MapKind(MzKeyboardLayout.KeyKind k) => k switch
    {
        MzKeyboardLayout.KeyKind.Character => PhysicalKeyKind.Character,
        MzKeyboardLayout.KeyKind.Modifier  => PhysicalKeyKind.Modifier,
        MzKeyboardLayout.KeyKind.Mode      => PhysicalKeyKind.Mode,
        MzKeyboardLayout.KeyKind.Function  => PhysicalKeyKind.Function,
        MzKeyboardLayout.KeyKind.Cursor    => PhysicalKeyKind.Cursor,
        MzKeyboardLayout.KeyKind.Edit      => PhysicalKeyKind.Edit,
        MzKeyboardLayout.KeyKind.Enter     => PhysicalKeyKind.Enter,
        MzKeyboardLayout.KeyKind.Space     => PhysicalKeyKind.Space,
        MzKeyboardLayout.KeyKind.Blank     => PhysicalKeyKind.Blank,
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
