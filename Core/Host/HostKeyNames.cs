using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace MZRaku.Core;

/// <summary>
/// Startup check that <see cref="HostKey"/>'s <c>ToString()</c> still
/// gives the key names MZRaku saves. Key overrides are written to
/// <c>settings.ini</c> and <c>.mzkbd</c> files by name (e.g. <c>Oem3</c>,
/// <c>Oem3, Shift</c>), and read back with <c>Enum.TryParse</c>, which
/// accepts every name a value has.
///
/// Sixteen values have more than one name (<c>Return</c> and
/// <c>Enter</c>, <c>Oemtilde</c> and <c>Oem3</c>, …). .NET documents
/// that <c>ToString()</c> may return any of them, so a future runtime
/// could change the saved spelling. <see cref="Expected"/> pins the name
/// each of those values has always formatted as (checked on .NET 8 and
/// .NET 10, 2026-11-21); <see cref="Validate"/> fails if the runtime
/// disagrees, or if a new shared value appears without a pin. Lives in
/// Core so every front end can run it, the same way as
/// <see cref="MatrixValidation"/>.
/// </summary>
public static class HostKeyNames
{
    /// <summary>
    /// The saved name of every <see cref="HostKey"/> value that has more
    /// than one name.
    /// </summary>
    public static readonly IReadOnlyDictionary<HostKey, string> Expected = new Dictionary<HostKey, string>
    {
        [HostKey.Enter]        = "Enter",         // also Return
        [HostKey.Capital]      = "Capital",       // also CapsLock
        [HostKey.KanaMode]     = "KanaMode",      // also HanguelMode, HangulMode
        [HostKey.KanjiMode]    = "KanjiMode",     // also HanjaMode
        [HostKey.IMEAceept]    = "IMEAceept",     // also IMEAccept
        [HostKey.PageUp]       = "PageUp",        // also Prior
        [HostKey.Next]         = "Next",          // also PageDown
        [HostKey.PrintScreen]  = "PrintScreen",   // also Snapshot
        [HostKey.OemSemicolon] = "OemSemicolon",  // also Oem1
        [HostKey.Oem2]         = "Oem2",          // also OemQuestion
        [HostKey.Oem3]         = "Oem3",          // also Oemtilde
        [HostKey.Oem4]         = "Oem4",          // also OemOpenBrackets
        [HostKey.OemPipe]      = "OemPipe",       // also Oem5
        [HostKey.Oem6]         = "Oem6",          // also OemCloseBrackets
        [HostKey.Oem7]         = "Oem7",          // also OemQuotes
        [HostKey.OemBackslash] = "OemBackslash",  // also Oem102
    };

    /// <summary>
    /// Returns one line per problem; empty when every pinned value (alone
    /// and with each combination of Shift, Control and Alt) formats as
    /// expected and every shared value is pinned.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();

        var shared = typeof(HostKey).GetFields(BindingFlags.Public | BindingFlags.Static)
            .GroupBy(f => (HostKey)f.GetValue(null)!)
            .Where(g => g.Count() > 1);
        foreach (var g in shared)
        {
            if (!Expected.ContainsKey(g.Key))
                complaints.Add($"0x{(int)g.Key:X} has names {string.Join(", ", g.Select(f => f.Name))} but no pinned saved name");
        }

        foreach (var (key, name) in Expected)
        {
            for (int mods = 0; mods < 8; mods++)
            {
                var value = key
                    | ((mods & 1) != 0 ? HostKey.Shift : 0)
                    | ((mods & 2) != 0 ? HostKey.Control : 0)
                    | ((mods & 4) != 0 ? HostKey.Alt : 0);
                string want = name
                    + ((mods & 1) != 0 ? ", Shift" : "")
                    + ((mods & 2) != 0 ? ", Control" : "")
                    + ((mods & 4) != 0 ? ", Alt" : "");
                string got = value.ToString();
                if (got != want)
                    complaints.Add($"0x{(int)value:X5} formats as \"{got}\", saved name is \"{want}\"");
            }
        }

        return complaints;
    }

    /// <summary>
    /// Runs <see cref="Validate"/>; silent when it passes, otherwise logs
    /// to debug output and calls <paramref name="warn"/>(text, title).
    /// </summary>
    public static void RunAndLog(Action<string, string> warn)
    {
        var complaints = Validate();
        if (complaints.Count == 0) return;
        Debug.WriteLine($"[HostKeyNames] {complaints.Count} complaint(s):");
        foreach (var c in complaints) Debug.WriteLine($"  {c}");
        warn(string.Join("\n", complaints),
             "Host key check — saved key names no longer match");
    }
}
