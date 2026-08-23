namespace MZRaku.Hardware;

/// <summary>
/// User-editable override layer for the character-driven MZ-700
/// keyboard map. Consulted ahead of <see cref="CharMap.Defaults"/>
/// in <see cref="CharMap.TryLookup"/>, so a user can rebind any PC
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
public sealed class CharMapOverrides : MatrixOverrides<CharMap.Press>
{
    public CharMapOverrides() : base(
        makePress: (r, c, shift) => new CharMap.Press(r, c, shift),
        readPress: p => (p.Row, p.Col, p.MzShift))
    { }
}
