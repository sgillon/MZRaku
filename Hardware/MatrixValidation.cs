using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// Single entry point for the startup-time consistency check between
/// <see cref="Mz700MatrixReference"/> (the canonical matrix truth) and
/// the data files that consume it: <see cref="SpecialKeyMap"/>,
/// <see cref="CharMap"/>, and <see cref="MzKeyboardLayout"/>.
///
/// Drift between these files is exactly the bug class the matrix
/// reference was introduced to catch (the CTRL=(8,6) and @=(1,5) slot
/// fixes on 2026-06-12 and 2026-06-13 had been latent for weeks because
/// nothing forced the codebase's coordinates to be cross-checked).
/// </summary>
public static class MatrixValidation
{
    /// <summary>
    /// Runs every validator and returns all complaints in source order
    /// (reference → SpecialKeyMap → CharMap → MzKeyboardLayout). Each
    /// line is prefixed with the source so the origin is obvious.
    /// </summary>
    public static IReadOnlyList<string> RunAll()
    {
        var all = new List<string>();
        AddPrefixed(all, "Mz700MatrixReference", Mz700MatrixReference.Validate());
        AddPrefixed(all, "SpecialKeyMap",        SpecialKeyMap.Validate());
        AddPrefixed(all, "CharMap",              CharMap.Validate());
        AddPrefixed(all, "MzKeyboardLayout",     MzKeyboardLayout.Validate());
        return all;
    }

    /// <summary>
    /// Runs every validator at startup. Silent when everything agrees;
    /// loud when it doesn't — writes complaints to the debug output
    /// stream and pops a single MessageBox so drift can't slip past a
    /// developer who isn't watching the debugger. Release users
    /// shouldn't ever see the dialog (it means a build shipped with
    /// undetected matrix drift, which is itself a bug worth surfacing).
    /// </summary>
    public static void RunAndLog()
    {
        var complaints = RunAll();
        if (complaints.Count == 0) return;
        Debug.WriteLine($"[MatrixValidation] {complaints.Count} complaint(s):");
        foreach (var c in complaints) Debug.WriteLine($"  {c}");
        MessageBox.Show(
            string.Join("\n", complaints),
            $"Matrix validation — {complaints.Count} drift(s) detected",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static void AddPrefixed(List<string> all, string prefix, IReadOnlyList<string> items)
    {
        foreach (var item in items) all.Add($"[{prefix}] {item}");
    }
}
