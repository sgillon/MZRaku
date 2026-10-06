using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Wraps the two mzkbd file-format handlers (Export + Import) plus
/// the WinForms prompts that surround them (file dialog, machine-
/// mismatch warning, merge / replace prompt, error surface).
///
/// Split out of <see cref="SettingsForm"/> in v1.2 audit F-050 —
/// SettingsForm was carrying six concerns; mzkbd I/O was the most
/// self-contained. Static methods here take just the pieces they
/// need (owner window for dialogs, the mutable Settings, active
/// machine, and a "refresh after change" callback) so a v2.0
/// Avalonia SettingsForm could call the same coordinator with a
/// different owner shape without dragging any WinForms class
/// hierarchy along.
/// </summary>
internal static class MzKbdIoCoordinator
{
    /// <summary>
    /// Prompts for a target path and writes the active machine's
    /// keyboard-map overrides to it. Success + failure both surface
    /// through a MessageBox on the owner. No-op if the user cancels
    /// the file dialog.
    /// </summary>
    /// <summary>
    /// The active machine's two override stores, as the operations
    /// export and import need: entry counts, serialise, clear, parse.
    /// </summary>
    private sealed record Stores(
        string Label,
        string FileStem,
        int CharCount,
        Func<IEnumerable<string>> CharLines,
        Action ClearChars,
        Func<string, string, bool> ParseChar,
        KeyOverride Keys);

    private static Stores StoresFor(Settings s, MachineType machine) => machine switch
    {
        MachineType.MZ80A => new("MZ-80A", "mz80a", s.Mz80aCharMapOverrides.Count,
            s.Mz80aCharMapOverrides.SerialiseLines, s.Mz80aCharMapOverrides.Clear,
            s.Mz80aCharMapOverrides.TryParseLine, s.Mz80aKeyOverrides),
        MachineType.MZ800 => new("MZ-800", "mz800", s.Mz800CharMapOverrides.Count,
            s.Mz800CharMapOverrides.SerialiseLines, s.Mz800CharMapOverrides.Clear,
            s.Mz800CharMapOverrides.TryParseLine, s.Mz800KeyOverrides),
        _ => new("MZ-700", "mz700", s.CharMapOverrides.Count,
            s.CharMapOverrides.SerialiseLines, s.CharMapOverrides.Clear,
            s.CharMapOverrides.TryParseLine, s.KeyOverrides),
    };

    public static void PromptAndExport(IWin32Window owner, Settings settings, MachineType activeMachine)
    {
        var stores = StoresFor(settings, activeMachine);
        int charCount = stores.CharCount;
        int keyCount  = stores.Keys.Count;
        var charLines = stores.CharLines();
        var keyOverrides = stores.Keys;
        string defaultFileName = $"{stores.FileStem}-keyboard.mzkbd";

        using var dlg = new SaveFileDialog
        {
            Title = $"Export {stores.Label} keyboard mapping",
            Filter = KeyboardMapFile.FileFilter,
            DefaultExt = "mzkbd",
            AddExtension = true,
            FileName = defaultFileName,
            InitialDirectory = AppContext.BaseDirectory,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            KeyboardMapFile.Save(dlg.FileName, activeMachine, charLines, keyOverrides);
            MessageBox.Show(owner,
                $"Exported {charCount} CharMap and {keyCount} KeyOverride entries to:\n{dlg.FileName}",
                "Export complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"Failed to save the file:\n\n{ex.Message}",
                "Export failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Prompts for a source path, reads it, then either merges or
    /// replaces the active machine's override stores with what came
    /// back. Refuses cross-machine imports (matrix coords don't
    /// align). Fires <paramref name="onImportApplied"/> after a
    /// successful import so the caller can refresh dependent UI
    /// (typically the diagram labels).
    /// </summary>
    public static void PromptAndImport(
        IWin32Window owner,
        Settings settings,
        MachineType activeMachine,
        Action onImportApplied)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import keyboard mapping",
            Filter = KeyboardMapFile.FileFilter,
            InitialDirectory = AppContext.BaseDirectory,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;

        KeyboardMapFile.LoadResult loaded;
        try
        {
            loaded = KeyboardMapFile.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"Failed to read the file:\n\n{ex.Message}",
                "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // File-machine vs active-machine check. Matrix coords aren't
        // compatible across the machines' keyboards, so a mismatched
        // import would land bindings on the wrong slots. Refuse rather
        // than silently misroute.
        if (loaded.Machine != activeMachine)
        {
            MessageBox.Show(owner,
                $"This file is a {loaded.Machine} keyboard mapping, but the current session is running {activeMachine}.\n\n" +
                "Matrix coordinates don't align between machines, so importing here would land bindings on the wrong slots.\n\n" +
                $"Switch machines (System → Machine → {loaded.Machine}) first, then import.",
                "Machine mismatch",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Empty file = nothing actionable; bail with a friendly note
        // rather than silently no-op.
        if (loaded.CharEntries.Count == 0 && loaded.KeyEntries.Count == 0)
        {
            MessageBox.Show(owner,
                "The file didn't contain any overrides to import.",
                "Nothing to import",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Merge / Replace / Cancel via a three-button prompt.
        // Yes = Merge (apply on top of current overrides),
        // No  = Replace (clear current first).
        var choice = MessageBox.Show(owner,
            $"Import contains {loaded.CharEntries.Count} CharMap and " +
            $"{loaded.KeyEntries.Count} KeyOverride entries.\n\n" +
            "Yes  = Merge into current overrides (imported entries win on conflict).\n" +
            "No   = Replace current overrides entirely.\n" +
            "Cancel = abort import.",
            "Import keyboard mapping",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        // Route into the active machine's override stores.
        var stores = StoresFor(settings, activeMachine);
        if (choice == DialogResult.No)
        {
            stores.ClearChars();
            stores.Keys.Clear();
        }
        foreach (var (k, v) in loaded.CharEntries)
            stores.ParseChar(k, v);
        foreach (var (k, v) in loaded.KeyEntries)
            stores.Keys.TryParseLine(k, v);

        onImportApplied();
    }
}
