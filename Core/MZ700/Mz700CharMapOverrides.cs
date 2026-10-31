namespace MZRaku.Core;

/// <summary>
/// User-editable override layer for the character-driven MZ-700
/// keyboard map. Consulted ahead of <see cref="Mz700CharMap.Defaults"/>
/// in <see cref="Mz700CharMap.TryLookup"/>, so a user can rebind any PC
/// character (the Unicode char a host keystroke produces) to a
/// different MZ-700 matrix slot without touching the built-in
/// defaults.
///
/// Persisted to the <c>[CharMap]</c> section of <c>settings.ini</c>
/// via the base class's SerialiseLines / TryParseLine. Mirrors
/// <see cref="KeyOverride"/>'s shape, keyed by char rather than VK.
///
/// v1.2 audit F-023 lifted the storage + serialisation into a
/// generic <see cref="MatrixOverrides{TPress}"/> so this class and
/// <see cref="Mz80aCharMapOverrides"/> stopped carrying two copies
/// of the same 130 lines.
/// </summary>
public sealed class Mz700CharMapOverrides : MatrixOverrides<Mz700CharMap.Press>
{
    public Mz700CharMapOverrides() : base(
        makePress: (r, c, shift) => new Mz700CharMap.Press(r, c, shift),
        readPress: p => (p.Row, p.Col, p.MzShift))
    { }
}
