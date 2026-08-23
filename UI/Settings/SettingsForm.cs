using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

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
    /// <summary>
    /// Which tab to land on when the dialog opens. Values are aligned
    /// with the order tabs are added in the constructor so the host
    /// can pass them straight to <see cref="TabControl.SelectedIndex"/>.
    /// </summary>
    public enum Tab
    {
        Startup = 0,
        Roms = 1,
        Display = 2,
        Keyboard = 3,
        Joystick = 4,
    }

    private readonly Settings _settings;
    private readonly JoystickInput? _joystickInput;
    private readonly MZ700? _machine;
    // MZ-80A instance for Phase 5.5b Advanced Keyboard editor.
    // Populated by MainForm.OpenSettings when the active machine is
    // MZ-80A; null on MZ-700. Enables OpenAdvancedKeyboard to
    // construct a Mz80aKeyboardEditorContext instead of falling
    // through to the pre-5.5b "coming soon" fallback.
    private readonly MZ80A? _mz80a;

    // Keyboard tab — diagram is the primary view (P2-7); matrix grid
    // lives behind an Advanced expander.
    private MzKeyboardDiagram? _kbdDiagram;

    // Display
    private readonly RadioButton _rb1x = new() { Text = "&1× (320×200)", AutoSize = true };
    private readonly RadioButton _rb2x = new() { Text = "&2× (640×400)", AutoSize = true };
    private readonly RadioButton _rb3x = new() { Text = "&3× (960×600)", AutoSize = true };
    private readonly CheckBox _chkScanlines = new() { Text = "CRT-style scan&lines", AutoSize = true };
    // MZ-80A only (Phase 5.2). Green phosphor tint on the monochrome
    // display — retires the previous View → Green screen menu item.
    private readonly CheckBox _chkMz80aGreenScreen = new()
    {
        Text = "&Green phosphor tint (authentic MZ-80A look)",
        AutoSize = true,
    };
    // MZ-80A only (Phase 5.2). Inverts Shift-for-uppercase behaviour
    // on letter keys — retires the INI-only Mz80aInvertLetterShift
    // property.
    private readonly CheckBox _chkMz80aInvertLetterShift = new()
    {
        Text = "&Invert letter Shift (PC-style Shift-for-uppercase)",
        AutoSize = true,
    };

    // ROMs — per-machine as of Phase 5.1a. Both machines' rom sets are
    // shown side-by-side so the user can maintain both without switching
    // machine first; whichever isn't the active machine renders with a
    // "(not active)" title and dimmed group heading (D5).
    private readonly TextBox _txtMz700Monitor = new() { Width = 280 };
    private readonly TextBox _txtMz700Font = new() { Width = 280 };
    private readonly TextBox _txtMz700Basic = new() { Width = 280 };
    private readonly Label _lblMz700MonitorStatus = new() { AutoSize = true };
    private readonly Label _lblMz700FontStatus = new() { AutoSize = true };
    private readonly Label _lblMz700BasicStatus = new() { AutoSize = true };
    private readonly TextBox _txtMz80aMonitor = new() { Width = 280 };
    private readonly TextBox _txtMz80aFont = new() { Width = 280 };
    private readonly TextBox _txtMz80aBasic = new() { Width = 280 };
    private readonly Label _lblMz80aMonitorStatus = new() { AutoSize = true };
    private readonly Label _lblMz80aFontStatus = new() { AutoSize = true };
    private readonly Label _lblMz80aBasicStatus = new() { AutoSize = true };

    // Joystick
    private readonly NumericUpDown _numButton1 = new() { Minimum = 0, Maximum = 31, Width = 60 };
    private readonly NumericUpDown _numButton2 = new() { Minimum = 0, Maximum = 31, Width = 60 };

    // Startup — Phase 5.3. DefaultMachine picker + boot-time debug
    // pane visibility. DefaultMachine is the persisted default; the
    // --mz700 / --mz80a CLI flag overrides at launch without touching
    // this value. Debug pane checkboxes for panes that don't apply to
    // the persisted DefaultMachine grey out with an explanatory tooltip
    // (their stored value survives — switch the default back and
    // they'll open again).
    private readonly RadioButton _rbDefaultMz700 = new() { Text = "MZ-&700", AutoSize = true };
    private readonly RadioButton _rbDefaultMz80a = new() { Text = "MZ-&80A", AutoSize = true };
    private readonly Label _lblCliOverrideHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly CheckBox _chkDebuggerAtStartup = new() { Text = "&Debugger (Ctrl+D)", AutoSize = true };
    private readonly CheckBox _chkMemoryViewerAtStartup = new() { Text = "&Memory Viewer (Ctrl+M)", AutoSize = true };
    private readonly CheckBox _chkHidDiagnosticAtStartup = new() { Text = "&HID Diagnostic (Ctrl+H)", AutoSize = true };
    private readonly CheckBox _chkFontSheetAtStartup = new() { Text = "&Font Sheet (Ctrl+G)", AutoSize = true };
    private readonly CheckBox _chkSoundDiagnosticAtStartup = new() { Text = "&Sound Diagnostic", AutoSize = true };
    private readonly CheckBox _chkKeyboardMatrixAtStartup = new() { Text = "&Keyboard Matrix", AutoSize = true };
    private ToolTip? _startupTooltips;

    // Pre-edit baseline captured when the dialog opens; diffed against
    // the live state at Apply / OK time so the user sees a summary of
    // exactly what's about to be persisted. Refreshed after a successful
    // Apply so a second Apply doesn't re-list the same changes.
    private SettingsSnapshot _baseline = null!;

    /// <summary>Raised after settings are written. MainForm uses this to
    /// reflect changes in the running emulator (display scale, joystick
    /// button bindings).</summary>
    public event Action? Applied;

    public SettingsForm(Settings settings, JoystickInput? joystickInput = null, MZ700? machine = null,
        Tab initialTab = Tab.Startup, MZ80A? mz80a = null)
    {
        _settings = settings;
        _joystickInput = joystickInput;
        _machine = machine;
        _mz80a = mz80a;
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
        tabs.TabPages.Add(BuildStartupTab());
        tabs.TabPages.Add(BuildRomsTab());
        tabs.TabPages.Add(BuildDisplayTab());
        tabs.TabPages.Add(BuildKeyboardTab());
        tabs.TabPages.Add(BuildJoystickTab());
        // Caller can deep-link to a specific tab via the Tab enum; clamp
        // defensively if a new tab is added but the enum value lags.
        int idx = (int)initialTab;
        if (idx >= 0 && idx < tabs.TabPages.Count) tabs.SelectedIndex = idx;

        var buttonRow = BuildButtonRow();

        // Phase 5.5 delivered full MZ-80A GUI coverage — the amber
        // MZ-80A partial-coverage banner that lived here is retired.
        // Every MZ-80A setting the banner used to point at (char-map
        // overrides, key overrides, green-screen toggle, InvertLetterShift)
        // now has a dialog surface.
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

    private TabPage BuildStartupTab()
    {
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            WrapContents = false,
        };

        // Default machine group.
        var machineGroup = new GroupBox
        {
            Text = "Default machine on startup",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 16, 8, 8),
            Margin = new Padding(0, 0, 0, 12),
        };
        var machineStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        machineStack.Controls.Add(_rbDefaultMz700);
        machineStack.Controls.Add(_rbDefaultMz80a);
        machineStack.Controls.Add(new Label
        {
            Text = "Overridable per-run with the --mz700 / --mz80a CLI flag (which does not rewrite this setting).",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 0),
        });
        // CLI-override hint — only visible when the current session's
        // machine differs from the persisted DefaultMachine, i.e. a CLI
        // flag is overriding right now.
        _lblCliOverrideHint.Margin = new Padding(0, 6, 0, 0);
        machineStack.Controls.Add(_lblCliOverrideHint);
        machineGroup.Controls.Add(machineStack);
        stack.Controls.Add(machineGroup);

        // Debug panes group.
        var panesGroup = new GroupBox
        {
            Text = "Debug panes on startup",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 16, 8, 8),
            Margin = new Padding(0),
        };
        var panesStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        panesStack.Controls.Add(_chkDebuggerAtStartup);
        panesStack.Controls.Add(_chkMemoryViewerAtStartup);
        panesStack.Controls.Add(_chkHidDiagnosticAtStartup);
        panesStack.Controls.Add(_chkFontSheetAtStartup);
        panesStack.Controls.Add(_chkSoundDiagnosticAtStartup);
        panesStack.Controls.Add(_chkKeyboardMatrixAtStartup);
        panesStack.Controls.Add(new Label
        {
            Text = "Panes that don't apply to the current DefaultMachine grey out; their stored setting still survives, so switching the default back restores them.",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 0),
        });
        panesGroup.Controls.Add(panesStack);
        stack.Controls.Add(panesGroup);

        // Tooltips explain the grey-out reasoning.
        _startupTooltips = new ToolTip();
        _startupTooltips.SetToolTip(_chkSoundDiagnosticAtStartup,
            "MZ-700 only. Won't open at boot when DefaultMachine=MZ-80A.");
        // Live grey-out on radio change.
        _rbDefaultMz700.CheckedChanged += (_, _) => RefreshDebugPaneEnabledState();
        _rbDefaultMz80a.CheckedChanged += (_, _) => RefreshDebugPaneEnabledState();

        return BuildTabPage("Startup", stack);
    }

    /// <summary>
    /// Grey out MZ-700-only debug panes when the DefaultMachine radio
    /// is set to MZ-80A. Stored values survive — disabling only masks
    /// the checkbox visually, doesn't alter its Checked state.
    /// </summary>
    private void RefreshDebugPaneEnabledState()
    {
        bool mz80aDefault = _rbDefaultMz80a.Checked;
        _chkSoundDiagnosticAtStartup.Enabled = !mz80aDefault;
    }

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
        stack.Controls.Add(new Label
        {
            Text = "Effects:",
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 6),
        });
        stack.Controls.Add(_chkScanlines);

        // MZ-80A group (Phase 5.2). Green phosphor tint lives here now
        // — the View → Green screen menu item retired in the same
        // commit. Live-applies when MZ-80A is active (see MainForm.
        // OnSettingsApplied); persists silently under the "(not
        // active)" tint when MZ-700 is running.
        var mz80aGroup = MakeMachineGroup("MZ-80A display", MachineScope.Mz80aOnly);
        mz80aGroup.AutoSize = true;
        mz80aGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        mz80aGroup.Margin = new Padding(0, 14, 0, 0);
        // GroupBox with AutoSize needs its child docked-top or laid out
        // by a FlowLayoutPanel so the group measures correctly. Use
        // the same FlowLayoutPanel-inside-GroupBox pattern.
        var mz80aStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        mz80aStack.Controls.Add(_chkMz80aGreenScreen);
        mz80aGroup.Controls.Add(mz80aStack);
        stack.Controls.Add(mz80aGroup);

        return BuildTabPage("Display", stack);
    }

    /// <summary>
    /// Which machine a group of settings applies to. Drives the group-box
    /// title suffix + heading tint per D1/D5 — group whose scope isn't
    /// currently active renders as "(not active)" in <see cref="SystemColors.GrayText"/>
    /// but its controls stay fully enabled (edits persist for next boot).
    /// </summary>
    private enum MachineScope { Shared, Mz700Only, Mz80aOnly }

    /// <summary>
    /// Build a <see cref="GroupBox"/> whose title + heading colour reflect
    /// the given <paramref name="scope"/> relative to the currently active
    /// machine. Caller docks / adds content into the returned group's
    /// Controls collection.
    /// </summary>
    private GroupBox MakeMachineGroup(string title, MachineScope scope)
    {
        bool active = scope switch
        {
            MachineScope.Mz700Only => _settings.CurrentMachine == MachineType.MZ700,
            MachineScope.Mz80aOnly => _settings.CurrentMachine == MachineType.MZ80A,
            _ => true,
        };
        string suffix = (scope != MachineScope.Shared && !active) ? " (not active)" : "";
        return new GroupBox
        {
            Text = title + suffix,
            Dock = DockStyle.Fill,
            // GroupBox title lands inside the top padding band — 16px
            // clears the text so first content row doesn't overlap.
            // Same pattern as the Known limitations group and the HID
            // Diagnostic pane's own group boxes.
            Padding = new Padding(8, 16, 8, 8),
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = active ? SystemColors.ControlText : SystemColors.GrayText,
        };
    }

    private TabPage BuildRomsTab()
    {
        // Two per-machine groups stacked vertically + a hint at the
        // bottom. Each group hosts a 4×3 grid: label / textbox / status
        // / browse button per ROM file (Monitor, Font, BASIC).
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // 3 ROM rows × 34 px = 102 px of content per group, plus
        // GroupBox chrome (16 px top-padding for title band + 8 px
        // bottom padding + a little breathing room). Explicit heights
        // are needed here — Dock=Fill on the GroupBox doesn't report a
        // natural size, so AutoSize rows collapse to just the visible
        // top and clip the BASIC row.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        root.Controls.Add(BuildRomGroup(
            MachineScope.Mz700Only, "MZ-700 ROMs",
            _txtMz700Monitor, _lblMz700MonitorStatus,
            "1z-013a.rom",  // monitor
            _txtMz700Font, _lblMz700FontStatus,
            "mz700fon.int",  // font
            "Font files (*.int;*.bin;*.txt)|*.int;*.bin;*.txt|All files|*.*",
            _txtMz700Basic, _lblMz700BasicStatus,
            "1Z-013B.mzf"), 0, 0);

        root.Controls.Add(BuildRomGroup(
            MachineScope.Mz80aOnly, "MZ-80A ROMs",
            _txtMz80aMonitor, _lblMz80aMonitorStatus,
            "SA-1510.rom",  // monitor
            _txtMz80aFont, _lblMz80aFontStatus,
            "SA-CG.rom",  // font
            "Font files (*.rom;*.bin)|*.rom;*.bin|All files|*.*",
            _txtMz80aBasic, _lblMz80aBasicStatus,
            "SA-5510.mzf"), 0, 1);

        var hint = new Label
        {
            Text = "Monitor/Font path changes take effect on next launch.\nBASIC path takes effect on next Load BASIC.\nBoth machines' paths are editable regardless of which is currently active.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
        };
        root.Controls.Add(hint, 0, 2);

        return BuildTabPage("ROMs", root);
    }

    private GroupBox BuildRomGroup(MachineScope scope, string title,
        TextBox monitor, Label monitorStatus, string monitorHint,
        TextBox font, Label fontStatus, string fontHint,
        string fontFilter,
        TextBox basic, Label basicStatus, string basicHint)
    {
        var group = MakeMachineGroup(title, scope);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

        AddRomRow(grid, 0, "Monitor ROM:", monitor, monitorStatus,
            $"Select monitor ROM ({monitorHint})", "ROM files (*.rom;*.bin)|*.rom;*.bin|All files|*.*");
        AddRomRow(grid, 1, "Font ROM:", font, fontStatus,
            $"Select character ROM ({fontHint})", fontFilter);
        AddRomRow(grid, 2, "BASIC:", basic, basicStatus,
            $"Select S-BASIC cassette ({basicHint})", "Cassette files (*.mzf;*.m12;*.mzt)|*.mzf;*.m12;*.mzt|All files|*.*");

        group.Controls.Add(grid);
        return group;
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
        //
        // MZ-80A group at row 4 (Phase 5.2) — the diagram + editor
        // above are MZ-700-only for now; Phase 5.5 will split them
        // into an "Edit MZ-700 key mappings…" button + a matching
        // MZ-80A button and formally scope the tab.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(8),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // caption
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210f));  // diagram
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // export / import row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // advanced settings button
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // MZ-80A group (Phase 5.2)
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

        // 5.5c: diagram reflects the currently-active machine. On MZ-700
        // the PC-key labels + unreachable-essential red outline come from
        // PcKeyIndex + MzKeyboardLayout.EssentialKeys; on MZ-80A neither
        // has an analogue yet, so the diagram renders label-less. That's
        // still a major improvement over pre-5.5c, where MZ-80A users saw
        // the MZ-700 diagram — extending PcKeyIndex to MZ-80A is v1.2
        // polish per Q3 defer.
        _kbdDiagram = new MzKeyboardDiagram(BuildActiveLayout()) { Dock = DockStyle.Fill };
        _kbdDiagram.KeyClicked += OnKeyboardDiagramKeyClicked;
        RefreshKeyboardDiagramLabels();
        layout.Controls.Add(_kbdDiagram, 0, 1);

        // Export / Import row — .mzkbd file format is MZ-700-only for
        // now. Extending it to carry MZ-80A entries is a separate scope
        // (deferred). Hide entirely on MZ-80A rather than showing buttons
        // that operate on the wrong machine's overrides.
        var ioButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 4),
            Visible = _machine != null,
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

        // MZ-80A group (Phase 5.2) — currently just the InvertLetterShift
        // checkbox. Phase 5.5 extends this into the MZ-80A key mapping
        // editor and formally scopes the MZ-700 content above.
        var mz80aGroup = MakeMachineGroup("MZ-80A keyboard", MachineScope.Mz80aOnly);
        mz80aGroup.AutoSize = true;
        mz80aGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        mz80aGroup.Margin = new Padding(0, 8, 0, 0);
        var mz80aStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        mz80aStack.Controls.Add(_chkMz80aInvertLetterShift);
        mz80aGroup.Controls.Add(mz80aStack);
        layout.Controls.Add(mz80aGroup, 0, 4);

        layout.Controls.Add(BuildKeyboardLimitationsPanel(), 0, 5);

        return BuildTabPage("Keyboard", layout);
    }

    private const string KeyboardDocUrl =
        "https://github.com/sgillon/MZRaku/blob/main/docs/usage/keyboard.md";

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
        var context = TryBuildActiveEditorContext();
        if (context == null) return;
        using var dlg = new AdvancedKeyboardForm(context);
        dlg.ShowDialog(this);
        // Edits flow into the shared override instances; refresh the
        // diagram so any changes made via the matrix grid show through.
        RefreshKeyboardDiagramLabels();
    }

    /// <summary>
    /// Returns an editor context for the currently-active machine, or
    /// null if neither <see cref="_machine"/> nor <see cref="_mz80a"/>
    /// is populated (shouldn't happen in normal use — the host always
    /// runs one of them). Same pattern for OpenAdvancedKeyboard,
    /// OnKeyboardDiagramKeyClicked, and any future keyboard entry point.
    /// </summary>
    private IKeyboardEditorContext? TryBuildActiveEditorContext() =>
        _machine != null ? new Mz700KeyboardEditorContext(_machine, _settings.CharMapOverrides, _settings.KeyOverrides) :
        _mz80a   != null ? new Mz80aKeyboardEditorContext(_mz80a,  _settings.Mz80aCharMapOverrides, _settings.Mz80aKeyOverrides) :
        null;

    /// <summary>
    /// Physical-keyboard-layout for the currently-active machine.
    /// Used by the Keyboard-tab diagram construction. MZ-700 falls
    /// through as the default so a host that supplies neither machine
    /// (e.g. Settings opened from a broken state) still renders
    /// something rather than throwing.
    /// </summary>
    private IPhysicalKeyboardLayout BuildActiveLayout() =>
        _mz80a != null ? new Mz80aPhysicalKeyboardLayout()
                       : new Mz700PhysicalKeyboardLayout();

    private MachineType ActiveMachine =>
        _mz80a != null ? MachineType.MZ80A : MachineType.MZ700;

    private void OnExportMzKbd() =>
        MzKbdIoCoordinator.PromptAndExport(this, _settings, ActiveMachine);

    private void OnImportMzKbd() =>
        MzKbdIoCoordinator.PromptAndImport(this, _settings, ActiveMachine,
            onImportApplied: RefreshKeyboardDiagramLabels);

    private void OnKeyboardDiagramKeyClicked(object? sender, KeyDiagramClickedEventArgs e)
    {
        var context = TryBuildActiveEditorContext();
        if (context == null) return;

        // MZ Shift is permanently wired to PC Shift via the Keyboard
        // modifier path (concurrent assertion is needed so Shift+1 → '!'
        // produces the character bit and the shift bit simultaneously).
        // Surfacing the editor would imply it's rebindable; explain instead.
        if (e.Key.Row == context.ShiftSlot.Row && e.Key.Col == context.ShiftSlot.Col)
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
        using var editor = new MzKeyEditorForm(e.Key, context);
        editor.ShowDialog(this);
        RefreshKeyboardDiagramLabels();
    }

    private void RefreshKeyboardDiagramLabels()
    {
        if (_kbdDiagram == null) return;

        // Both machines route through the same parameterised PcKeyIndex
        // (v1.2 audit F-036). Null context = neither machine held,
        // shouldn't happen in normal use — clear labels defensively.
        var context = TryBuildActiveEditorContext();
        if (context == null)
        {
            _kbdDiagram.PcKeyLabels = null;
            _kbdDiagram.UnreachableKeyIds = null;
            _kbdDiagram.RefreshLabels();
            return;
        }

        _kbdDiagram.PcKeyLabels = PcKeyIndex.BuildLabelsByMzKey(context.LayoutKeys, context);

        // Recompute the unreachable-essential set so the red outline on
        // affected caps tracks live with edits — Apply's safety gate
        // reads the same set, so what you see on the diagram before
        // Apply is what the confirm dialog will mention.
        var slotShiftLabels = PcKeyIndex.BuildLabelsBySlotShift(context);
        var unreachable = new HashSet<string>();
        foreach (var k in context.EssentialLayoutKeys)
        {
            if (!KeyboardReachability.IsKeyFullyReachable(k, slotShiftLabels, context))
                unreachable.Add(k.Id);
        }
        _kbdDiagram.UnreachableKeyIds = unreachable.Count > 0 ? unreachable : null;

        _kbdDiagram.RefreshLabels();
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
        // Startup — DefaultMachine radio + CLI-override hint.
        if (_settings.DefaultMachine == MachineType.MZ80A)
            _rbDefaultMz80a.Checked = true;
        else
            _rbDefaultMz700.Checked = true;
        if (_settings.CurrentMachine != _settings.DefaultMachine)
        {
            var flag = _settings.CurrentMachine == MachineType.MZ700 ? "--mz700" : "--mz80a";
            var name = _settings.CurrentMachine == MachineType.MZ700 ? "MZ-700" : "MZ-80A";
            _lblCliOverrideHint.Text = $"Current session: {name} (via {flag} CLI flag).";
            _lblCliOverrideHint.Visible = true;
        }
        else
        {
            _lblCliOverrideHint.Visible = false;
        }
        var dp = _settings.DebugPanesAtStartup;
        _chkDebuggerAtStartup.Checked = dp.Debugger;
        _chkMemoryViewerAtStartup.Checked = dp.MemoryViewer;
        _chkHidDiagnosticAtStartup.Checked = dp.HidDiagnostic;
        _chkFontSheetAtStartup.Checked = dp.FontSheet;
        _chkSoundDiagnosticAtStartup.Checked = dp.SoundDiagnostic;
        _chkKeyboardMatrixAtStartup.Checked = dp.KeyboardMatrix;
        RefreshDebugPaneEnabledState();

        switch (_settings.DisplayScale)
        {
            case 1: _rb1x.Checked = true; break;
            case 3: _rb3x.Checked = true; break;
            default: _rb2x.Checked = true; break;
        }
        _chkScanlines.Checked = _settings.DisplayScanlines;
        _chkMz80aGreenScreen.Checked = _settings.Mz80aGreenScreen;
        _chkMz80aInvertLetterShift.Checked = _settings.Mz80aInvertLetterShift;
        _txtMz700Monitor.Text = _settings.Mz700Roms.MonitorRomPath;
        _txtMz700Font.Text = _settings.Mz700Roms.FontPath;
        _txtMz700Basic.Text = _settings.Mz700Roms.BasicPath;
        _txtMz80aMonitor.Text = _settings.Mz80aRoms.MonitorRomPath;
        _txtMz80aFont.Text = _settings.Mz80aRoms.FontPath;
        _txtMz80aBasic.Text = _settings.Mz80aRoms.BasicPath;
        _numButton1.Value = Math.Clamp(_settings.JoyButton1Index, 0, 31);
        _numButton2.Value = Math.Clamp(_settings.JoyButton2Index, 0, 31);
        RefreshAllRomStatus();
    }

    private void ApplyChanges()
    {
        // Startup / display / ROMs / joystick all live on the snapshot;
        // overrides are already live-mutated by the per-key editor flow
        // so the snapshot's char/key sections stay purely diagnostic.
        var snap = CaptureDialogSnapshot();
        snap.ApplyTo(_settings);
        _settings.Save();
        Applied?.Invoke();

        // Reset baseline so a follow-up Apply only summarises further
        // edits, not the ones the user just confirmed.
        _baseline = SettingsSnapshot.Capture(_settings);
    }

    /// <summary>
    /// Snapshot of every scalar the dialog currently shows plus the
    /// live keyboard override stores. Reads controls for scalars (the
    /// dialog hasn't pushed them into <see cref="_settings"/> yet)
    /// and the live <see cref="_settings"/> stores for the four
    /// override layers (both machines' char + VK maps — the per-key
    /// editor flow has already written whatever the user did there).
    /// </summary>
    private SettingsSnapshot CaptureDialogSnapshot()
    {
        var baseSnap = SettingsSnapshot.Capture(_settings);
        return new SettingsSnapshot
        {
            DefaultMachine = _rbDefaultMz80a.Checked ? MachineType.MZ80A : MachineType.MZ700,
            PaneDebugger = _chkDebuggerAtStartup.Checked,
            PaneMemoryViewer = _chkMemoryViewerAtStartup.Checked,
            PaneHidDiagnostic = _chkHidDiagnosticAtStartup.Checked,
            PaneFontSheet = _chkFontSheetAtStartup.Checked,
            PaneSoundDiagnostic = _chkSoundDiagnosticAtStartup.Checked,
            PaneKeyboardMatrix = _chkKeyboardMatrixAtStartup.Checked,
            DisplayScale = _rb3x.Checked ? 3 : _rb1x.Checked ? 1 : 2,
            DisplayScanlines = _chkScanlines.Checked,
            Mz80aGreenScreen = _chkMz80aGreenScreen.Checked,
            Mz80aInvertLetterShift = _chkMz80aInvertLetterShift.Checked,
            Mz700MonitorPath = _txtMz700Monitor.Text.Trim(),
            Mz700FontPath = _txtMz700Font.Text.Trim(),
            Mz700BasicPath = _txtMz700Basic.Text.Trim(),
            Mz80aMonitorPath = _txtMz80aMonitor.Text.Trim(),
            Mz80aFontPath = _txtMz80aFont.Text.Trim(),
            Mz80aBasicPath = _txtMz80aBasic.Text.Trim(),
            JoyButton1Index = (int)_numButton1.Value,
            JoyButton2Index = (int)_numButton2.Value,
            CharOverrides = baseSnap.CharOverrides,
            SuppressedChars = baseSnap.SuppressedChars,
            KeyOverrides = baseSnap.KeyOverrides,
            Mz80aCharOverrides = baseSnap.Mz80aCharOverrides,
            Mz80aSuppressedChars = baseSnap.Mz80aSuppressedChars,
            Mz80aKeyOverrides = baseSnap.Mz80aKeyOverrides,
        };
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
        var candidate = CaptureDialogSnapshot();
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

        // Walk the active machine's layout keys — MZ-700 = MzKeyboardLayout,
        // MZ-80A = Mz80aKeyboardLayout — via the editor context.
        var context = TryBuildActiveEditorContext();
        if (context == null) return true;
        var unreachable = context.LayoutKeys
            .Where(k => unreachableIds.Contains(k.Id))
            .ToList();

        // Pull the user's attention to the diagram so the red outlines
        // and the dialog text describe the same keys.
        if (_kbdDiagram?.Parent is TabPage page && page.Parent is TabControl tabs)
            tabs.SelectedTab = page;

        const int previewMax = 10;
        var names = string.Join(", ", unreachable.Take(previewMax).Select(k => KeyboardReachability.DescribeKeyForGate(k, context)));
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

    // -- ROM browse + path-status indicator -----------------------------

    // Every ROM textbox + its status label, in the same order the tab
    // displays them. WireValidation and RefreshAllRomStatus fan out over
    // this list — add a new row here rather than editing both methods.
    private (TextBox Box, Label Status)[] RomRows() => new[]
    {
        (_txtMz700Monitor, _lblMz700MonitorStatus),
        (_txtMz700Font,    _lblMz700FontStatus),
        (_txtMz700Basic,   _lblMz700BasicStatus),
        (_txtMz80aMonitor, _lblMz80aMonitorStatus),
        (_txtMz80aFont,    _lblMz80aFontStatus),
        (_txtMz80aBasic,   _lblMz80aBasicStatus),
    };

    private void WireValidation()
    {
        foreach (var (box, status) in RomRows())
            box.TextChanged += (_, _) => UpdateRomStatus(box, status);
    }

    private void RefreshAllRomStatus()
    {
        foreach (var (box, status) in RomRows())
            UpdateRomStatus(box, status);
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
        var resolved = Settings.Resolve(path);
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
            var resolved = Settings.Resolve(current);
            if (File.Exists(resolved))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(resolved);
                dlg.FileName = Path.GetFileName(resolved);
            }
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        target.Text = Settings.MakeStorable(dlg.FileName);
    }
}
