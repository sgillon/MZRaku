using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MZRaku.Hardware;

/// <summary>
/// Generic base for the two user-override stores
/// (<see cref="CharMapOverrides"/> and
/// <see cref="Mz80aCharMapOverrides"/>): character → matrix-slot
/// map, plus a suppression set marking built-in defaults the
/// runtime should ignore. Persisted to <c>settings.ini</c> via
/// <see cref="SerialiseLines"/> / <see cref="TryParseLine"/>.
///
/// <typeparamref name="TPress"/> is the machine's native press
/// record (<see cref="CharMap.Press"/> = Row/Col/MzShift for MZ-700,
/// <see cref="Mz80aCharMap.Press"/> = Strobe/Bit/MzShift for MZ-80A).
/// Two adapter delegates handed to the base at construction bridge
/// between TPress and the shared internal <c>(int, int, bool)</c>
/// shape so serialisation, parsing, and lookups don't have to know
/// which machine they're serving.
///
/// The INI wire format is identical on both machines:
/// <c>HHHH=X,Y,shift</c> where HHHH is the 4-digit hex Unicode
/// codepoint, X/Y are 0-9 / 0-7, shift is <c>t</c> / <c>f</c>.
/// A value of <c>-</c> marks the codepoint as suppressed. Only the
/// hand-editor comment differs by machine (Row/Col vs Strobe/Bit
/// vocabulary) — that's cosmetic; files round-trip cross-machine at
/// the byte level.
///
/// Shift state is a definite <c>bool</c> here (no pass-through
/// tri-state like <see cref="KeyOverride"/>) because by the time a
/// host keystroke has produced a Unicode char, the OS has already
/// resolved the modifier.
///
/// Suppression: in addition to positive overrides, this layer
/// carries a set of PC chars whose corresponding built-in default
/// entry should be ignored (the runtime lookup acts as if the
/// default didn't exist). Used by the slot editor so that binding
/// PC 'a' to the MZ '1' slot also clears the original '1'-to-(1,0)
/// default — otherwise both PC keys would continue to drive the
/// same MZ slot.
/// </summary>
public abstract class MatrixOverrides<TPress> where TPress : struct
{
    private readonly Dictionary<char, TPress> _map = new();
    private readonly HashSet<char> _suppressed = new();
    private readonly Func<int, int, bool, TPress> _makePress;
    private readonly Func<TPress, (int R, int C, bool Shift)> _readPress;

    protected MatrixOverrides(
        Func<int, int, bool, TPress> makePress,
        Func<TPress, (int R, int C, bool Shift)> readPress)
    {
        _makePress = makePress;
        _readPress = readPress;
    }

    public bool TryLookup(char c, out TPress press) => _map.TryGetValue(c, out press);

    /// <summary>
    /// Setting a positive override for a char also clears any prior
    /// suppression for it — the slot editor relies on this so
    /// rebinding a previously-suppressed PC char "wakes it up"
    /// automatically.
    /// </summary>
    public void Set(char c, TPress press)
    {
        _map[c] = press;
        _suppressed.Remove(c);
    }

    public void Remove(char c) => _map.Remove(c);
    public void Clear() { _map.Clear(); _suppressed.Clear(); }
    public int Count => _map.Count;
    public IEnumerable<KeyValuePair<char, TPress>> All => _map;

    // ---- Suppression ------------------------------------------------------

    /// <summary>
    /// Mark a PC char so its built-in default entry is ignored by
    /// the runtime lookup. Has no effect if the char isn't in
    /// Defaults; harmless to call repeatedly.
    /// </summary>
    public void Suppress(char c) => _suppressed.Add(c);

    /// <summary>
    /// Restore a default entry by removing it from the suppression
    /// set. Idempotent.
    /// </summary>
    public void Unsuppress(char c) => _suppressed.Remove(c);

    public bool IsSuppressed(char c) => _suppressed.Contains(c);

    public IEnumerable<char> AllSuppressed => _suppressed;

    // ---- INI serialisation -------------------------------------------------

    /// <summary>
    /// Serialise each binding as <c>HHHH=X,Y,Shift   ; '&lt;glyph&gt;'</c>
    /// where HHHH is the 4-digit hex Unicode codepoint of the PC
    /// char (hex avoids breaking the INI parser on chars like
    /// <c>=</c>, <c>;</c>, <c>#</c>) and Shift is <c>t</c> (assert
    /// MZ shift) or <c>f</c> (clear it). The trailing comment shows
    /// the literal glyph when printable ASCII, purely for
    /// hand-editing readability.
    ///
    /// Suppressed defaults serialise as <c>HHHH=-   ; '&lt;glyph&gt;' (suppressed)</c>
    /// and merge into the same codepoint-sorted output stream so
    /// the section diffs cleanly.
    /// </summary>
    public IEnumerable<string> SerialiseLines()
    {
        var positives = _map.Select(kv =>
        {
            var (r, c, shift) = _readPress(kv.Value);
            return (codepoint: (int)kv.Key,
                    line: $"{(int)kv.Key:X4}={r},{c},{ShiftChar(shift)}{GlyphComment(kv.Key)}");
        });
        var suppressed = _suppressed.Select(c =>
            (codepoint: (int)c,
             line: $"{(int)c:X4}=-{GlyphComment(c)}{(GlyphComment(c).Length > 0 ? " (suppressed)" : "   ; (suppressed)")}"));
        return positives.Concat(suppressed).OrderBy(t => t.codepoint).Select(t => t.line);
    }

    /// <summary>
    /// Parses one INI line (key=value, the comment already stripped
    /// by the caller). Returns true on success and updates the map;
    /// false if the line can't be decoded (INI is forgiving —
    /// silent skip). A value of <c>-</c> is read as "suppress this
    /// default" — the codepoint is added to
    /// <see cref="AllSuppressed"/> instead of the positive map.
    /// </summary>
    public bool TryParseLine(string keyName, string value)
    {
        if (!int.TryParse(keyName, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codepoint)) return false;
        if (codepoint < 0 || codepoint > 0xFFFF) return false;
        var trimmed = value.Trim();
        if (trimmed == "-")
        {
            _suppressed.Add((char)codepoint);
            return true;
        }
        var parts = value.Split(',');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var r)) return false;
        if (!int.TryParse(parts[1], out var c)) return false;
        if (r < 0 || r > 9 || c < 0 || c > 7) return false;
        bool shift = parts[2].Trim() switch
        {
            "t" or "T" => true,
            _ => false,
        };
        _map[(char)codepoint] = _makePress(r, c, shift);
        return true;
    }

    private static string ShiftChar(bool s) => s ? "t" : "f";

    private static string GlyphComment(char c) =>
        c >= 0x20 && c <= 0x7E ? $"   ; '{c}'" : "";
}
