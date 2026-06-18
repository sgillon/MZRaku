using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MZ700Emul.Hardware;

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

    // Paths to the system files (monitor ROM, character font, BASIC
    // cassette image). Populated automatically on first run by scanning
    // the program directory; the user can edit settings.ini afterwards
    // to point them somewhere else.
    //
    // The raw *Path strings are written to disk verbatim — kept as a
    // path RELATIVE to the executable when the file lives at-or-below
    // it, otherwise as an absolute path. Use the *FullPath helpers
    // when actually opening the file.
    public string MonitorRomPath { get; set; } = "";
    public string FontPath { get; set; } = "";
    public string BasicPath { get; set; } = "";

    // PC gamepad button index (0..31, matching the WinMM dwButtons
    // bitmask) that drives each MZ-1X03 stick button. Defaults match
    // the original hardcoded behaviour: button 0 → MZ SW1, button 1
    // → MZ SW2. Both emulated sticks share the same mapping; if you
    // need per-slot mappings, this can be split into JoyStick1Button1
    // etc. later without breaking the existing keys.
    public int JoyButton1Index { get; set; } = 0;
    public int JoyButton2Index { get; set; } = 1;

    // User-editable physical-key overrides. Empty by default; built-in
    // defaults (Enter, arrows, GRAPH, ALPHA, ...) live in SpecialKeyMap.
    // Anything in here is consulted FIRST by Keyboard.OnKeyDown.
    public KeyOverride KeyOverrides { get; } = new();

    // User-editable character-map overrides. Empty by default; built-in
    // defaults (letters, digits, common punctuation) live in CharMap.
    // Anything in here is consulted FIRST by CharMap.TryLookup.
    public CharMapOverrides CharMapOverrides { get; } = new();

    // Persisted debugger / memory-viewer state. Values of (0,0,0,0)
    // mean "no saved geometry — fall back to the host's default
    // positioning on first open." Serialised to [DebuggerWindow] /
    // [MemoryViewerWindow] / [DebuggerBreakpoints] sections.
    public WindowState DebuggerWindow { get; set; } = new();
    public WindowState MemoryViewerWindow { get; set; } = new();
    public List<int> DebuggerBreakpoints { get; set; } = new();

    public record struct WindowState(int X, int Y, int Width, int Height)
    {
        public bool HasGeometry => Width > 0 && Height > 0;
    }

    public string MonitorRomFullPath => Resolve(MonitorRomPath);
    public string FontFullPath => Resolve(FontPath);
    public string BasicFullPath => Resolve(BasicPath);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        bool fileExisted = File.Exists(FilePath);
        bool missingSection = false;
        try
        {
            if (fileExisted)
            {
                var ini = ParseIni(File.ReadAllLines(FilePath));
                s.DisplayScale = GetInt(ini, "Display", "Scale", s.DisplayScale);
                s.MonitorRomPath = GetString(ini, "Roms", "Monitor", "");
                s.FontPath = GetString(ini, "Roms", "Font", "");
                s.BasicPath = GetString(ini, "Roms", "Basic", "");
                s.JoyButton1Index = GetInt(ini, "Joystick", "Button1", s.JoyButton1Index);
                s.JoyButton2Index = GetInt(ini, "Joystick", "Button2", s.JoyButton2Index);
                if (ini.TryGetValue("KeyOverrides", out var ko))
                {
                    foreach (var kv in ko) s.KeyOverrides.TryParseLine(kv.Key, kv.Value);
                }
                if (ini.TryGetValue("CharMap", out var cm))
                {
                    foreach (var kv in cm) s.CharMapOverrides.TryParseLine(kv.Key, kv.Value);
                }
                s.DebuggerWindow = ReadWindowState(ini, "DebuggerWindow");
                s.MemoryViewerWindow = ReadWindowState(ini, "MemoryViewerWindow");
                if (ini.TryGetValue("DebuggerBreakpoints", out var bps))
                {
                    foreach (var kv in bps)
                    {
                        if (int.TryParse(kv.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)
                            && a >= 0 && a <= 0xFFFF)
                            s.DebuggerBreakpoints.Add(a);
                    }
                    s.DebuggerBreakpoints.Sort();
                }
                // Older settings.ini files predate sections added in later
                // versions. Flag any missing section so Save() runs once
                // and the user gets a complete, editable file (now with the
                // retrofitted self-documenting comment blocks).
                if (!ini.ContainsKey("Joystick")) missingSection = true;
                if (!ini.ContainsKey("KeyOverrides")) missingSection = true;
                if (!ini.ContainsKey("CharMap")) missingSection = true;
                if (!ini.ContainsKey("DebuggerWindow")) missingSection = true;
                if (!ini.ContainsKey("MemoryViewerWindow")) missingSection = true;
                if (!ini.ContainsKey("DebuggerBreakpoints")) missingSection = true;
            }
        }
        catch { /* fall through to defaults */ }
        s.Sanitize();
        // Auto-detect any ROM paths that are empty or point at a missing
        // file. On the very first run this populates all three; on later
        // runs it self-heals if the user moved files around.
        bool dirty = s.EnsureRomPaths();
        if (dirty || !fileExisted || missingSection) s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; MZ700 Emulator settings — edit by hand if you like.");
            sb.AppendLine("; Every section's format is documented inline below; you shouldn't");
            sb.AppendLine("; need to consult anything else to understand or tweak this file.");
            sb.AppendLine();

            sb.AppendLine("[Display]");
            sb.AppendLine("; Window scale factor for the 320×200 MZ-700 framebuffer.");
            sb.AppendLine(";   Scale=1   native 320×200");
            sb.AppendLine(";   Scale=2   640×400 (default)");
            sb.AppendLine(";   Scale=3   960×600");
            sb.AppendLine($"Scale={DisplayScale}");
            sb.AppendLine();

            sb.AppendLine("[Roms]");
            sb.AppendLine("; Paths to the Sharp firmware files. Stored relative to MZ700Emul.exe");
            sb.AppendLine("; when the file lives at-or-below it (portable installs), absolute");
            sb.AppendLine("; otherwise. If a path goes stale (file moved or deleted) the next");
            sb.AppendLine("; launch re-scans the standard locations and patches the entry up.");
            sb.AppendLine(";   Monitor   1z-013a.rom   4 KiB monitor ROM");
            sb.AppendLine(";   Font      mz700fon.int  character generator ROM");
            sb.AppendLine(";   Basic     1Z-013B.mzf   S-BASIC cassette image");
            sb.AppendLine($"Monitor={MonitorRomPath}");
            sb.AppendLine($"Font={FontPath}");
            sb.AppendLine($"Basic={BasicPath}");
            sb.AppendLine();

            sb.AppendLine("[Joystick]");
            sb.AppendLine("; MZ-1X03 stick emulation driven by any Windows-recognised gamepad.");
            sb.AppendLine("; Both emulated sticks share the same button mapping.");
            sb.AppendLine(";   Button1   PC gamepad button index (0..31) that drives MZ SW1");
            sb.AppendLine(";   Button2   PC gamepad button index (0..31) that drives MZ SW2");
            sb.AppendLine("; Capture an index via Settings → Joystick → Capture… rather than guessing.");
            sb.AppendLine($"Button1={JoyButton1Index}");
            sb.AppendLine($"Button2={JoyButton2Index}");
            sb.AppendLine();

            sb.AppendLine("[KeyOverrides]");
            sb.AppendLine("; User physical-key bindings. Consulted ahead of the built-in");
            sb.AppendLine("; SpecialKeyMap defaults (Enter, cursors, GRAPH/ALPHA, F-keys, etc).");
            sb.AppendLine("; One line per binding:");
            sb.AppendLine(";   <KeyName>=<row>,<col>,<shift>");
            sb.AppendLine("; Where:");
            sb.AppendLine(";   <KeyName>  WinForms Keys enum value, e.g. F5, Tab, 'Control, G'.");
            sb.AppendLine(";   <row>      MZ-700 matrix row, 0-9.");
            sb.AppendLine(";   <col>      MZ-700 matrix column, 0-7.");
            sb.AppendLine(";   <shift>    MZ shift state to assert while the key is held:");
            sb.AppendLine(";                t = force MZ shift on");
            sb.AppendLine(";                f = force MZ shift off");
            sb.AppendLine(";                - = pass through whatever PC shift is currently held");
            foreach (var line in KeyOverrides.SerialiseLines()) sb.AppendLine(line);
            sb.AppendLine();

            sb.AppendLine("[CharMap]");
            sb.AppendLine("; User character-map overrides. Consulted ahead of the built-in");
            sb.AppendLine("; CharMap defaults (letters, digits, punctuation). Drives the");
            sb.AppendLine("; char-resolved keystroke path (after host-OS layout / AltGr).");
            sb.AppendLine("; One line per binding:");
            sb.AppendLine(";   <hex-codepoint>=<row>,<col>,<shift>   ; <glyph>");
            sb.AppendLine("; Or, to suppress a built-in default (so its PC char produces");
            sb.AppendLine("; nothing on the MZ side):");
            sb.AppendLine(";   <hex-codepoint>=-                     ; <glyph> (suppressed)");
            sb.AppendLine("; The slot editor writes a suppression entry automatically when you");
            sb.AppendLine("; bind a different PC char to an MZ slot that already had a default");
            sb.AppendLine("; — so the slot still has just one PC binding after Save.");
            sb.AppendLine("; Where:");
            sb.AppendLine(";   <hex-codepoint>  Unicode codepoint of the PC char in 4-digit hex");
            sb.AppendLine(";                    (e.g. 002A = '*'). Hex avoids breaking the INI");
            sb.AppendLine(";                    parser on chars like '=', ';', '#'.");
            sb.AppendLine(";   <row>            MZ-700 matrix row, 0-9.");
            sb.AppendLine(";   <col>            MZ-700 matrix column, 0-7.");
            sb.AppendLine(";   <shift>          MZ shift state to assert while the char fires:");
            sb.AppendLine(";                      t = force MZ shift on");
            sb.AppendLine(";                      f = force MZ shift off");
            sb.AppendLine(";                    (Pass-through doesn't apply here — by the time");
            sb.AppendLine(";                    we have a char, the OS has resolved the shift.)");
            sb.AppendLine(";   <glyph>          Free-text comment showing the literal character,");
            sb.AppendLine(";                    purely for hand-editing readability.");
            foreach (var line in CharMapOverrides.SerialiseLines()) sb.AppendLine(line);
            sb.AppendLine();

            sb.AppendLine("[DebuggerWindow]");
            sb.AppendLine("; Last on-screen position + size of the Debugger window (Ctrl+D).");
            sb.AppendLine("; Restored on next open so the layout follows the user across runs.");
            sb.AppendLine("; If a value is missing or zero the window falls back to its default");
            sb.AppendLine("; placement (just to the right of the main emulator window).");
            sb.AppendLine(";   X / Y       desktop pixel coordinates of the top-left corner");
            sb.AppendLine(";   Width / Height   client-area size in pixels");
            sb.AppendLine($"X={DebuggerWindow.X}");
            sb.AppendLine($"Y={DebuggerWindow.Y}");
            sb.AppendLine($"Width={DebuggerWindow.Width}");
            sb.AppendLine($"Height={DebuggerWindow.Height}");
            sb.AppendLine();

            sb.AppendLine("[MemoryViewerWindow]");
            sb.AppendLine("; Last on-screen position + size of the Memory Viewer (Ctrl+M).");
            sb.AppendLine("; Format matches [DebuggerWindow] above; defaults to just below the");
            sb.AppendLine("; main emulator window on first open.");
            sb.AppendLine($"X={MemoryViewerWindow.X}");
            sb.AppendLine($"Y={MemoryViewerWindow.Y}");
            sb.AppendLine($"Width={MemoryViewerWindow.Width}");
            sb.AppendLine($"Height={MemoryViewerWindow.Height}");
            sb.AppendLine();

            sb.AppendLine("[DebuggerBreakpoints]");
            sb.AppendLine("; Address-based breakpoints to re-arm in the Debugger on next launch.");
            sb.AppendLine("; One line per breakpoint, 4-digit hex Z80 address followed by '=1':");
            sb.AppendLine(";   <hex-addr>=1");
            sb.AppendLine("; Edit by hand to add or remove; the Debugger writes this section");
            sb.AppendLine("; back from the live list when it's closed.");
            foreach (var addr in DebuggerBreakpoints)
                sb.AppendLine($"{addr:X4}=1");

            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { /* non-fatal */ }
    }

    private void Sanitize()
    {
        if (DisplayScale < 1 || DisplayScale > 3) DisplayScale = 2;
        if (JoyButton1Index < 0 || JoyButton1Index > 31) JoyButton1Index = 0;
        if (JoyButton2Index < 0 || JoyButton2Index > 31) JoyButton2Index = 1;
    }

    /// <summary>
    /// Fills in any ROM/font/BASIC path that's empty or no longer points
    /// at an existing file by scanning the program directory and its
    /// <c>roms/</c> / <c>basic/</c> subdirectories (walking up the tree
    /// so dev-time runs from <c>bin/Debug/...</c> still find files at
    /// the source-tree root). Returns true if anything changed.
    /// </summary>
    private bool EnsureRomPaths()
    {
        bool dirty = false;
        dirty |= EnsureOne(
            MonitorRomFullPath, MonitorRomPath, v => MonitorRomPath = v,
            () => FindFile("1z-013a.rom", "roms", ""));
        dirty |= EnsureOne(
            FontFullPath, FontPath, v => FontPath = v,
            // Prefer the binary font over the hex text dump.
            () => FindFile("mz700fon.int", "roms", "") ?? FindFile("font_hex.txt", "roms", ""));
        dirty |= EnsureOne(
            BasicFullPath, BasicPath, v => BasicPath = v,
            // BASIC is conceptually another ROM image; scan the same
            // places, plus the legacy basic/ folder for back-compat.
            () => FindFile("1Z-013B.mzf", "roms", "basic", ""));
        return dirty;
    }

    /// <summary>
    /// Either auto-detects a missing path or normalizes an existing one
    /// to the relative-when-possible storage form. Returns true if the
    /// stored value changed.
    /// </summary>
    private static bool EnsureOne(string fullPath, string storedPath, Action<string> setStored, Func<string?> scan)
    {
        if (IsExistingFile(fullPath))
        {
            var normalized = MakeStorable(fullPath);
            if (normalized != storedPath) { setStored(normalized); return true; }
            return false;
        }
        var found = scan();
        if (found == null) return false;
        var storable = MakeStorable(found);
        if (storable == storedPath) return false;
        setStored(storable);
        return true;
    }

    private static bool IsExistingFile(string path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <summary>
    /// Converts a raw stored path (relative or absolute) into an
    /// absolute path. Relative paths are resolved against
    /// <see cref="AppContext.BaseDirectory"/>. Empty stays empty.
    /// </summary>
    private static string Resolve(string storedPath)
    {
        if (string.IsNullOrEmpty(storedPath)) return storedPath;
        if (Path.IsPathRooted(storedPath)) return storedPath;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, storedPath));
    }

    /// <summary>
    /// Picks the form to write to settings.ini for an absolute path:
    /// relative-to-base when the file lives under the executable
    /// directory (so the INI file stays portable), absolute otherwise.
    /// </summary>
    private static string MakeStorable(string absolutePath)
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(absolutePath);
        var prefix = baseDir + Path.DirectorySeparatorChar;
        if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return full.Substring(prefix.Length);
        return full;
    }

    /// <summary>
    /// Walks up the directory tree from <see cref="AppContext.BaseDirectory"/>
    /// looking for <paramref name="filename"/> inside any of the listed
    /// subdirectories at each level. Empty string in <paramref name="subdirs"/>
    /// means "the level itself".
    /// </summary>
    private static string? FindFile(string filename, params string[] subdirs)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            foreach (var sub in subdirs)
            {
                var candidate = string.IsNullOrEmpty(sub)
                    ? Path.Combine(dir, filename)
                    : Path.Combine(dir, sub, filename);
                if (File.Exists(candidate)) return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
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
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1);
            // Strip inline ';' comment from the value side (e.g. the
            // glyph comment on a [CharMap] line). Values in our format
            // never legitimately contain ';' — sections that need to
            // would have to escape, but none currently do.
            int comment = value.IndexOf(';');
            if (comment >= 0) value = value.Substring(0, comment);
            current[key] = value.Trim();
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

    private static string GetString(Dictionary<string, Dictionary<string, string>> ini, string section, string key, string fallback)
    {
        if (ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v))
            return v;
        return fallback;
    }

    private static WindowState ReadWindowState(Dictionary<string, Dictionary<string, string>> ini, string section)
    {
        int x = GetInt(ini, section, "X", 0);
        int y = GetInt(ini, section, "Y", 0);
        int w = GetInt(ini, section, "Width", 0);
        int h = GetInt(ini, section, "Height", 0);
        return new WindowState(x, y, w, h);
    }
}
