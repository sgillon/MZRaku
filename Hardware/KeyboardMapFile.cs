using System;
using System.Collections.Generic;
using System.IO;

namespace MZRaku.Hardware;

/// <summary>
/// Save / load helpers for the <c>.mzkbd</c> keyboard-mapping
/// exchange file. Two sections (<c>[CharMap]</c> and
/// <c>[KeyOverrides]</c>) using the same line formats
/// <see cref="CharMapOverrides.SerialiseLines"/> /
/// <see cref="Mz80aCharMapOverrides.SerialiseLines"/> /
/// <see cref="KeyOverride.SerialiseLines"/> already write into
/// <c>settings.ini</c>, plus a header comment block documenting the
/// shape for hand-editors.
///
/// v2 file format (v1.2 audit F-037): adds a <c>[Meta]</c> section
/// with <c>machine=MZ-700</c> / <c>machine=MZ-80A</c> at the top so
/// import can route the entries into the right machine's override
/// store. v1 files (no <c>[Meta]</c>) still load — they're treated
/// as MZ-700, matching the historical assumption. New saves are
/// always v2.
///
/// Only user overrides are persisted — built-in defaults are applied
/// at runtime and don't need to round-trip through the file. Import
/// offers <i>merge</i> (apply on top of current overrides) and
/// <i>replace</i> (clear current first); the caller drives that
/// prompt.
/// </summary>
public static class KeyboardMapFile
{
    public const string FileExtension = ".mzkbd";
    public const string FileFilter =
        "MZ keyboard mapping (*.mzkbd)|*.mzkbd|All files|*.*";

    /// <summary>
    /// Writes the machine-tagged file at <paramref name="path"/>.
    /// <paramref name="charSerialisedLines"/> feeds the [CharMap]
    /// section (MZ-700 CharMapOverrides.SerialiseLines() or MZ-80A
    /// Mz80aCharMapOverrides.SerialiseLines() — same wire format
    /// both sides). <paramref name="keyOverrides"/> is the shared
    /// KeyOverride type. Built-in defaults are never included.
    /// </summary>
    public static void Save(
        string path,
        MachineType machine,
        IEnumerable<string> charSerialisedLines,
        KeyOverride keyOverrides)
    {
        string machineTag = machine == MachineType.MZ80A ? "MZ-80A" : "MZ-700";
        using var w = new StreamWriter(path);
        w.WriteLine($"; {machineTag} Keyboard mapping file (v.mzkbd v2)");
        w.WriteLine($"; Saved {DateTime.Now:yyyy-MM-dd HH:mm:ss} by MZRaku.");
        w.WriteLine(";");
        w.WriteLine("; Contains user-customised PC-keyboard bindings only — the");
        w.WriteLine("; emulator's built-in defaults are still applied and don't");
        w.WriteLine("; need to be listed here. Load via Settings -> Keyboard ->");
        w.WriteLine("; Import...");
        w.WriteLine();
        w.WriteLine("[Meta]");
        w.WriteLine("; machine = MZ-700 or MZ-80A. Absent = MZ-700 (v1 file).");
        w.WriteLine($"machine={machineTag}");
        w.WriteLine();
        w.WriteLine("[CharMap]");
        w.WriteLine("; PC character (4-digit hex Unicode codepoint) = Row,Col,Shift");
        w.WriteLine("; Shift: t = MZ shift forced ON, f = forced OFF");
        foreach (var line in charSerialisedLines)
            w.WriteLine(line);
        w.WriteLine();
        w.WriteLine("[KeyOverrides]");
        w.WriteLine("; PC virtual key (with modifiers) = Row,Col,Shift");
        w.WriteLine("; Shift: t = forced ON, f = forced OFF, - = pass-through PC shift");
        foreach (var line in keyOverrides.SerialiseLines())
            w.WriteLine(line);
    }

    /// <summary>
    /// The parts a caller needs after Load(): which machine the file
    /// is for, plus the raw (key, value) pairs from each section so
    /// the caller can feed them into the right override store's
    /// TryParseLine.
    /// </summary>
    public sealed class LoadResult
    {
        public MachineType Machine { get; init; }
        public IReadOnlyList<(string Key, string Value)> CharEntries { get; init; }
            = System.Array.Empty<(string, string)>();
        public IReadOnlyList<(string Key, string Value)> KeyEntries { get; init; }
            = System.Array.Empty<(string, string)>();
    }

    /// <summary>
    /// Parses <paramref name="path"/>. Returns the machine tag (v1
    /// files default to MZ-700) plus the raw section entries. The
    /// caller applies them to the appropriate machine's override
    /// store via TryParseLine — this keeps KeyboardMapFile from
    /// needing to know about either concrete CharMap type.
    /// </summary>
    public static LoadResult Load(string path)
    {
        var chars = new List<(string, string)>();
        var vks = new List<(string, string)>();
        var machine = MachineType.MZ700;   // v1-file default

        string? section = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw;
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line.Substring(0, comment);
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            switch (section)
            {
                case "Meta":
                    if (key.Equals("machine", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("MZ-80A", StringComparison.OrdinalIgnoreCase)
                            || val.Equals("MZ80A", StringComparison.OrdinalIgnoreCase))
                            machine = MachineType.MZ80A;
                        else
                            machine = MachineType.MZ700;
                    }
                    break;
                case "CharMap":      chars.Add((key, val)); break;
                case "KeyOverrides": vks.Add((key, val));   break;
            }
        }
        return new LoadResult
        {
            Machine = machine,
            CharEntries = chars,
            KeyEntries = vks,
        };
    }
}
