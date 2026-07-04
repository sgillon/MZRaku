namespace MZRaku.Hardware;

/// <summary>
/// The minimum surface Ppi8255 needs to feed keyboard row data back
/// on Port B reads. The MZ-700's rich <see cref="Keyboard"/> class
/// implements this (its full public API is far bigger, spanning
/// auto-typer, CharMap-driven typing, staged shift bits, ROM-scan
/// diagnostics), and the leaner <see cref="Mz80aKeyboard"/> also
/// implements it. Both machines strobe rows 0-9 the same way at the
/// 8255 layer, so the interface stays trivial.
/// </summary>
public interface IKeyboardMatrix
{
    /// <summary>
    /// Return the 8-bit row-data value the PPI Port B would read
    /// when Port A strobes <paramref name="strobe"/> (0-9). Bit N
    /// clear = column N pressed (active-low). Values 10+ return
    /// $FF (nothing pressed).
    /// </summary>
    byte ReadRow(int strobe);
}
