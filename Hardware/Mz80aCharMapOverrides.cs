using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-80A analogue of <see cref="CharMapOverrides"/>. Same shape and INI
/// serialisation (hex codepoint + strobe,bit,shift), but keys map into
/// <see cref="Mz80aCharMap.Press"/>. Persisted under <c>[CharMap.MZ80A]</c>
/// in settings.ini.
///
/// Suppression: as with the MZ-700 sibling, an entry of "-" marks a
/// default as suppressed so the runtime lookup acts as if the default
/// didn't exist. Used by the keyboard editor's slot-replace flow.
/// </summary>
public sealed class Mz80aCharMapOverrides
{
    private readonly Dictionary<char, Mz80aCharMap.Press> _map = new();
    private readonly HashSet<char> _suppressed = new();

    public bool TryLookup(char c, out Mz80aCharMap.Press press) =>
        _map.TryGetValue(c, out press);

    public void Set(char c, Mz80aCharMap.Press press)
    {
        _map[c] = press;
        _suppressed.Remove(c);
    }

    public void Remove(char c) => _map.Remove(c);
    public void Clear() { _map.Clear(); _suppressed.Clear(); }
    public int Count => _map.Count;

    public IEnumerable<KeyValuePair<char, Mz80aCharMap.Press>> All => _map;

    public void Suppress(char c) => _suppressed.Add(c);
    public void Unsuppress(char c) => _suppressed.Remove(c);
    public bool IsSuppressed(char c) => _suppressed.Contains(c);
    public IEnumerable<char> AllSuppressed => _suppressed;

    public IEnumerable<string> SerialiseLines()
    {
        var positives = _map.Select(kv =>
            (codepoint: (int)kv.Key,
             line: $"{(int)kv.Key:X4}={kv.Value.Strobe},{kv.Value.Bit},{ShiftChar(kv.Value.MzShift)}{GlyphComment(kv.Key)}"));
        var suppressed = _suppressed.Select(c =>
            (codepoint: (int)c,
             line: $"{(int)c:X4}=-{GlyphComment(c)}{(GlyphComment(c).Length > 0 ? " (suppressed)" : "   ; (suppressed)")}"));
        return positives.Concat(suppressed).OrderBy(t => t.codepoint).Select(t => t.line);
    }

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
        if (!int.TryParse(parts[0], out var strobe)) return false;
        if (!int.TryParse(parts[1], out var bit)) return false;
        if (strobe < 0 || strobe > 9 || bit < 0 || bit > 7) return false;
        bool shift = parts[2].Trim() switch
        {
            "t" or "T" => true,
            _ => false,
        };
        _map[(char)codepoint] = new Mz80aCharMap.Press(strobe, bit, shift);
        return true;
    }

    private static string ShiftChar(bool s) => s ? "t" : "f";

    private static string GlyphComment(char c) =>
        c >= 0x20 && c <= 0x7E ? $"   ; '{c}'" : "";
}
