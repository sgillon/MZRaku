namespace MZRaku.Hardware;

/// <summary>
/// MZ-80A analogue of <see cref="CharMapOverrides"/>. Same shape
/// and INI serialisation (hex codepoint + coord,coord,shift), but
/// entries store <see cref="Mz80aCharMap.Press"/> — matrix
/// coordinates are the same shape (10 strobes × 8 bits) as MZ-700's
/// (10 rows × 8 cols), only the vocabulary differs. Persisted under
/// <c>[CharMap.MZ80A]</c> in settings.ini.
///
/// Suppression: as with the MZ-700 sibling, an entry of "-" marks
/// a default as suppressed so the runtime lookup acts as if the
/// default didn't exist. Used by the keyboard editor's slot-replace
/// flow.
///
/// v1.2 audit F-023 lifted the storage + serialisation into a
/// generic <see cref="MatrixOverrides{TPress}"/> shared with
/// <see cref="CharMapOverrides"/>.
/// </summary>
public sealed class Mz80aCharMapOverrides : MatrixOverrides<Mz80aCharMap.Press>
{
    public Mz80aCharMapOverrides() : base(
        makePress: (r, c, shift) => new Mz80aCharMap.Press(r, c, shift),
        readPress: p => (p.Strobe, p.Bit, p.MzShift))
    { }
}
