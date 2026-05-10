using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MZ700Emul;

/// <summary>
/// User preferences persisted across runs as a small INI file
/// (<c>settings.ini</c>) next to the executable. INI was picked over
/// JSON because it's trivially human-readable and editable when the
/// user wants to tweak something by hand.
///
/// To add a new setting: declare a property, read it from the parsed
/// dictionary in <see cref="Load"/>, and write it in <see cref="Save"/>.
/// </summary>
public sealed class Settings
{
    public int DisplayScale { get; set; } = 2;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            var ini = ParseIni(File.ReadAllLines(FilePath));
            s.DisplayScale = GetInt(ini, "Display", "Scale", s.DisplayScale);
        }
        catch { /* fall through to defaults */ }
        return s.Sanitize();
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; MZ700 Emulator settings — edit by hand if you like.");
            sb.AppendLine();
            sb.AppendLine("[Display]");
            sb.AppendLine($"Scale={DisplayScale}");
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { /* non-fatal */ }
    }

    private Settings Sanitize()
    {
        if (DisplayScale < 1 || DisplayScale > 3) DisplayScale = 2;
        return this;
    }

    // --- minimal INI parser: [Section] headers, key=value lines, ';' or '#' comments. ---
    private static Dictionary<string, Dictionary<string, string>> ParseIni(string[] lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        result[""] = current;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line.Substring(1, line.Length - 2).Trim();
                if (!result.TryGetValue(name, out current!))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[name] = current;
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return result;
    }

    private static int GetInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback)
    {
        if (ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) &&
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        return fallback;
    }
}
