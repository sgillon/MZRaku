using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MZRaku.Core;

/// <summary>
/// Single entry point for the startup-time consistency check between
/// the canonical matrix references (<see cref="Mz700MatrixReference"/>,
/// <see cref="Mz80aMatrixReference"/>, <see cref="Mz800MatrixReference"/>)
/// and the data files that consume them: <see cref="Mz700SpecialKeyMap"/>,
/// <see cref="Mz700CharMap"/>, <see cref="Mz700KeyboardLayout"/>,
/// <see cref="Mz80aSpecialKeyMap"/>, <see cref="Mz80aCharMap"/>,
/// <see cref="Mz800SpecialKeyMap"/>, <see cref="Mz800CharMap"/>.
///
/// Drift between these files is exactly the bug class the matrix
/// reference was introduced to catch (the CTRL=(8,6) and @=(1,5) slot
/// fixes on 2026-06-12 and 2026-06-13 had been latent for weeks because
/// nothing forced the codebase's coordinates to be cross-checked).
///
/// All three machines are validated at every launch — the tables are
/// static so there's no cost to catching one machine's drift while
/// running another.
/// </summary>
public static class MatrixValidation
{
    /// <summary>
    /// Runs every validator and returns all complaints in source order
    /// (MZ-700 reference → Mz700SpecialKeyMap → CharMap → Mz700KeyboardLayout →
    /// MZ-80A reference → Mz80aSpecialKeyMap → Mz80aCharMap). Each line
    /// is prefixed with the source so the origin is obvious.
    /// </summary>
    internal static IReadOnlyList<string> RunAll()
    {
        var all = new List<string>();
        AddPrefixed(all, "Mz700MatrixReference", Mz700MatrixReference.Validate());
        AddPrefixed(all, "Mz700SpecialKeyMap",   Mz700SpecialKeyMap.Validate());
        AddPrefixed(all, "Mz700CharMap",         Mz700CharMap.Validate());
        AddPrefixed(all, "Mz700KeyboardLayout",  Mz700KeyboardLayout.Validate());
        AddPrefixed(all, "Mz80aMatrixReference", Mz80aMatrixReference.Validate());
        AddPrefixed(all, "Mz80aSpecialKeyMap",   Mz80aSpecialKeyMap.Validate());
        AddPrefixed(all, "Mz80aCharMap",         Mz80aCharMap.Validate());
        AddPrefixed(all, "Mz800MatrixReference", Mz800MatrixReference.Validate());
        AddPrefixed(all, "Mz800SpecialKeyMap",   Mz800SpecialKeyMap.Validate());
        AddPrefixed(all, "Mz800CharMap",         Mz800CharMap.Validate());
        AddPrefixed(all, "Mz800KeyboardLayout",  Mz800KeyboardLayout.Validate());
        return all;
    }

    /// <summary>
    /// Runs every validator at startup. Silent when everything agrees;
    /// loud when it doesn't — writes complaints to the debug output
    /// stream and hands them once to <paramref name="warn"/> (message,
    /// then title), which the front end shows as a dialog, so drift
    /// can't slip past a developer who isn't watching the debugger. The
    /// core shows no dialogs itself (v1.4 design decision D6). Release
    /// users shouldn't ever see the warning (it means a build shipped
    /// with undetected matrix drift, which is itself a bug worth
    /// surfacing).
    /// </summary>
    public static void RunAndLog(Action<string, string> warn)
    {
        var complaints = RunAll();
        if (complaints.Count == 0) return;
        Debug.WriteLine($"[MatrixValidation] {complaints.Count} complaint(s):");
        foreach (var c in complaints) Debug.WriteLine($"  {c}");
        warn(string.Join("\n", complaints),
             $"Matrix validation — {complaints.Count} drift(s) detected");
    }

    private static void AddPrefixed(List<string> all, string prefix, IReadOnlyList<string> items)
    {
        foreach (var item in items) all.Add($"[{prefix}] {item}");
    }
}
