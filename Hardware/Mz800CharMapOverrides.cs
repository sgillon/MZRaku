namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 analogue of <see cref="CharMapOverrides"/> /
/// <see cref="Mz80aCharMapOverrides"/>: user char → matrix-slot
/// overrides plus suppressed defaults, consulted ahead of
/// <see cref="Mz800CharMap.Defaults"/>. Same INI wire format; persisted
/// under <c>[CharMap.MZ800]</c> in settings.ini.
/// </summary>
public sealed class Mz800CharMapOverrides : MatrixOverrides<Mz800CharMap.Press>
{
    public Mz800CharMapOverrides() : base(
        makePress: (r, c, shift) => new Mz800CharMap.Press(r, c, shift),
        readPress: p => (p.Strobe, p.Bit, p.MzShift))
    { }
}
