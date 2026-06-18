using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Tabbed settings dialog (Ctrl+,) — the user-facing front end for the
/// values that live in <c>settings.ini</c>. Three tabs at launch:
/// Display, ROMs, Joystick.
///
/// Pattern: the form takes a <see cref="Settings"/> instance, populates
/// its controls from it, and on OK / Apply writes any changes back to
/// the same instance, persists via <see cref="Settings.Save"/> and
/// raises <see cref="Applied"/>. <see cref="MainForm"/> subscribes to
/// <see cref="Applied"/> to push live changes (display scale, joystick
/// button bindings) into the running emulator; ROM-path changes are
/// next picked up on the following reset.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly JoystickInput? _joystickInput;
    private readonly MZ700? _machine;

    // Keyboard tab — diagram is the primary view (P2-7); matrix grid
    // lives behind an Advanced expander.
    private MzKeyboardDiagram? _kbdDiagram;

    // Display
    private readonly RadioButton _rb1x = new() { Text = "&1× (320×200)", AutoSize = true };
    private readonly RadioButton _rb2x = new() { Text = "&2× (640×400)", AutoSize = true };
    private readonly RadioButton _rb3x = new() { Text = "&3× (960×600)", AutoSize = true };

    // ROMs
    private readonly TextBox _txtMonitor = new() { Width = 280 };
    private readonly TextBox _txtFont = new() { Width = 280 };
    private readonly TextBox _txtBasic = new() { Width = 280 };
    private readonly Label _lblMonitorStatus = new() { AutoSize = true };
    private readonly Label _lblFontStatus = new() { AutoSize = true };
    private readonly Label _lblBasicStatus = new() { AutoSize = true };

    // Joystick
    private readonly NumericUpDown _numButton1 = new() { Minimum = 0, Maximum = 31, Width = 60 };
    private readonly NumericUpDown _numButton2 = new() { Minimum = 0, Maximum = 31, Width = 60 };

    // Pre-edit baseline captured when the dialog opens; diffed against
    // the live state at Apply / OK time so the user sees a summary of
    // exactly what's about to be persisted. Refreshed after a successful
    // Apply so a second Apply doesn't re-list the same changes.
    private SettingsSnapshot _baseline = null!;

    /// <summary>Raised after settings are written. MainForm uses this to
    /// reflect changes in the running emulator (display scale, joystick
    /// button bindings).</summary>
    public event Action? Applied;

    public SettingsForm(Settings settings, JoystickInput? joystickInput = null, MZ700? machine = null)
    {
        _settings = settings;
        _joystickInput = joystickInput;
        _machine = machine;
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        // Sized to the Keyboard tab's natural content (caption + 210 px
        // diagram + Export/Import row + Advanced settings button +
        // known-limitations panel); the matrix grid + overrides list
        // live in AdvancedKeyboardForm now, so the main dialog no
        // longer has to budget room for them.
        ClientSize = new Size(740, 600);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildRomsTab());
        tabs.TabPages.Add(BuildDisplayTab());
        tabs.TabPages.Add(BuildKeyboardTab());
        tabs.TabPages.Add(BuildJoystickTab());

        var buttonRow = BuildButtonRow();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(buttonRow, 0, 1);
        Controls.Add(root);

        LoadFromSettings();
        WireValidation();

        _baseline = SettingsSnapshot.Capture(_settings);
    }

    // -- Tab construction -----------------------------------------------

    private TabPage BuildDisplayTab()
    {
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            WrapContents = false,
        };
        stack.Controls.Add(new Label
        {
            Text = "Window scale:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        });
        stack.Controls.Add(_rb1x);
        stack.Controls.Add(_rb2x);
        stack.Controls.Add(_rb3x);
        return BuildTabPage("Display", stack);
    }

    private TabPage BuildRomsTab()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

        AddRomRow(grid, 0, "Monitor ROM:", _txtMonitor, _lblMonitorStatus,
            "Select monitor ROM (1z-013a.rom)", "ROM files (*.rom;*.bin)|*.rom;*.bin|All files|*.*");
        AddRomRow(grid, 1, "Font ROM:", _txtFont, _lblFontStatus,
            "Select character ROM (mz700fon.int)", "Font files (*.int;*.bin;*.txt)|*.int;*.bin;*.txt|All files|*.*");
        AddRomRow(grid, 2, "BASIC:", _txtBasic, _lblBasicStatus,
            "Select S-BASIC cassette (1Z-013B.mzf)", "Cassette files (*.mzf;*.m12;*.mzt)|*.mzf;*.m12;*.mzt|All files|*.*");

        var hint = new Label
        {
            Text = "Monitor/Font path changes take effect on next launch.\nBASIC path takes effect on next Load BASIC.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        grid.Controls.Add(hint, 0, 3);
        grid.SetColumnSpan(hint, 4);

        return BuildTabPage("ROMs", grid);
    }

    private void AddRomRow(TableLayoutPanel grid, int row, string label, TextBox textBox, Label statusLabel,
        string browseTitle, string browseFilter)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0) }, 0, row);
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 4, 6, 4);
        grid.Controls.Add(textBox, 1, row);
        statusLabel.Anchor = AnchorStyles.Left;
        statusLabel.Margin = new Padding(0, 8, 0, 0);
        grid.Controls.Add(statusLabel, 2, row);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        browse.Click += (_, _) => BrowseFor(textBox, browseTitle, browseFilter);
        grid.Controls.Add(browse, 3, row);
    }

    private TabPage BuildJoystickTab()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));

        var header = new Label
        {
            Text = "PC gamepad button → MZ-1X03 stick",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        grid.Controls.Add(header, 0, 0);
        grid.SetColumnSpan(header, 3);

        AddJoystickRow(grid, 1, "Left button (SW1):", _numButton1);
        AddJoystickRow(grid, 2, "Right button (SW2):", _numButton2);

        var hint = new Label
        {
            Text = "Click Capture… then press a button on your controller.\nChanges take effect on Apply / OK.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 12, 0, 0),
        };
        grid.Controls.Add(hint, 0, 3);
        grid.SetColumnSpan(hint, 3);

        return BuildTabPage("Joystick", grid);
    }

    private void AddJoystickRow(TableLayoutPanel grid, int row, string label, NumericUpDown spinner)
    {
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
        }, 0, row);
        spinner.Margin = new Padding(0, 2, 6, 2);
        grid.Controls.Add(spinner, 1, row);
        var capture = new Button
        {
            Text = "Capture…",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Enabled = _joystickInput != null,
        };
        capture.Click += (_, _) => CaptureButtonFor(spinner);
        grid.Controls.Add(capture, 2, row);
    }

    private TabPage BuildKeyboardTab()
    {
        // P2-7 layout: MzKeyboardDiagram is the primary view (top),
        // matrix grid is hidden behind an Advanced expander, and the
        // overrides list keeps its place at the bottom. AutoScroll on
        // the tab content covers the matrix grid (~678 tall) when the
        // expander is open, since the dialog itself is fixed-size.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // caption
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210f));  // diagram
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // export / import row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // advanced settings button
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // known-limitations panel

        layout.Controls.Add(new Label
        {
            Text = "Click any key on the diagram to edit its PC-keyboard binding. "
                 + "Each cap shows the PC key(s) currently bound to it.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);

        _kbdDiagram = new MzKeyboardDiagram { Dock = DockStyle.Fill };
        _kbdDiagram.KeyClicked += OnKeyboardDiagramKeyClicked;
        RefreshKeyboardDiagramLabels();
        layout.Controls.Add(_kbdDiagram, 0, 1);

        // Export / Import row — operates on the whole mapping, so it
        // stays in the primary view rather than under Advanced.
        var ioButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 4),
        };
        var exportBtn = new Button { Text = "Export…", Width = 90 };
        var importBtn = new Button { Text = "Import…", Width = 90 };
        exportBtn.Click += (_, _) => OnExportMzKbd();
        importBtn.Click += (_, _) => OnImportMzKbd();
        ioButtons.Controls.Add(exportBtn);
        ioButtons.Controls.Add(importBtn);
        layout.Controls.Add(ioButtons, 0, 2);

        var advancedBtn = new Button
        {
            Text = "Advanced settings…",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 0, 4),
        };
        advancedBtn.Click += (_, _) => OpenAdvancedKeyboard();
        layout.Controls.Add(advancedBtn, 0, 3);

        layout.Controls.Add(BuildKeyboardLimitationsPanel(), 0, 4);

        return BuildTabPage("Keyboard", layout);
    }

    private const string KeyboardDocUrl =
        "https://github.com/sgillon/Mz700Emul/blob/main/docs/usage/keyboard.md";

    private static Control BuildKeyboardLimitationsPanel()
    {
        var group = new GroupBox
        {
            Text = "Known limitations",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // GroupBox's title sits inside its top padding band — 8px
            // isn't enough to clear the text, so the first content row
            // overlaps the heading. Same 6/16/6/6 pattern used by the
            // HID Diagnostic groupboxes.
            Padding = new Padding(8, 16, 8, 8),
            Margin = new Padding(0, 12, 0, 0),
        };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            // Dock so the FlowLayoutPanel respects the GroupBox's
            // Padding — without this it sits at (0, 0) and the first
            // bullet renders over the heading text.
            Dock = DockStyle.Fill,
        };

        stack.Controls.Add(Item(
            "Font Sheet — bank-1 click-to-type lands the byte but the attribute "
            + "isn't switched to bank 1, so the glyph renders as its bank-0 "
            + "equivalent. Browse-mode (reading bank 1) still works."));
        stack.Controls.Add(Item(
            "Rapid char-driven input can occasionally drop the MZ shift bit, "
            + "so a shifted character registers unshifted (e.g. repeated '@' "
            + "may produce ''')."));
        stack.Controls.Add(Item(
            "Left and Right PC Ctrl are not distinguished — both fire MZ Ctrl. "
            + "The keyboard editor can't currently bind them separately."));

        var linkRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        linkRow.Controls.Add(new Label
        {
            Text = "Full details:",
            AutoSize = true,
            Margin = new Padding(0, 3, 6, 0),
            ForeColor = SystemColors.ControlDarkDark,
        });
        var link = new LinkLabel
        {
            Text = "docs/usage/keyboard.md",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = KeyboardDocUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't open browser:\n" + ex.Message, "Settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        linkRow.Controls.Add(link);
        stack.Controls.Add(linkRow);

        group.Controls.Add(stack);
        return group;

        static Label Item(string text) => new()
        {
            Text = "• " + text,
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Margin = new Padding(0, 0, 0, 4),
            ForeColor = SystemColors.ControlText,
        };
    }

    private void OpenAdvancedKeyboard()
    {
        using var dlg = new AdvancedKeyboardForm(
            _machine, _settings.CharMapOverrides, _settings.KeyOverrides);
        dlg.ShowDialog(this);
        // Edits flow into the shared override instances; refresh the
        // diagram so any changes made via the matrix grid show through.
        RefreshKeyboardDiagramLabels();
    }

    private void OnExportMzKbd()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export keyboard mapping",
            Filter = KeyboardMapFile.FileFilter,
            DefaultExt = "mzkbd",
            AddExtension = true,
            FileName = "mz700-keyboard.mzkbd",
            InitialDirectory = AppContext.BaseDirectory,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            KeyboardMapFile.Save(dlg.FileName,
                _settings.CharMapOverrides, _settings.KeyOverrides);
            MessageBox.Show(this,
                $"Exported {_settings.CharMapOverrides.Count} CharMap and " +
                $"{_settings.KeyOverrides.Count} KeyOverride entries to:\n{dlg.FileName}",
                "Export complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to save the file:\n\n{ex.Message}",
                "Export failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnImportMzKbd()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import keyboard mapping",
            Filter = KeyboardMapFile.FileFilter,
            InitialDirectory = AppContext.BaseDirectory,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        CharMapOverrides importedChars;
        KeyOverride importedVks;
        try
        {
            (importedChars, importedVks) = KeyboardMapFile.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to read the file:\n\n{ex.Message}",
                "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Empty file = nothing actionable; bail with a friendly note
        // rather than silently no-op.
        if (importedChars.Count == 0 && importedVks.Count == 0)
        {
            MessageBox.Show(this,
                "The file didn't contain any overrides to import.",
                "Nothing to import",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Merge / Replace / Cancel via a three-button prompt.
        // Yes = Merge (apply on top of current overrides),
        // No  = Replace (clear current first).
        var choice = MessageBox.Show(this,
            $"Import contains {importedChars.Count} CharMap and " +
            $"{importedVks.Count} KeyOverride entries.\n\n" +
            "Yes  = Merge into current overrides (imported entries win on conflict).\n" +
            "No   = Replace current overrides entirely.\n" +
            "Cancel = abort import.",
            "Import keyboard mapping",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        if (choice == DialogResult.No)
        {
            _settings.CharMapOverrides.Clear();
            _settings.KeyOverrides.Clear();
        }
        foreach (var kv in importedChars.All)
            _settings.CharMapOverrides.Set(kv.Key, kv.Value);
        foreach (var kv in importedVks.All)
            _settings.KeyOverrides.Set(kv.Key, kv.Value);

        // Refresh the surface controls so the change is visible
        // immediately. Persistence still waits for Apply / OK.
        RefreshKeyboardDiagramLabels();
    }

    private void OnKeyboardDiagramKeyClicked(object? sender, KeyDiagramClickedEventArgs e)
    {
        // MZ Shift is permanently wired to PC Shift via the Keyboard
        // modifier path (concurrent assertion is needed so Shift+1 → '!'
        // produces the character bit and the shift bit simultaneously).
        // Surfacing the editor would imply it's rebindable; explain instead.
        if (e.Key.Row == 8 && e.Key.Col == 0)
        {
            MessageBox.Show(this,
                "MZ Shift is permanently bound to your PC Shift key.\n\n" +
                "Unlike the other keys, Shift is held alongside whatever else " +
                "you press (so Shift+1 produces '!'), which needs the MZ shift " +
                "bit and the character bit asserted at the same time. That's " +
                "handled by a dedicated path and isn't rebindable from here.",
                "MZ Shift", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Editor mutates the override layers directly — change is live
        // for subsequent emulator keystrokes. Persistence still waits
        // for this dialog's Apply / OK.
        using var editor = new MzKeyEditorForm(
            e.Key, _settings.CharMapOverrides, _settings.KeyOverrides);
        editor.ShowDialog(this);
        RefreshKeyboardDiagramLabels();
    }

    private void RefreshKeyboardDiagramLabels()
    {
        if (_kbdDiagram == null) return;
        _kbdDiagram.PcKeyLabels = PcKeyIndex.BuildLabelsByMzKey(
            _settings.CharMapOverrides, _settings.KeyOverrides);

        // Recompute the unreachable-essential set so the red outline on
        // affected caps tracks live with edits — Apply's safety gate
        // reads the same set, so what you see on the diagram before
        // Apply is what the confirm dialog will mention.
        var slotShiftLabels = PcKeyIndex.BuildLabelsBySlotShift(
            _settings.CharMapOverrides, _settings.KeyOverrides);
        var unreachable = new HashSet<string>();
        foreach (var k in MzKeyboardLayout.EssentialKeys)
        {
            if (!IsKeyFullyReachable(k, slotShiftLabels))
                unreachable.Add(k.Id);
        }
        _kbdDiagram.UnreachableKeyIds = unreachable.Count > 0 ? unreachable : null;

        _kbdDiagram.RefreshLabels();
    }

    /// <summary>
    /// A character key with both unshifted and shifted glyphs is
    /// "fully reachable" only if both halves can be produced from the
    /// host keyboard — losing just the shifted half (e.g. PC '1'
    /// rebound but Shift+1 still maps to MZ '!') still leaves the
    /// unshifted half unreachable, which the gate must surface.
    ///
    /// Fixed-label keys (CR, GRAPH, ALPHA, CTRL, SHIFT, BREAK, INST,
    /// DEL, cursors) are shift-agnostic — any binding in either shift
    /// state is enough.
    /// </summary>
    private static bool IsKeyFullyReachable(
        MzKeyboardLayout.MzKey k,
        IReadOnlyDictionary<(int row, int col, bool shift), IReadOnlyList<string>> labels)
    {
        if (!k.Row.HasValue || !k.Col.HasValue) return true;
        int row = k.Row.Value, col = k.Col.Value;

        if (!string.IsNullOrEmpty(k.FixedLabel))
            return labels.ContainsKey((row, col, false))
                || labels.ContainsKey((row, col, true));

        bool hasUnshifted = !string.IsNullOrEmpty(k.UnshiftedLabel)
            || MzGlyphCatalog.FindByPrintableSlot(row, col, false).HasValue;
        bool hasShifted = !string.IsNullOrEmpty(k.ShiftedLabel)
            || MzGlyphCatalog.FindByPrintableSlot(row, col, true).HasValue;

        if (hasUnshifted && !labels.ContainsKey((row, col, false))) return false;
        if (hasShifted && !labels.ContainsKey((row, col, true))) return false;
        return true;
    }

    private void CaptureButtonFor(NumericUpDown target)
    {
        if (_joystickInput == null) return;
        using var capture = new JoystickCaptureForm(_joystickInput);
        if (capture.ShowDialog(this) != DialogResult.OK) return;
        int idx = capture.CapturedButtonIndex;
        if (idx >= (int)target.Minimum && idx <= (int)target.Maximum)
            target.Value = idx;
    }

    // -- Tab page with optional right-docked image ----------------------

    private static TabPage BuildTabPage(string text, Control content)
    {
        var page = new TabPage(text);
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    // -- Button row -----------------------------------------------------

    private Panel BuildButtonRow()
    {
        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        var apply = new Button { Text = "Apply", Width = 80 };
        // OK / Apply both run the safety gate before persisting. OK uses
        // DialogResult.None so a refused gate keeps the dialog open
        // rather than closing as it would with DialogResult.OK.
        ok.Click += (_, _) =>
        {
            if (!ConfirmKeyboardSafetyGate()) return;
            if (!ConfirmDiff()) return;
            ApplyChanges();
            DialogResult = DialogResult.OK;
            Close();
        };
        apply.Click += (_, _) =>
        {
            if (!ConfirmKeyboardSafetyGate()) return;
            if (!ConfirmDiff()) return;
            ApplyChanges();
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        flow.Controls.Add(apply);
        flow.Controls.Add(cancel);
        flow.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        return flow;
    }

    // -- Load / Apply ---------------------------------------------------

    private void LoadFromSettings()
    {
        switch (_settings.DisplayScale)
        {
            case 1: _rb1x.Checked = true; break;
            case 3: _rb3x.Checked = true; break;
            default: _rb2x.Checked = true; break;
        }
        _txtMonitor.Text = _settings.MonitorRomPath;
        _txtFont.Text = _settings.FontPath;
        _txtBasic.Text = _settings.BasicPath;
        _numButton1.Value = Clamp(_settings.JoyButton1Index, 0, 31);
        _numButton2.Value = Clamp(_settings.JoyButton2Index, 0, 31);
        RefreshAllRomStatus();
    }

    private void ApplyChanges()
    {
        _settings.DisplayScale = _rb3x.Checked ? 3 : _rb1x.Checked ? 1 : 2;
        _settings.MonitorRomPath = _txtMonitor.Text.Trim();
        _settings.FontPath = _txtFont.Text.Trim();
        _settings.BasicPath = _txtBasic.Text.Trim();
        _settings.JoyButton1Index = (int)_numButton1.Value;
        _settings.JoyButton2Index = (int)_numButton2.Value;
        _settings.Save();
        Applied?.Invoke();

        // Reset baseline so a follow-up Apply only summarises further
        // edits, not the ones the user just confirmed.
        _baseline = SettingsSnapshot.Capture(_settings);
    }

    /// <summary>
    /// Apply-time change summary: snapshot the dialog's current state
    /// (controls for scalars, live overrides for keyboard layers) and
    /// diff against the baseline taken when the dialog opened. If there
    /// are changes, show a Yes/No confirmation listing them so the user
    /// can see exactly what's about to be persisted. Returns false only
    /// if the user declines; an empty diff falls through silently.
    /// </summary>
    private bool ConfirmDiff()
    {
        var candidate = SettingsSnapshot.Build(
            displayScale: _rb3x.Checked ? 3 : _rb1x.Checked ? 1 : 2,
            monitorPath: _txtMonitor.Text.Trim(),
            fontPath: _txtFont.Text.Trim(),
            basicPath: _txtBasic.Text.Trim(),
            joy1: (int)_numButton1.Value,
            joy2: (int)_numButton2.Value,
            charOverrides: _settings.CharMapOverrides,
            keyOverrides: _settings.KeyOverrides);

        var lines = SettingsDiff.Describe(_baseline, candidate);
        if (lines.Count == 0) return true;

        const int previewMax = 16;
        var preview = string.Join("\n", lines.Take(previewMax).Select(l => "  • " + l));
        if (lines.Count > previewMax)
            preview += $"\n  • … (+{lines.Count - previewMax} more)";

        var result = MessageBox.Show(this,
            $"Saving {lines.Count} change{(lines.Count == 1 ? "" : "s")}:\n\n{preview}\n\nApply?",
            "Confirm changes",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        return result == DialogResult.Yes;
    }

    /// <summary>
    /// P2-9 safety gate: if any essential MZ key has no PC binding,
    /// switch to the Keyboard tab, highlight the unreachable caps on
    /// the diagram (already happening live via
    /// <see cref="RefreshKeyboardDiagramLabels"/>), and ask the user to
    /// confirm before saving. Returns true if Apply may proceed.
    /// </summary>
    private bool ConfirmKeyboardSafetyGate()
    {
        var unreachableIds = _kbdDiagram?.UnreachableKeyIds;
        if (unreachableIds == null || unreachableIds.Count == 0) return true;

        var unreachable = MzKeyboardLayout.Keys
            .Where(k => unreachableIds.Contains(k.Id))
            .ToList();

        // Pull the user's attention to the diagram so the red outlines
        // and the dialog text describe the same keys.
        if (_kbdDiagram?.Parent is TabPage page && page.Parent is TabControl tabs)
            tabs.SelectedTab = page;

        const int previewMax = 10;
        var names = string.Join(", ", unreachable.Take(previewMax).Select(DescribeKeyForGate));
        if (unreachable.Count > previewMax)
            names += $", … (+{unreachable.Count - previewMax} more)";

        var result = MessageBox.Show(this,
            $"{unreachable.Count} essential MZ key(s) have no PC binding:\n\n" +
            $"{names}\n\n" +
            "These keys are unreachable from the host keyboard until rebound. " +
            "Apply anyway?",
            "Unreachable keys — Apply anyway?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return result == DialogResult.Yes;
    }

    private static string DescribeKeyForGate(MzKeyboardLayout.MzKey k)
    {
        if (!string.IsNullOrEmpty(k.FixedLabel)) return k.FixedLabel!;
        if (!string.IsNullOrEmpty(k.UnshiftedLabel)) return k.UnshiftedLabel!;
        if (!string.IsNullOrEmpty(k.ShiftedLabel)) return k.ShiftedLabel!;
        if (k.Row.HasValue && k.Col.HasValue)
        {
            var c = MzGlyphCatalog.FindByPrintableSlot(k.Row.Value, k.Col.Value, false)
                  ?? MzGlyphCatalog.FindByPrintableSlot(k.Row.Value, k.Col.Value, true);
            if (c.HasValue) return c.Value.ToString();
        }
        return k.Id;
    }

    // -- ROM browse + path-status indicator -----------------------------

    private void WireValidation()
    {
        _txtMonitor.TextChanged += (_, _) => UpdateRomStatus(_txtMonitor, _lblMonitorStatus);
        _txtFont.TextChanged += (_, _) => UpdateRomStatus(_txtFont, _lblFontStatus);
        _txtBasic.TextChanged += (_, _) => UpdateRomStatus(_txtBasic, _lblBasicStatus);
    }

    private void RefreshAllRomStatus()
    {
        UpdateRomStatus(_txtMonitor, _lblMonitorStatus);
        UpdateRomStatus(_txtFont, _lblFontStatus);
        UpdateRomStatus(_txtBasic, _lblBasicStatus);
    }

    private static void UpdateRomStatus(TextBox textBox, Label statusLabel)
    {
        var path = textBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            statusLabel.Text = "(unset)";
            statusLabel.ForeColor = SystemColors.GrayText;
            return;
        }
        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(resolved))
        {
            statusLabel.Text = "✓ found";
            statusLabel.ForeColor = Color.FromArgb(0, 128, 0);
        }
        else
        {
            statusLabel.Text = "✗ missing";
            statusLabel.ForeColor = Color.FromArgb(192, 0, 0);
        }
    }

    private void BrowseFor(TextBox target, string title, string filter)
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        var current = target.Text.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            var resolved = Path.IsPathRooted(current)
                ? current
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, current));
            if (File.Exists(resolved))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(resolved);
                dlg.FileName = Path.GetFileName(resolved);
            }
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        target.Text = MakeRelativeToBase(dlg.FileName);
    }

    private static string MakeRelativeToBase(string absolutePath)
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(absolutePath);
        var prefix = baseDir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(prefix.Length)
            : full;
    }


    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
