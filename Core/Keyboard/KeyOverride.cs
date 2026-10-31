using System.Collections.Generic;
using System.Linq;

namespace MZRaku.Core;

/// <summary>
/// User-editable override layer that maps a PC virtual key (optionally
/// combined with modifier flags) to a specific matrix position. Each
/// machine has its own store. Consulted ahead of the machine's
/// special-key map and character map in its keyboard's
/// <c>OnKeyDown</c> (e.g. <see cref="Mz700Keyboard.OnKeyDown"/> on the
/// MZ-700), so a user can rebind any key without touching the
/// built-in defaults.
///
/// Lookup priority inside this layer alone: combined-modifier match wins
/// over a bare-VK match, so e.g. an entry for <c>Control+G</c> beats an
/// entry for plain <c>G</c> when both are present.
///
/// Persisted to the <c>[KeyOverrides.MZ700]</c>,
/// <c>[KeyOverrides.MZ80A]</c> and <c>[KeyOverrides.MZ800]</c> sections
/// of <c>settings.ini</c> via <see cref="SerialiseLines"/> /
/// <see cref="TryParseLine"/>.
/// </summary>
public sealed class KeyOverride
{
    /// <param name="Row">Matrix row (MZ-700) or strobe (MZ-80A, MZ-800).</param>
    /// <param name="Col">Matrix column (MZ-700) or bit (MZ-80A, MZ-800), 0-7.</param>
    /// <param name="MzShift">
    /// true  → force the machine's SHIFT SET while held.
    /// false → force the machine's SHIFT CLEAR while held.
    /// null  → pass through the user's actual PC shift state.
    /// </param>
    public readonly record struct Binding(int Row, int Col, bool? MzShift);

    private readonly Dictionary<HostKey, Binding> _map = new();

    public bool TryLookup(HostKey keys, out Binding b) => _map.TryGetValue(keys, out b);

    public void Set(HostKey keys, Binding b) => _map[keys] = b;
    public void Remove(HostKey keys) => _map.Remove(keys);
    public void Clear() => _map.Clear();
    public int Count => _map.Count;

    public IEnumerable<KeyValuePair<HostKey, Binding>> All => _map;

    /// <summary>
    /// Two-stage lookup for use from <see cref="Mz700Keyboard.OnKeyDown"/>:
    /// the combined-modifier form wins, falling back to the bare VK
    /// (modifier flags stripped). Returns null if neither matches.
    /// </summary>
    public Binding? Resolve(HostKey keyData)
    {
        if (_map.TryGetValue(keyData, out var b)) return b;
        var bare = keyData & HostKey.KeyCode;
        if (bare != keyData && _map.TryGetValue(bare, out b)) return b;
        return null;
    }

    // ---- INI serialisation -------------------------------------------------

    /// <summary>
    /// Serialise each binding as <c>KeyName=Row,Col,Shift</c> where Shift
    /// is one of <c>t</c> (forced on), <c>f</c> (forced off), <c>-</c>
    /// (pass through PC shift). KeyName is the <see cref="HostKey"/>
    /// ToString output, the same as WinForms Keys gives (e.g. <c>F5</c>,
    /// <c>Control, G</c>).
    /// </summary>
    public IEnumerable<string> SerialiseLines() =>
        _map.OrderBy(kv => kv.Key.ToString())
            .Select(kv => $"{kv.Key}={kv.Value.Row},{kv.Value.Col},{ShiftChar(kv.Value.MzShift)}");

    /// <summary>
    /// Parses one INI line (key=value, comments / blanks already stripped
    /// by the caller). Returns true on success and updates the map; false
    /// if the line can't be decoded (silently skipped — INI is forgiving).
    /// </summary>
    public bool TryParseLine(string keyName, string value)
    {
        if (!System.Enum.TryParse<HostKey>(keyName, ignoreCase: true, out var keys)) return false;
        var parts = value.Split(',');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var row)) return false;
        if (!int.TryParse(parts[1], out var col)) return false;
        if (row < 0 || row > 9 || col < 0 || col > 7) return false;
        bool? shift = parts[2].Trim() switch
        {
            "t" or "T" => true,
            "f" or "F" => false,
            "-" or ""  => null,
            _ => (bool?)null,
        };
        _map[keys] = new Binding(row, col, shift);
        return true;
    }

    private static string ShiftChar(bool? s) => s switch
    {
        true => "t",
        false => "f",
        _ => "-",
    };
}
