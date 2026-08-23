using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Immutable snapshot of the user-visible settings state, taken before
/// the user has a chance to edit anything in <see cref="SettingsForm"/>.
/// Compared against a second snapshot at Apply time so the user gets a
/// human-readable summary of what's about to be persisted, rather than
/// having to remember exactly which knobs they touched.
///
/// Held as a flat record because the override layers are mutated live
/// while the dialog is open (the slot editor commits immediately into
/// the layer it was handed) — by the time Apply fires we can't recover
/// the pre-edit state from the live objects.
/// </summary>
internal sealed class SettingsSnapshot
{
    // Startup preferences (Phase 5.3). DefaultMachine is the persisted
    // boot machine; six DebugPanesAtStartup flags govern which debug
    // panes auto-open at boot.
    public MachineType DefaultMachine { get; init; } = MachineType.MZ700;
    public bool PaneDebugger { get; init; }
    public bool PaneMemoryViewer { get; init; }
    public bool PaneHidDiagnostic { get; init; }
    public bool PaneFontSheet { get; init; }
    public bool PaneSoundDiagnostic { get; init; }
    public bool PaneKeyboardMatrix { get; init; }

    public int DisplayScale { get; init; }
    public bool DisplayScanlines { get; init; }
    // MZ-80A-specific toggles (Phase 5.2). Green-screen tint retires
    // the View menu item; InvertLetterShift retires the INI-only
    // property.
    public bool Mz80aGreenScreen { get; init; }
    public bool Mz80aInvertLetterShift { get; init; }
    // Per-machine ROM paths (Phase 5.1c). Both machines' paths are
    // editable in the ROMs tab regardless of active machine, so the
    // diff has to cover both sets independently.
    public string Mz700MonitorPath { get; init; } = "";
    public string Mz700FontPath { get; init; } = "";
    public string Mz700BasicPath { get; init; } = "";
    public string Mz80aMonitorPath { get; init; } = "";
    public string Mz80aFontPath { get; init; } = "";
    public string Mz80aBasicPath { get; init; } = "";
    public int JoyButton1Index { get; init; }
    public int JoyButton2Index { get; init; }

    /// <summary>Frozen copy of <see cref="CharMapOverrides.All"/>.</summary>
    public IReadOnlyDictionary<char, CharMap.Press> CharOverrides { get; init; }
        = new Dictionary<char, CharMap.Press>();

    /// <summary>Frozen copy of <see cref="CharMapOverrides.AllSuppressed"/>.</summary>
    public IReadOnlyCollection<char> SuppressedChars { get; init; }
        = new HashSet<char>();

    /// <summary>Frozen copy of <see cref="KeyOverride.All"/>.</summary>
    public IReadOnlyDictionary<Keys, KeyOverride.Binding> KeyOverrides { get; init; }
        = new Dictionary<Keys, KeyOverride.Binding>();

    public static SettingsSnapshot Capture(Settings settings) => new()
    {
        DefaultMachine = settings.DefaultMachine,
        PaneDebugger = settings.DebugPanesAtStartup.Debugger,
        PaneMemoryViewer = settings.DebugPanesAtStartup.MemoryViewer,
        PaneHidDiagnostic = settings.DebugPanesAtStartup.HidDiagnostic,
        PaneFontSheet = settings.DebugPanesAtStartup.FontSheet,
        PaneSoundDiagnostic = settings.DebugPanesAtStartup.SoundDiagnostic,
        PaneKeyboardMatrix = settings.DebugPanesAtStartup.KeyboardMatrix,
        DisplayScale = settings.DisplayScale,
        DisplayScanlines = settings.DisplayScanlines,
        Mz80aGreenScreen = settings.Mz80aGreenScreen,
        Mz80aInvertLetterShift = settings.Mz80aInvertLetterShift,
        Mz700MonitorPath = settings.Mz700Roms.MonitorRomPath ?? "",
        Mz700FontPath = settings.Mz700Roms.FontPath ?? "",
        Mz700BasicPath = settings.Mz700Roms.BasicPath ?? "",
        Mz80aMonitorPath = settings.Mz80aRoms.MonitorRomPath ?? "",
        Mz80aFontPath = settings.Mz80aRoms.FontPath ?? "",
        Mz80aBasicPath = settings.Mz80aRoms.BasicPath ?? "",
        JoyButton1Index = settings.JoyButton1Index,
        JoyButton2Index = settings.JoyButton2Index,
        CharOverrides = settings.CharMapOverrides.All.ToDictionary(kv => kv.Key, kv => kv.Value),
        SuppressedChars = new HashSet<char>(settings.CharMapOverrides.AllSuppressed),
        KeyOverrides = settings.KeyOverrides.All.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>
    /// Build a candidate snapshot from the form controls (for scalars
    /// that haven't yet been pushed to <see cref="Settings"/>) plus the
    /// live overrides (which have been mutated live by the per-key
    /// editor flow). Mirrors <see cref="SettingsForm.ApplyChanges"/>'s
    /// own reads.
    /// </summary>
    public static SettingsSnapshot Build(
        MachineType defaultMachine,
        bool paneDebugger, bool paneMemoryViewer, bool paneHidDiagnostic,
        bool paneFontSheet, bool paneSoundDiagnostic, bool paneKeyboardMatrix,
        int displayScale,
        bool displayScanlines,
        bool mz80aGreenScreen,
        bool mz80aInvertLetterShift,
        string mz700Monitor, string mz700Font, string mz700Basic,
        string mz80aMonitor, string mz80aFont, string mz80aBasic,
        int joy1, int joy2,
        CharMapOverrides charOverrides,
        KeyOverride keyOverrides) => new()
        {
            DefaultMachine = defaultMachine,
            PaneDebugger = paneDebugger,
            PaneMemoryViewer = paneMemoryViewer,
            PaneHidDiagnostic = paneHidDiagnostic,
            PaneFontSheet = paneFontSheet,
            PaneSoundDiagnostic = paneSoundDiagnostic,
            PaneKeyboardMatrix = paneKeyboardMatrix,
            DisplayScale = displayScale,
            DisplayScanlines = displayScanlines,
            Mz80aGreenScreen = mz80aGreenScreen,
            Mz80aInvertLetterShift = mz80aInvertLetterShift,
            Mz700MonitorPath = mz700Monitor ?? "",
            Mz700FontPath = mz700Font ?? "",
            Mz700BasicPath = mz700Basic ?? "",
            Mz80aMonitorPath = mz80aMonitor ?? "",
            Mz80aFontPath = mz80aFont ?? "",
            Mz80aBasicPath = mz80aBasic ?? "",
            JoyButton1Index = joy1,
            JoyButton2Index = joy2,
            CharOverrides = charOverrides.All.ToDictionary(kv => kv.Key, kv => kv.Value),
            SuppressedChars = new HashSet<char>(charOverrides.AllSuppressed),
            KeyOverrides = keyOverrides.All.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
}

/// <summary>
/// Diff two <see cref="SettingsSnapshot"/>s into a list of human-readable
/// change lines, suitable for the Apply-time confirmation dialog. Each
/// line covers a single primitive change so the user can see — line by
/// line — exactly what's about to be saved.
///
/// Letter case-pairs collapse to a single line ("PC 'A/a' …"): the slot
/// editor binds both halves of a case-pair as a unit, and the user
/// thinks of them as one physical PC key.
/// </summary>
internal static class SettingsDiff
{
    public static IReadOnlyList<string> Describe(SettingsSnapshot before, SettingsSnapshot after)
    {
        var lines = new List<string>();

        if (before.DefaultMachine != after.DefaultMachine)
            lines.Add($"Default machine on startup: {before.DefaultMachine} → {after.DefaultMachine}");
        DescribePane(before.PaneDebugger, after.PaneDebugger, "Debugger", lines);
        DescribePane(before.PaneMemoryViewer, after.PaneMemoryViewer, "Memory Viewer", lines);
        DescribePane(before.PaneHidDiagnostic, after.PaneHidDiagnostic, "HID Diagnostic", lines);
        DescribePane(before.PaneFontSheet, after.PaneFontSheet, "Font Sheet", lines);
        DescribePane(before.PaneSoundDiagnostic, after.PaneSoundDiagnostic, "Sound Diagnostic", lines);
        DescribePane(before.PaneKeyboardMatrix, after.PaneKeyboardMatrix, "Keyboard Matrix", lines);

        if (before.DisplayScale != after.DisplayScale)
            lines.Add($"Display scale: {before.DisplayScale}× → {after.DisplayScale}×");
        if (before.DisplayScanlines != after.DisplayScanlines)
            lines.Add($"Scanlines: {(before.DisplayScanlines ? "on" : "off")} → {(after.DisplayScanlines ? "on" : "off")}");
        if (before.Mz80aGreenScreen != after.Mz80aGreenScreen)
            lines.Add($"MZ-80A green screen: {(before.Mz80aGreenScreen ? "on" : "off")} → {(after.Mz80aGreenScreen ? "on" : "off")}");
        if (before.Mz80aInvertLetterShift != after.Mz80aInvertLetterShift)
            lines.Add($"MZ-80A InvertLetterShift: {(before.Mz80aInvertLetterShift ? "on" : "off")} → {(after.Mz80aInvertLetterShift ? "on" : "off")}");
        if (before.Mz700MonitorPath != after.Mz700MonitorPath)
            lines.Add($"MZ-700 Monitor ROM: \"{before.Mz700MonitorPath}\" → \"{after.Mz700MonitorPath}\"");
        if (before.Mz700FontPath != after.Mz700FontPath)
            lines.Add($"MZ-700 Font ROM: \"{before.Mz700FontPath}\" → \"{after.Mz700FontPath}\"");
        if (before.Mz700BasicPath != after.Mz700BasicPath)
            lines.Add($"MZ-700 BASIC image: \"{before.Mz700BasicPath}\" → \"{after.Mz700BasicPath}\"");
        if (before.Mz80aMonitorPath != after.Mz80aMonitorPath)
            lines.Add($"MZ-80A Monitor ROM: \"{before.Mz80aMonitorPath}\" → \"{after.Mz80aMonitorPath}\"");
        if (before.Mz80aFontPath != after.Mz80aFontPath)
            lines.Add($"MZ-80A Font ROM: \"{before.Mz80aFontPath}\" → \"{after.Mz80aFontPath}\"");
        if (before.Mz80aBasicPath != after.Mz80aBasicPath)
            lines.Add($"MZ-80A BASIC image: \"{before.Mz80aBasicPath}\" → \"{after.Mz80aBasicPath}\"");
        if (before.JoyButton1Index != after.JoyButton1Index)
            lines.Add($"Joystick button 1: index {before.JoyButton1Index} → {after.JoyButton1Index}");
        if (before.JoyButton2Index != after.JoyButton2Index)
            lines.Add($"Joystick button 2: index {before.JoyButton2Index} → {after.JoyButton2Index}");

        lines.AddRange(DescribeCharOverrides(before, after));
        lines.AddRange(DescribeSuppressed(before, after));
        lines.AddRange(DescribeKeyOverrides(before, after));

        return lines;
    }

    private static void DescribePane(bool before, bool after, string paneName, List<string> lines)
    {
        if (before == after) return;
        lines.Add($"Open {paneName} at startup: {(before ? "on" : "off")} → {(after ? "on" : "off")}");
    }

    private static IEnumerable<string> DescribeCharOverrides(SettingsSnapshot before, SettingsSnapshot after)
    {
        // Coalesce letter case-pairs so binding PC 'a' (which auto-pairs
        // 'A') reads as one line, not two.
        var beforeKeys = new HashSet<char>(before.CharOverrides.Keys);
        var afterKeys = new HashSet<char>(after.CharOverrides.Keys);
        var emittedPairs = new HashSet<char>();

        var allKeys = beforeKeys.Union(afterKeys).OrderBy(c => CanonicalSortKey(c)).ThenBy(c => (int)c);

        foreach (var c in allKeys)
        {
            if (emittedPairs.Contains(c)) continue;
            char canon = CanonicalForLabel(c);
            char? paired = CasePair(c);

            // If a case-pair exists and both halves have the same
            // before/after state, emit a single combined line and mark
            // the partner as handled.
            if (paired is char p && IsCasePairUnified(p, before, after))
            {
                emittedPairs.Add(p);
                var label = BuildCasePairLabel(c);
                foreach (var line in DescribeOneChar(label, c, before, after))
                    yield return line;
                continue;
            }

            foreach (var line in DescribeOneChar($"PC {Quote(c)}", c, before, after))
                yield return line;
        }
    }

    // Formats an "upper/lower" case pair as "PC 'A/a'", accepting either
    // half of the pair as input. Both DescribeCharOverrides and
    // DescribeSuppressed coalesce case-pairs into a single line and were
    // spelling the label inline — factored so the two stay in step.
    private static string BuildCasePairLabel(char c)
    {
        char upper = char.IsLower(c) ? char.ToUpperInvariant(c) : c;
        char lower = char.IsLower(c) ? c : char.ToLowerInvariant(c);
        return $"PC '{upper}/{lower}'";
    }

    private static IEnumerable<string> DescribeOneChar(string label, char c,
        SettingsSnapshot before, SettingsSnapshot after)
    {
        bool wasOverride = before.CharOverrides.TryGetValue(c, out var prev);
        bool isOverride = after.CharOverrides.TryGetValue(c, out var curr);

        if (!wasOverride && isOverride)
            yield return $"{label} override added → MZ slot ({curr.Row},{curr.Col}) {ShiftWord(curr.MzShift)}";
        else if (wasOverride && !isOverride)
            yield return $"{label} override removed (was → ({prev.Row},{prev.Col}) {ShiftWord(prev.MzShift)})";
        else if (wasOverride && isOverride && !prev.Equals(curr))
            yield return $"{label} override moved: ({prev.Row},{prev.Col}) {ShiftWord(prev.MzShift)} → ({curr.Row},{curr.Col}) {ShiftWord(curr.MzShift)}";
    }

    private static IEnumerable<string> DescribeSuppressed(SettingsSnapshot before, SettingsSnapshot after)
    {
        var beforeSet = new HashSet<char>(before.SuppressedChars);
        var afterSet = new HashSet<char>(after.SuppressedChars);
        var addedRaw = afterSet.Except(beforeSet).ToList();
        var removedRaw = beforeSet.Except(afterSet).ToList();

        // Case-pair coalescing here too.
        foreach (var line in CoalescedSuppressionLines(addedRaw, isAdded: true))
            yield return line;
        foreach (var line in CoalescedSuppressionLines(removedRaw, isAdded: false))
            yield return line;
    }

    private static IEnumerable<string> CoalescedSuppressionLines(IList<char> chars, bool isAdded)
    {
        var emitted = new HashSet<char>();
        foreach (var c in chars.OrderBy(c => CanonicalSortKey(c)).ThenBy(c => (int)c))
        {
            if (emitted.Contains(c)) continue;
            char? paired = CasePair(c);
            string label;
            if (paired is char p && chars.Contains(p))
            {
                emitted.Add(p);
                label = BuildCasePairLabel(c);
            }
            else
            {
                label = $"PC {Quote(c)}";
            }

            if (isAdded)
            {
                if (CharMap.Defaults.TryGetValue(c, out var def))
                    yield return $"{label} default suppressed (was → ({def.Row},{def.Col}) {ShiftWord(def.MzShift)})";
                else
                    yield return $"{label} default suppressed";
            }
            else
            {
                if (CharMap.Defaults.TryGetValue(c, out var def))
                    yield return $"{label} default restored → ({def.Row},{def.Col}) {ShiftWord(def.MzShift)}";
                else
                    yield return $"{label} default restored";
            }
        }
    }

    private static IEnumerable<string> DescribeKeyOverrides(SettingsSnapshot before, SettingsSnapshot after)
    {
        var allKeys = before.KeyOverrides.Keys.Union(after.KeyOverrides.Keys)
            .OrderBy(k => k.ToString());
        foreach (var k in allKeys)
        {
            bool wasOverride = before.KeyOverrides.TryGetValue(k, out var prev);
            bool isOverride = after.KeyOverrides.TryGetValue(k, out var curr);
            string label = $"PC {VkLabel(k)}";

            if (!wasOverride && isOverride)
                yield return $"{label} override added → MZ slot ({curr.Row},{curr.Col}) {ShiftWord(curr.MzShift)}";
            else if (wasOverride && !isOverride)
                yield return $"{label} override removed (was → ({prev.Row},{prev.Col}) {ShiftWord(prev.MzShift)})";
            else if (wasOverride && isOverride && !prev.Equals(curr))
                yield return $"{label} override moved: ({prev.Row},{prev.Col}) {ShiftWord(prev.MzShift)} → ({curr.Row},{curr.Col}) {ShiftWord(curr.MzShift)}";
        }
    }

    private static bool IsCasePairUnified(char paired, SettingsSnapshot before, SettingsSnapshot after)
    {
        bool wasA = before.CharOverrides.TryGetValue(paired, out var pa);
        bool isA = after.CharOverrides.TryGetValue(paired, out var pb);
        return (wasA == isA) && (!wasA || pa.Equals(pb));
    }

    private static char? CasePair(char c)
    {
        if (!char.IsLetter(c)) return null;
        if (char.IsLower(c)) return char.ToUpperInvariant(c);
        if (char.IsUpper(c)) return char.ToLowerInvariant(c);
        return null;
    }

    private static char CanonicalForLabel(char c) => char.IsLetter(c) ? char.ToUpperInvariant(c) : c;
    private static char CanonicalSortKey(char c) => char.IsLetter(c) ? char.ToUpperInvariant(c) : c;

    private static string Quote(char c) => c == ' ' ? "Space" : $"'{c}'";

    private static string ShiftWord(bool mzShift) => mzShift ? "shifted" : "unshifted";

    private static string ShiftWord(bool? mzShift) => mzShift switch
    {
        true => "shifted",
        false => "unshifted",
        _ => "shift pass-through",
    };

    private static string VkLabel(Keys k) =>
        SpecialKeyMap.Labels.TryGetValue(k, out var s) ? s : k.ToString();
}
