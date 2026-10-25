using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Startup check that the core's <see cref="HostKey"/> still mirrors
/// WinForms <see cref="Keys"/> (v1.4 design decision D1). The WinForms
/// front end converts between the two with a cast, and key names are
/// saved in <c>settings.ini</c> and <c>.mzkbd</c> files, so a drift
/// would silently break bindings. Two checks:
///   - the same members, by name and value, in the same declaration
///     order (the order decides which of two names sharing a value,
///     such as <c>Enter</c> and <c>Return</c>, <c>ToString()</c> picks);
///   - the same <c>ToString()</c> for every key code under every
///     combination of Shift, Control and Alt, which is the exact text
///     the override layer writes.
/// Run alongside <see cref="MatrixValidation"/>, the same way: silent
/// when everything agrees, a debug-output log and one dialog when not.
/// </summary>
public static class HostKeyParity
{
    private const int MaxFormatComplaints = 20;

    public static IReadOnlyList<string> Validate()
    {
        var complaints = new List<string>();

        var keys = typeof(Keys).GetFields(BindingFlags.Public | BindingFlags.Static);
        var host = typeof(HostKey).GetFields(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < Math.Max(keys.Length, host.Length); i++)
        {
            string k = i < keys.Length ? Describe(keys[i]) : "(none)";
            string h = i < host.Length ? Describe(host[i]) : "(none)";
            if (k != h) complaints.Add($"member {i}: Keys has {k}, HostKey has {h}");
        }

        int formatMismatches = 0;
        for (int code = 0; code <= 0xFF; code++)
        {
            for (int mods = 0; mods < 8; mods++)
            {
                int value = code | (mods << 16);
                string k = ((Keys)value).ToString();
                string h = ((HostKey)value).ToString();
                if (k == h) continue;
                if (++formatMismatches <= MaxFormatComplaints)
                    complaints.Add($"0x{value:X5} formats as \"{k}\" in Keys but \"{h}\" in HostKey");
            }
        }
        if (formatMismatches > MaxFormatComplaints)
            complaints.Add($"... and {formatMismatches - MaxFormatComplaints} more formatting differences");

        return complaints;
    }

    public static void RunAndLog()
    {
        var complaints = Validate();
        if (complaints.Count == 0) return;
        Debug.WriteLine($"[HostKeyParity] {complaints.Count} complaint(s):");
        foreach (var c in complaints) Debug.WriteLine($"  {c}");
        MessageBox.Show(
            string.Join("\n", complaints),
            "Host key check — HostKey no longer matches WinForms Keys",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static string Describe(FieldInfo f) =>
        $"{f.Name} = 0x{Convert.ToInt32(f.GetValue(null)):X}";
}
