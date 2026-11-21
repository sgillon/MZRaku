using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Startup check that the core's <see cref="HostKey"/> still mirrors
/// WinForms <see cref="Keys"/> (v1.4 design decision D1): the same
/// names with the same values. The WinForms front end converts between
/// the two with a cast, so a value drift would silently break bindings.
///
/// Declaration order and <c>ToString()</c> are not compared. WinForms on
/// .NET 10 swapped three alias pairs (<c>Oemtilde</c>/<c>Oem3</c>,
/// <c>OemQuotes</c>/<c>Oem7</c>, <c>OemBackslash</c>/<c>Oem102</c>), so
/// <c>Keys</c> now formats those values differently. Saved key names
/// come from <see cref="HostKey"/>, never <c>Keys</c>, and
/// <see cref="HostKeyNames"/> pins them.
///
/// Run alongside <see cref="MatrixValidation"/> and
/// <see cref="HostKeyNames"/>, the same way: silent when everything
/// agrees, a debug-output log and one warning when not.
/// </summary>
public static class HostKeyParity
{
    public static IReadOnlyList<string> Validate()
    {
        var keys = Members(typeof(Keys));
        var host = Members(typeof(HostKey));

        var complaints = new List<string>();
        foreach (var m in keys.Except(host)) complaints.Add($"Keys has {m}, HostKey doesn't");
        foreach (var m in host.Except(keys)) complaints.Add($"HostKey has {m}, Keys doesn't");
        return complaints;
    }

    public static void RunAndLog(Action<string, string> warn)
    {
        var complaints = Validate();
        if (complaints.Count == 0) return;
        Debug.WriteLine($"[HostKeyParity] {complaints.Count} complaint(s):");
        foreach (var c in complaints) Debug.WriteLine($"  {c}");
        warn(string.Join("\n", complaints),
             "Host key check — HostKey no longer matches WinForms Keys");
    }

    private static SortedSet<string> Members(Type t) => new(
        t.GetFields(BindingFlags.Public | BindingFlags.Static)
         .Select(f => $"{f.Name} = 0x{Convert.ToInt32(f.GetValue(null)):X}"),
        StringComparer.Ordinal);
}
