using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

public sealed class MainForm : Form
{
    // Exactly one of these is populated at construction based on
    // Settings.Type. MZ-700-specific accesses (Sound, Ppi, Pit,
    // Joystick, Cassette, Keyboard, Video, KeyTables) use _machine!.
    // and are gated by _machine != null. IMachine-surface calls
    // (Cpu, Mem, RunFrame, Reset, LoadRoms, AutoLoad*, debugger
    // controls) use the Active property, which works for both.
    private readonly MZ700? _machine;
    private readonly MZ80A? _mz80a;
    private IMachine Active => (IMachine?)_machine ?? _mz80a!;
    private readonly Hardware.JoystickInput _joystickInput;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly StatusStrip _status = new();
    // Left pane: persistent machine identity (MZ-700 / MZ-80A). Fixed
    // width so its position stays put across status changes.
    private readonly ToolStripStatusLabel _machineLabel = new()
    {
        Spring = false,
        AutoSize = false,
        Width = 72,
        TextAlign = ContentAlignment.MiddleCenter,
        BorderSides = ToolStripStatusLabelBorderSides.Right,
        BorderStyle = Border3DStyle.Etched,
    };
    // Middle pane: transient status messages. Springs to fill the
    // remaining strip width; centered so the message doesn't jump
    // horizontally as it changes length.
    private readonly ToolStripStatusLabel _statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleCenter,
    };
    // Right pane, LEFT of the mode indicator: TAPE activity chip.
    // Idle (no cassette queued) → greyed; Steady (queued, awaiting
    // trap) → pale yellow; Flash (trap fired within the last
    // TapeFlashFrames frames) → white on orange. Updated per frame
    // from Timer_Tick via UpdateTapeLabel.
    private readonly ToolStripStatusLabel _tapeLabel = new()
    {
        Spring = false,
        Text = "TAPE",
        AutoSize = false,
        Width = 56,
        TextAlign = ContentAlignment.MiddleCenter,
        BorderSides = ToolStripStatusLabelBorderSides.Left,
        BorderStyle = Border3DStyle.Etched,
        ForeColor = SystemColors.GrayText,
    };
    // Right pane: ALPHA/GRAPH mode indicator. Fixed width; classic
    // sunken separator on the left so it reads as a distinct pane.
    private readonly ToolStripStatusLabel _modeLabel = new()
    {
        Spring = false,
        Text = "ALPHA",
        AutoSize = false,
        Width = 56,
        TextAlign = ContentAlignment.MiddleCenter,
        BorderSides = ToolStripStatusLabelBorderSides.Left,
        BorderStyle = Border3DStyle.Etched,
    };
    // Frame at which _statusLabel last received a non-empty message.
    // The message auto-clears after StatusIdleFrames so the middle
    // pane stays empty when nothing's happening. Populated via the
    // _statusLabel.TextChanged hook, so every direct
    // `_statusLabel.Text = "…"` assignment sitewide participates
    // without needing a helper wrapper.
    private int _statusLastSetFrame = -1;
    private const int StatusIdleFrames = 300;   // ~5 s at 60 Hz
    private string MachineLabel => _mz80a != null ? "MZ-80A" : "MZ-700";
    private readonly PictureBox _display = new();
    private readonly Settings _settings = Settings.Load();
    private readonly ToolStripMenuItem[] _scaleMenuItems = new ToolStripMenuItem[3];
    private readonly string? _initialCassette;
    private readonly bool _autoLoadBasic;
    private readonly string? _dumpPath;
    private bool _started;
    private bool _pendingLoadBasic;
    private string? _pendingCassette;
    private string? _pendingBasicSource;
    private DebuggerForm? _debugger;
    private MemoryViewerForm? _memViewer;
    private HidDiagnosticForm? _hidDiag;
    private SoundDiagnosticForm? _soundDiag;
    private FontSheetForm? _fontSheet;

    // Full-screen state. _isFullScreen tracks the toggle; the other
    // fields capture the windowed-mode chrome so ExitFullScreen can
    // restore exactly what was there. Alt+Enter toggles via
    // ProcessCmdKey, the View → Full-screen menu item is the
    // discovery affordance.
    private bool _isFullScreen;
    private FormBorderStyle _preFullScreenBorderStyle;
    private FormWindowState _preFullScreenWindowState;
    private Rectangle _preFullScreenBounds;
    private ToolStripMenuItem? _fullScreenMenuItem;
    private ToolStripMenuItem? _scanlinesMenuItem;
    // Phase 5.2 retired the standalone View → Green screen menu item;
    // the toggle now lives entirely in Settings → Display → MZ-80A
    // display group. ApplyMz80aScreenColor() below is still the single
    // apply-point, called from initial construction + OnSettingsApplied.
    private ToolStripMenuItem? _pauseMenuItem;
    private readonly bool _startFullScreen;
    // Captures the pre-override scanlines value when --scanlines was
    // passed on the CLI. The natural-close FormClosing handler restores
    // it before Save so a per-session override doesn't sneak into
    // settings.ini. Cleared the moment the user explicitly toggles
    // (View → Scanlines, Ctrl+L) — that's now their persisted choice.
    private bool? _scanlinesRestoreOnClose;
    // Tracks the previous GRAPH-mode bit so we can detect ALPHA→GRAPH
    // transitions and auto-surface the Font Sheet — the only way to
    // reach MZ-only graphic glyphs (no PC-key equivalent exists). Opens
    // unconditionally on each transition (if not already visible) since
    // GRAPH mode is effectively unusable without it.
    private bool _wasGraphMode;
    private KeyboardMatrixForm? _matrixForm;

    public MainForm(string? cassettePath, bool autoLoadBasic, string? dumpPath = null,
        int? displayScaleOverride = null, bool startFullScreen = false,
        bool? scanlinesOverride = null, MachineType? machineOverride = null)
    {
        _initialCassette = cassettePath;
        _autoLoadBasic = autoLoadBasic;
        _dumpPath = dumpPath;
        _startFullScreen = startFullScreen;
        // CLI --scanlines on/off wins for this session. Snapshot the
        // persisted value so the natural-close path can restore it.
        if (scanlinesOverride.HasValue)
        {
            _scanlinesRestoreOnClose = _settings.DisplayScanlines;
            _settings.DisplayScanlines = scanlinesOverride.Value;
        }
        // Machine selection: --mz700/--mz80a CLI wins for this run
        // over the persisted [Machine] Type. Not written back to
        // settings.ini (the File → Machine menu is the persist path).
        if (machineOverride.HasValue) _settings.Type = machineOverride.Value;
        // EnsureRomPaths scans for both machines' ROMs, so the CLI
        // override finding e.g. SA-1510.rom under roms/ works even
        // when settings.ini had never seen an MZ-80A launch before.
        // No-op if the previous Load() already populated both sides.
        if (_settings.EnsureRomPaths()) _settings.Save();
        // Construct exactly one of the two machines.
        if (_settings.Type == MachineType.MZ700)
            _machine = new MZ700();
        else
        {
            _mz80a = new MZ80A();
            _mz80a.Keyboard.InvertLetterShift = _settings.Mz80aInvertLetterShift;
            ApplyMz80aScreenColor();
        }

        // JoystickInput needs a non-null Joystick reference to
        // construct even when the machine has no joystick (MZ-80A).
        // A fresh throwaway Joystick keeps the field non-null; nothing
        // ever pumps its bits, and Timer_Tick's Poll() is guarded so
        // the reads go nowhere. Cheap — Joystick is a small POCO.
        var joystickForInput = _machine?.Joystick ?? new Hardware.Joystick();
        _joystickInput = new Hardware.JoystickInput(joystickForInput);
        _joystickInput.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
        // Keyboard-override wiring. Both machines have identical
        // 10-strobe × 8-bit matrix topology so KeyOverride is shared;
        // Settings persists two independent instances at
        // [KeyOverrides.MZ700] / [KeyOverrides.MZ80A] (Phase 5.1a).
        // Char-map layers are split too: CharMap for MZ-700,
        // Mz80aCharMap for MZ-80A.
        if (_machine != null)
        {
            _machine!.Keyboard.Overrides = _settings.KeyOverrides;
            CharMap.Overrides = _settings.CharMapOverrides;
        }
        if (_mz80a != null)
        {
            _mz80a.Keyboard.Overrides = _settings.Mz80aKeyOverrides;
        }
        Mz80aCharMap.Overrides = _settings.Mz80aCharMapOverrides;

        Text = TitleBase;
        Icon = LoadEmbeddedIcon();
        KeyPreview = true;
        AllowDrop = true;
        DoubleBuffered = true;
        // Saved geometry wins, else center on the screen. Size is set by
        // ApplyDisplayScale further down — only X / Y are restored here.
        if (_settings.MainWindow.HasGeometry)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(_settings.MainWindow.X, _settings.MainWindow.Y);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
        FormBorderStyle = FormBorderStyle.Sizable;

        BuildMenu();

        _display.Dock = DockStyle.Fill;
        _display.SizeMode = PictureBoxSizeMode.Zoom;
        _display.BackColor = Color.Black;
        _display.Paint += Display_Paint;
        _display.AllowDrop = true;
        _display.DragEnter += OnDragEnter;
        _display.DragDrop += OnDragDrop;
        // PictureBox isn't a tab-focus target; keep keyboard input on the
        // form (which has KeyPreview = true).
        _display.TabStop = false;

        // Four-pane layout: machine identity (left, fixed) | transient
        // status (middle, spring, centered) | TAPE activity chip |
        // ALPHA/GRAPH mode indicator (right, fixed). Order matters —
        // leftmost item first.
        _machineLabel.Text = MachineLabel;
        _status.Items.Add(_machineLabel);
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_tapeLabel);
        _status.Items.Add(_modeLabel);

        // SAVE-tape trap surfaces save outcomes via this event. Fires on
        // the UI thread (OnPreStep is called from Timer_Tick → RunFrame),
        // so we can touch the status label directly. MZ-700 only for
        // Phase 1 — MZ-80A cassette lands in Phase 4.
        if (_machine != null)
            _machine!.Cassette.OnSaved += msg => _statusLabel.Text = msg;

        // Every direct assignment to _statusLabel.Text starts the
        // idle-clear timer, so every call site sitewide participates
        // without needing a helper wrapper. Empty-string assignments
        // (our own idle revert) mark the tracker as -1 to stop
        // retriggering.
        _statusLabel.TextChanged += (_, _) =>
        {
            _statusLastSetFrame =
                string.IsNullOrEmpty(_statusLabel.Text) ? -1 : _bootFrames;
        };
        // Docking order matters: the Fill control must be added LAST so the
        // menu (top) and status strip (bottom) claim their space first.
        Controls.Add(_status);
        Controls.Add(_display);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += OnKeyDown;
        KeyPress += OnKeyPress;
        KeyUp += OnKeyUp;

        // Size last, after menu + status strip are docked so their heights
        // are known. Sets ClientSize so the video area is exactly N× native.
        // --display=N from the CLI wins over the persisted scale but isn't
        // saved back to settings.ini (per-session override).
        int initialScale = displayScaleOverride ?? _settings.DisplayScale;
        ApplyDisplayScale(initialScale, persist: !displayScaleOverride.HasValue);

        _timer.Interval = 1000 / MZ700.FramesPerSecond;
        _timer.Tick += Timer_Tick;

        Shown += (_, _) =>
        {
            // Drag focus to the form, including when launched from a
            // console host (cmd.exe / PowerShell). By the time Shown
            // fires the launching shell's foreground-rights grant has
            // already expired, so SetForegroundWindow alone is silently
            // ignored by Windows' focus-stealing-prevention.
            //
            // The keybd_event(VK_MENU) trick simulates a press+release
            // of the Alt key, which temporarily lifts the lock. It's a
            // hack but is the standard documented workaround.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(Handle);
            Activate();
            Focus();
            Start();

            // --display=full from the CLI flips into borderless mode
            // after the windowed form has settled (Shown fires after
            // the first paint), so the saved windowed bounds are still
            // valid for restoring later via Alt+Enter.
            if (_startFullScreen) EnterFullScreen();
        };
        FormClosing += (_, _) =>
        {
            // Snapshot main-window geometry to Settings before the child
            // windows tear down so the next launch comes up where the
            // user left it.
            _settings.MainWindow = new Settings.WindowState(
                Location.X, Location.Y, ClientSize.Width, ClientSize.Height);
            // Restore the pre-override scanlines value if --scanlines was
            // used on the CLI and the user never explicitly toggled.
            if (_scanlinesRestoreOnClose.HasValue)
                _settings.DisplayScanlines = _scanlinesRestoreOnClose.Value;
            _settings.Save();

            _timer.Stop();
            _debugger?.Dispose();
            _memViewer?.Dispose();
            _hidDiag?.Dispose();
            _soundDiag?.Dispose();
            _machine?.Sound.Dispose();
            _mz80a?.Sound.Dispose();
        };
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Load cassette...", null, (_, _) => BrowseAndLoad()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("Load &BASIC", null, (_, _) => LoadBasic()) { ShortcutKeys = Keys.Control | Keys.B });
        file.DropDownItems.Add(new ToolStripMenuItem("Load BASIC &source...", null, (_, _) => BrowseAndLoadBasicSource()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.B });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Reset", null, (_, _) => ResetMachine()) { ShortcutKeys = Keys.Control | Keys.R });
        file.DropDownItems.Add(new ToolStripSeparator());
        // Settings: parent expands a submenu of the five tabs. Ctrl+S
        // opens the dialog on the first tab (Startup); the other tabs
        // each get a Ctrl+Shift+letter shortcut so they can be reached
        // without a menu walk. The pattern is consistent: bare Ctrl+S
        // = first tab, Ctrl+Shift+letter = any other tab.
        var settings = new ToolStripMenuItem("&Settings");
        settings.DropDownItems.Add(new ToolStripMenuItem("S&tartup…", null,
            (_, _) => OpenSettings(SettingsForm.Tab.Startup))
            { ShortcutKeys = Keys.Control | Keys.S });
        settings.DropDownItems.Add(new ToolStripMenuItem("&ROMs…", null,
            (_, _) => OpenSettings(SettingsForm.Tab.Roms))
            { ShortcutKeys = Keys.Control | Keys.Shift | Keys.R });
        settings.DropDownItems.Add(new ToolStripMenuItem("&Display…", null,
            (_, _) => OpenSettings(SettingsForm.Tab.Display))
            { ShortcutKeys = Keys.Control | Keys.Shift | Keys.D });
        settings.DropDownItems.Add(new ToolStripMenuItem("&Keyboard…", null,
            (_, _) => OpenSettings(SettingsForm.Tab.Keyboard))
            { ShortcutKeys = Keys.Control | Keys.Shift | Keys.K });
        settings.DropDownItems.Add(new ToolStripMenuItem("&Joystick…", null,
            (_, _) => OpenSettings(SettingsForm.Tab.Joystick))
            { ShortcutKeys = Keys.Control | Keys.Shift | Keys.J });
        file.DropDownItems.Add(settings);
        file.DropDownItems.Add(new ToolStripSeparator());
        // Machine selection — radio pair. Selecting the non-active
        // machine writes settings.ini and prompts a restart; no live
        // switch (the debugger, memory viewer, sound diagnostic all
        // hold references to the current _machine, so tearing it
        // down mid-run is messier than an application restart).
        var machine = new ToolStripMenuItem("&Machine");
        var mz700Item = new ToolStripMenuItem("MZ-&700")
        {
            Checked = _settings.Type == MachineType.MZ700,
        };
        var mz80aItem = new ToolStripMenuItem("MZ-&80A")
        {
            Checked = _settings.Type == MachineType.MZ80A,
        };
        mz700Item.Click += (_, _) => SwitchMachine(MachineType.MZ700);
        mz80aItem.Click += (_, _) => SwitchMachine(MachineType.MZ80A);
        machine.DropDownItems.Add(mz700Item);
        machine.DropDownItems.Add(mz80aItem);
        file.DropDownItems.Add(machine);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        menu.Items.Add(file);

        var view = new ToolStripMenuItem("&View");
        for (int i = 0; i < 3; i++)
        {
            int scale = i + 1;
            var item = new ToolStripMenuItem($"&{scale}× ({VideoRenderer.PixelWidth * scale}×{VideoRenderer.PixelHeight * scale})",
                null, (_, _) => ApplyDisplayScale(scale))
            {
                ShortcutKeys = Keys.Control | (Keys.D0 + scale),
            };
            _scaleMenuItems[i] = item;
            view.DropDownItems.Add(item);
        }
        view.DropDownItems.Add(new ToolStripSeparator());
        // Full-screen toggle. Alt+Enter is captured in ProcessCmdKey
        // so Windows doesn't intercept it for the system menu; the
        // menu entry shows the shortcut text as a discovery hint.
        _fullScreenMenuItem = new ToolStripMenuItem("&Full-screen", null, (_, _) => ToggleFullScreen())
        {
            ShortcutKeyDisplayString = "Alt+Enter",
        };
        view.DropDownItems.Add(_fullScreenMenuItem);
        _scanlinesMenuItem = new ToolStripMenuItem("Scan&lines", null, (_, _) => ToggleScanlines())
        {
            ShortcutKeys = Keys.Control | Keys.L,
            Checked = _settings.DisplayScanlines,
        };
        view.DropDownItems.Add(_scanlinesMenuItem);
        view.DropDownItems.Add(new ToolStripSeparator());
        // Font Sheet — always available from View for click-to-type
        // glyph access. Also auto-surfaces in the Timer_Tick handler
        // on ALPHA→GRAPH transitions, since graphic glyphs have no
        // PC-key equivalent.
        view.DropDownItems.Add(new ToolStripMenuItem("&Font Sheet…", null, (_, _) => OpenFontSheet())
        {
            ShortcutKeys = Keys.Control | Keys.G,
        });
        menu.Items.Add(view);

        var debug = new ToolStripMenuItem("&Debug");
        // Pause / resume: global shortcut for the same Active.Pause /
        // Active.Resume toggle the Debugger's pause button drives.
        // Pause and Scroll Lock are both caught in ProcessCmdKey (menu
        // ShortcutKeys can't take a bare non-modifier key); bind both
        // so keyboards without a physical Pause key (e.g. Logitech
        // MX Keys) can still reach it.
        _pauseMenuItem = new ToolStripMenuItem("&Pause emulator", null, (_, _) => TogglePause())
        {
            ShortcutKeyDisplayString = "Pause / ScrLk",
            Checked = Active.Paused,
        };
        debug.DropDownItems.Add(_pauseMenuItem);
        debug.DropDownItems.Add(new ToolStripSeparator());
        debug.DropDownItems.Add(new ToolStripMenuItem("&Debugger…", null, (_, _) => OpenDebugger()) { ShortcutKeys = Keys.Control | Keys.D });
        debug.DropDownItems.Add(new ToolStripMenuItem("&Memory Viewer…", null, (_, _) => OpenMemoryViewer()) { ShortcutKeys = Keys.Control | Keys.M });
        debug.DropDownItems.Add(new ToolStripMenuItem("&HID Diagnostic…", null, (_, _) => OpenHidDiag()) { ShortcutKeys = Keys.Control | Keys.H });
        debug.DropDownItems.Add(new ToolStripMenuItem("&Sound Diagnostic…", null, (_, _) => OpenSoundDiag()));
        debug.DropDownItems.Add(new ToolStripMenuItem("Keyboard &Matrix…", null, (_, _) => OpenKeyboardMatrix()));
        menu.Items.Add(debug);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About…", null, (_, _) =>
        {
            using var dlg = new AboutForm(_settings.Type);
            dlg.ShowDialog(this);
        }));
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    // -- Full-screen ----------------------------------------------------

    private void ToggleFullScreen()
    {
        if (_isFullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen) return;
        _preFullScreenBorderStyle = FormBorderStyle;
        _preFullScreenWindowState = WindowState;
        _preFullScreenBounds = Bounds;

        if (MainMenuStrip != null) MainMenuStrip.Visible = false;
        _status.Visible = false;
        FormBorderStyle = FormBorderStyle.None;
        // Restore to Normal first; Maximized + None gives a maximized
        // borderless window that fills the working area only — we want
        // the whole screen including the taskbar area for true full-
        // screen, so set Bounds explicitly.
        WindowState = FormWindowState.Normal;
        Bounds = Screen.FromControl(this).Bounds;

        _isFullScreen = true;
        if (_fullScreenMenuItem != null) _fullScreenMenuItem.Checked = true;
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen) return;
        FormBorderStyle = _preFullScreenBorderStyle;
        WindowState = _preFullScreenWindowState;
        if (_preFullScreenWindowState == FormWindowState.Normal)
            Bounds = _preFullScreenBounds;
        if (MainMenuStrip != null) MainMenuStrip.Visible = true;
        _status.Visible = true;

        _isFullScreen = false;
        if (_fullScreenMenuItem != null) _fullScreenMenuItem.Checked = false;
    }

    private void ToggleScanlines()
    {
        _settings.DisplayScanlines = !_settings.DisplayScanlines;
        // User has now explicitly chosen — any CLI override no longer
        // needs to be restored on close.
        _scanlinesRestoreOnClose = null;
        _settings.Save();
        if (_scanlinesMenuItem != null) _scanlinesMenuItem.Checked = _settings.DisplayScanlines;
        _display.Invalidate();
    }

    private void ApplyMz80aScreenColor()
    {
        if (_mz80a == null) return;
        if (_settings.Mz80aGreenScreen)
        {
            _mz80a.Video.ForegroundArgb = unchecked((int)0xFF00FF00);
            _mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
        else
        {
            _mz80a.Video.ForegroundArgb = unchecked((int)0xFFFFFFFF);
            _mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Alt+Enter is the full-screen toggle. Catching it here keeps it
        // out of Windows' own Alt-handling (which would otherwise pop
        // the system menu or eat the keystroke).
        if (keyData == (Keys.Alt | Keys.Enter))
        {
            ToggleFullScreen();
            return true;
        }
        // Alt+F4 must close the emulator. F4 alone is wired to MZ F4 via
        // SpecialKeyMap so the normal OnKeyDown path marks it as handled
        // — that would swallow Alt+F4 too and the user gets no way out
        // of full-screen short of Task Manager. Catch it here, ahead of
        // the matrix path, so Close() fires (which honours FormClosing
        // and saves window state).
        if (keyData == (Keys.Alt | Keys.F4))
        {
            Close();
            return true;
        }
        // Pause / Scroll Lock: global pause toggle. Both bound because
        // some modern keyboards (Logitech MX Keys, most laptops) drop
        // one or the other. Caught here rather than via OnKeyDown so
        // the input doesn't pass through the MZ matrix layer — though
        // neither key is currently mapped to any MZ slot anyway.
        if (keyData == Keys.Pause || keyData == Keys.Scroll)
        {
            TogglePause();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Flip Active.Paused. Menu check mark, title-bar suffix, and
    /// display dim overlay all follow via RefreshPauseIndicator on
    /// the next frame.
    /// </summary>
    private void TogglePause()
    {
        if (Active.Paused) Active.Resume();
        else Active.Pause();
    }

    private void ApplyDisplayScale(int scale, bool persist = true)
    {
        if (scale < 1) scale = 1;
        if (scale > 3) scale = 3;
        int chrome = (MainMenuStrip?.Height ?? 0) + _status.Height;
        ClientSize = new Size(
            VideoRenderer.PixelWidth * scale,
            VideoRenderer.PixelHeight * scale + chrome);
        for (int i = 0; i < _scaleMenuItems.Length; i++)
        {
            if (_scaleMenuItems[i] != null)
                _scaleMenuItems[i].Checked = (i + 1 == scale);
        }
        // CLI --display=N overrides for this session without rewriting
        // settings.ini, so the persisted preference survives.
        if (persist && _settings.DisplayScale != scale)
        {
            _settings.DisplayScale = scale;
            _settings.Save();
        }
    }

    private void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            if (string.IsNullOrEmpty(_settings.MonitorRomFullPath) || !File.Exists(_settings.MonitorRomFullPath))
            {
                var expected = _settings.Type == MachineType.MZ700
                    ? "1z-013a.rom"
                    : "SA-1510.rom";
                var configured = string.IsNullOrEmpty(_settings.MonitorRomPath)
                    ? "(none configured)"
                    : $"{_settings.MonitorRomPath}  →  {_settings.MonitorRomFullPath}";
                throw new FileNotFoundException(
                    $"Monitor ROM ({expected}) not found.\n\n" +
                    $"Configured path: {configured}\n\n" +
                    $"Place {expected} under a 'roms' folder next to the executable, " +
                    $"or set [Roms.{_settings.Type}] Monitor= in {Path.Combine(AppContext.BaseDirectory, "settings.ini")}.");
            }
            Active.LoadRoms(_settings.MonitorRomFullPath, _settings.FontFullPath);
            Active.Reset();
            Active.Cpu.PcTraceEnabled = _traceEnabled;
            // Pit / Mem / Sound diagnostic hooks are MZ-700-specific
            // for now — MZ-80A equivalents arrive with Phase 2/5.
            if (_machine != null)
            {
                _machine!.Pit.WriteLog = _traceEnabled ? new System.Text.StringBuilder() : null;
                _machine!.Mem.BankSwitchLog = _traceEnabled ? new System.Text.StringBuilder() : null;
                _machine!.Sound.Start();
            }
            _mz80a?.Sound.Start();

            // Auto-load BASIC if requested explicitly (--basic) OR if the
            // initial cassette is a BASIC program (type 0x02 / 0x05). The
            // type peek means the user doesn't have to know whether a
            // given .mzf is MC or BASIC — they just point the emulator at
            // it and the right boot path runs.
            //
            // MZ-80A path: skipped for Phase 1 — the cassette-trap
            // machinery isn't wired yet. Users get a plain SA-1510 boot.
            bool loadBasic = _autoLoadBasic;
            bool cassetteNeedsBasic = false;
            if (_initialCassette != null)
            {
                try
                {
                    var img = Hardware.Cassette.Parse(Hardware.CassetteFile.ReadBytes(_initialCassette));
                    if (img.Type == 0x02 || img.Type == 0x05) { loadBasic = true; cassetteNeedsBasic = true; }
                }
                catch { /* let the Timer_Tick load path surface the error with a clearer status */ }
                _pendingCassette = _initialCassette;
            }
            if (loadBasic)
            {
                // Pre-flight the BASIC file so the failure shows up as a modal
                // at startup (parity with the menu's Load BASIC) rather than a
                // quiet status-bar line many frames later. If BASIC is missing
                // we cancel the auto-load — and the pending cassette too, when
                // it can't run without BASIC.
                if (EnsureBasicAvailable())
                {
                    // Run a few frames so the monitor boots before injecting BASIC
                    _pendingLoadBasic = true;
                }
                else if (cassetteNeedsBasic)
                {
                    _pendingCassette = null;
                }
            }
            _timer.Start();
            _statusLabel.Text = "Running.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to start emulator:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private int _bootFrames;
    // Tracks the last observed Active.Paused so Timer_Tick can react
    // only on transitions — title-bar suffix + display overlay flip
    // together, without either dependency having to know about the
    // other. See RefreshPauseIndicator.
    private bool _wasPaused;
    private const string TitleBase = "MZRaku";
    private bool _monitorReady;
    private int _basicLoadedFrame = -1;
    // MZ-80A cassette autorun (BASIC .mzf loaded at startup): typed
    // LOAD via the auto-typer once BASIC is Ready, then wait for the
    // cassette trap to deliver the body, settle for a moment while
    // BASIC re-tokenises, then type RUN. Same net UX as MZ-700's
    // AutoLoadCassette path.
    private bool _mz80aLoadTyped;
    private int _mz80aLoadDoneFrame = -1;
    private bool _mz80aRunTyped;

    /// <summary>
    /// Detect "monitor finished booting" by spotting the "MONITOR 1Z*"
    /// banner the 1Z-013A ROM writes to VRAM row 0 once init is complete
    /// and the keyboard input loop is active. This is exactly the state
    /// BASIC's startup at $7D79 needs — the call into monitor at $0033
    /// works cleanly only after the monitor has set up its stack and
    /// stopped clearing RAM around $1200.
    /// </summary>
    /// <summary>
    /// MZ-80A analogue of MonitorReady. SA-1510 draws a block-cursor
    /// glyph ($6B) at VRAM row 1 col 0 ($D028 → Vram[40]) once its
    /// boot init is complete and the main-loop cursor blink starts.
    /// The prompt "*" ($2A) alternates with the block via the cursor
    /// blink phase, so either non-zero value at that position
    /// indicates the prompt is live — either way, the ROM is out
    /// of init and it's safe to auto-inject BASIC or a cassette.
    /// </summary>
    private bool Mz80aMonitorReady()
    {
        if (_monitorReady) return true;
        if (_mz80a == null) return false;
        var v = _mz80a.Mem.Vram;
        if (v[40] != 0)
        {
            _monitorReady = true;
        }
        return _monitorReady;
    }

    /// <summary>
    /// Detect SA-5510's "Ready" prompt landing in VRAM. Scans the whole
    /// text area for the display-code sequence R($12) e($85) a($81)
    /// d($84) y($99) — uppercase R + lowercase-bank e/a/d/y (MZ-80A
    /// lowercase display codes = uppercase + $80). Used as the "safe
    /// to start typing" gate for cassette autorun on MZ-80A; the
    /// 60-frame heuristic that works for MZ-700 fires too early here
    /// because SA-5510's cold init takes ~3.3s.
    /// </summary>
    private bool Mz80aBasicReady()
    {
        if (_mz80a == null) return false;
        var v = _mz80a.Mem.Vram;
        for (int i = 0; i < 996; i++)
        {
            if (v[i]     == 0x12 && v[i + 1] == 0x85 &&
                v[i + 2] == 0x81 && v[i + 3] == 0x84 &&
                v[i + 4] == 0x99)
            {
                return true;
            }
        }
        return false;
    }

    // TAPE chip state tracking. Snapshots of the cassette's trap-hit
    // counters between frames so a fresh hit (delta) can extend the
    // flash window; _tapeFlashEndFrame is the deadline in _bootFrames
    // terms after which the chip decays back to Steady (if a cassette
    // is still queued) or Idle.
    private int _tapeFlashEndFrame = -1;
    private int _tapeLastHeaderHits;
    private int _tapeLastDataHits;
    private int _tapeLastWriteHits;
    private TapeState _tapeState = TapeState.Idle;
    private const int TapeFlashFrames = 15;   // ~250 ms at 60 Hz
    private enum TapeState { Idle, Steady, Flash }

    /// <summary>
    /// Poll the active machine's cassette state and update the TAPE chip.
    /// Idle = no pending image; Steady = pending queued, awaiting trap;
    /// Flash = trap fired within the last TapeFlashFrames frames.
    /// Cheap enough to call every frame from Timer_Tick.
    /// </summary>
    private void UpdateTapeLabel()
    {
        bool pending;
        int headerHits, dataHits, writeHits;
        if (_machine != null)
        {
            pending = _machine.Cassette.Pending != null;
            headerHits = _machine.Cassette.HeaderTrapHits;
            dataHits   = _machine.Cassette.DataTrapHits;
            writeHits  = _machine.Cassette.WriteTapeTrapHits;
        }
        else if (_mz80a != null)
        {
            pending = _mz80a.Cassette.Pending != null;
            headerHits = _mz80a.Cassette.HeaderTrapHits;
            dataHits   = _mz80a.Cassette.DataTrapHits;
            writeHits  = 0;   // SA-1510 SAVE traps not wired yet
        }
        else return;

        // Any fresh trap hit extends the flash window. Also handles the
        // MZ-700 SAVE path (WriteTapeTrapHits) even though it has no
        // Pending image.
        if (headerHits != _tapeLastHeaderHits ||
            dataHits   != _tapeLastDataHits   ||
            writeHits  != _tapeLastWriteHits)
        {
            _tapeFlashEndFrame  = _bootFrames + TapeFlashFrames;
            _tapeLastHeaderHits = headerHits;
            _tapeLastDataHits   = dataHits;
            _tapeLastWriteHits  = writeHits;
        }

        TapeState want =
            _bootFrames < _tapeFlashEndFrame ? TapeState.Flash :
            pending                          ? TapeState.Steady :
                                               TapeState.Idle;
        if (want == _tapeState) return;
        _tapeState = want;
        switch (want)
        {
            case TapeState.Idle:
                _tapeLabel.ForeColor = SystemColors.GrayText;
                _tapeLabel.BackColor = SystemColors.Control;
                break;
            case TapeState.Steady:
                _tapeLabel.ForeColor = SystemColors.ControlText;
                _tapeLabel.BackColor = SystemColors.Info;
                break;
            case TapeState.Flash:
                _tapeLabel.ForeColor = Color.White;
                _tapeLabel.BackColor = Color.DarkOrange;
                break;
        }
    }

    /// <summary>
    /// React to Active.Paused changes: title-bar suffix + display
    /// invalidate so Display_Paint's overlay flips in sync. Runs every
    /// frame; the transition guard keeps it cheap when the state's
    /// steady.
    /// </summary>
    private void RefreshPauseIndicator()
    {
        bool paused = Active.Paused;
        if (paused == _wasPaused) return;
        _wasPaused = paused;
        Text = paused ? TitleBase + " — PAUSED" : TitleBase;
        if (_pauseMenuItem != null) _pauseMenuItem.Checked = paused;
        // Silence the wave output on pause. Both machines share the same
        // Sound class (Mz80a uses it too); the concrete field lives on
        // MZ700 / MZ80A respectively, not on IMachine.
        var sound = _machine?.Sound ?? _mz80a?.Sound;
        if (sound != null) sound.Muted = paused;
        _display.Invalidate();
    }

    /// <summary>
    /// Updates the right-hand mode indicator. <paramref name="graph"/>
    /// null → grey "—" (mode unknowable / not-yet-meaningful), false
    /// → normal "ALPHA", true → highlighted "GRAPH". Cheap enough to
    /// call every 10 frames from either machine's tick block.
    /// </summary>
    private void UpdateModeLabel(bool? graph)
    {
        if (graph is null)
        {
            if (_modeLabel.Text != "—")
            {
                _modeLabel.Text = "—";
                _modeLabel.ForeColor = SystemColors.GrayText;
                _modeLabel.BackColor = SystemColors.Control;
            }
            return;
        }
        if (graph.Value && _modeLabel.Text != "GRAPH")
        {
            _modeLabel.Text = "GRAPH";
            _modeLabel.ForeColor = Color.White;
            _modeLabel.BackColor = Color.MediumVioletRed;
        }
        else if (!graph.Value && _modeLabel.Text != "ALPHA")
        {
            _modeLabel.Text = "ALPHA";
            _modeLabel.ForeColor = SystemColors.ControlText;
            _modeLabel.BackColor = SystemColors.Control;
        }
    }

    /// <summary>
    /// Called every frame — if the status label has held a transient
    /// message for longer than <see cref="StatusIdleFrames"/>, clear
    /// it. The machine identity lives in its own left pane, so the
    /// middle pane just goes empty rather than reverting. Populated
    /// by the TextChanged hook on _statusLabel, so every direct
    /// assignment sitewide participates.
    /// </summary>
    private void RevertStatusIfIdle()
    {
        if (_statusLastSetFrame < 0) return;
        if (_bootFrames - _statusLastSetFrame < StatusIdleFrames) return;
        _statusLabel.Text = "";   // TextChanged resets _statusLastSetFrame
    }

    /// <summary>
    /// Phase D pointer-hunt diagnostic (2026-07-12): dumps a report of
    /// candidate SA-5510 TXTTAB / VARTAB pointer positions to a text
    /// file next to MZRaku.exe. The $4E4E variable-slot table was
    /// pinned via this — see Mz80aCassette.FixupBasicProgramPointers.
    /// Other post-LOAD state SA-5510 needs for RUN resisted synthesis
    /// (Error 16 on GOSUB); kept dormant for future iteration on the
    /// invisible-LOAD path. Wire in from Timer_Tick when needed.
    /// </summary>
    private void DumpMz80aBasicPointerCandidates(Hardware.Cassette.MzfImage img, byte[] preLoadRam)
    {
        if (_mz80a == null) return;
        var ram = _mz80a.Mem.Ram;
        int loadAddr = img.LoadAddr;
        int endAddr  = loadAddr + img.Data.Length;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SA-5510 BASIC pointer-hunt diagnostic");
        sb.AppendLine($"Image: {img.Filename}");
        sb.AppendLine($"Type=${img.Type:X2}  LoadAddr=${loadAddr:X4}  ExecAddr=${img.ExecAddr:X4}  DataLen={img.Data.Length}");
        sb.AppendLine($"ProgramEnd = LoadAddr+DataLen = ${endAddr:X4}");
        sb.AppendLine();

        // Scan the whole 64 KiB RAM backing (though only $1000-$CFFF is
        // real user RAM). Report every LE 16-bit pair equal to any of
        // the target values, plus a few pointers into the program body.
        var targets = new (string label, int value)[]
        {
            ("LoadAddr           (TXTTAB?)  ", loadAddr),
            ("LoadAddr+1         (TXTTAB+1?)", loadAddr + 1),
            ("ProgramEnd         (VARTAB?)  ", endAddr),
            ("ProgramEnd+1       (VARTAB?)  ", endAddr + 1),
            ("ProgramEnd+2       (ARRTAB?)  ", endAddr + 2),
            ("ProgramEnd+3       (STREND?)  ", endAddr + 3),
        };
        foreach (var t in targets)
        {
            byte lo = (byte)(t.value & 0xFF);
            byte hi = (byte)((t.value >> 8) & 0xFF);
            sb.AppendLine($"Scan for ${t.value:X4} ({t.label}):");
            int hits = 0;
            for (int a = 0x1000; a < 0xF000; a++)
            {
                if (ram[a] == lo && ram[a + 1] == hi)
                {
                    sb.AppendLine($"  ${a:X4}");
                    hits++;
                    if (hits >= 32) { sb.AppendLine("  …(cutoff at 32 hits)"); break; }
                }
            }
            if (hits == 0) sb.AppendLine("  (no hits)");
            sb.AppendLine();
        }

        // Hex dump the first few bytes at LoadAddr — sanity check that
        // the program body actually landed where the header claimed.
        sb.AppendLine($"RAM[${loadAddr:X4}..${loadAddr + 31:X4}]:");
        sb.Append("  ");
        for (int i = 0; i < 32; i++)
            sb.Append($"{ram[loadAddr + i]:X2} ");
        sb.AppendLine();
        sb.AppendLine();

        // Hex dump the region right after the program end — that's
        // where a "0 0 0" empty-array marker typically follows the
        // program-text terminator on Sharp BASICs.
        sb.AppendLine($"RAM[${endAddr:X4}..${endAddr + 31:X4}]:");
        sb.Append("  ");
        for (int i = 0; i < 32; i++)
            sb.Append($"{ram[endAddr + i]:X2} ");
        sb.AppendLine();
        sb.AppendLine();

        // Also scan for LoadAddr-1: some Sharp BASICs (following MSBASIC)
        // store TXTTAB as start-of-text-minus-one so the link-follow
        // loop can pre-increment.
        int loadMinus1 = loadAddr - 1;
        {
            byte lo = (byte)(loadMinus1 & 0xFF);
            byte hi = (byte)((loadMinus1 >> 8) & 0xFF);
            sb.AppendLine($"Scan for ${loadMinus1:X4} (LoadAddr-1, TXTTAB MSBASIC-style):");
            int hits = 0;
            for (int a = 0x1000; a < 0xF000; a++)
            {
                if (ram[a] == lo && ram[a + 1] == hi)
                {
                    sb.AppendLine($"  ${a:X4}");
                    hits++;
                    if (hits >= 32) { sb.AppendLine("  …(cutoff at 32 hits)"); break; }
                }
            }
            if (hits == 0) sb.AppendLine("  (no hits)");
            sb.AppendLine();
        }

        // Focused hex dumps: the VARTAB cluster at ~$4E4E, and the
        // suspicious LoadAddr low-RAM cluster ~$1AC0-$1B30. TXTTAB
        // almost certainly sits in one of these regions.
        void DumpRange(int start, int end)
        {
            sb.AppendLine($"RAM[${start:X4}..${end:X4}]:");
            for (int a = start; a <= end; a += 16)
            {
                sb.Append($"  ${a:X4}: ");
                for (int j = 0; j < 16 && a + j <= end; j++)
                    sb.Append($"{ram[a + j]:X2} ");
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        DumpRange(0x4E40, 0x4E7F);
        DumpRange(0x1AC0, 0x1B3F);
        DumpRange(0x18A0, 0x18DF);

        // The killer diff: bytes that changed between pre-LOAD and
        // post-LOAD, EXCLUDING the program body (LoadAddr..ProgramEnd-1)
        // and the header buffer at $10F0-$113F (Cassette trap injection).
        // What remains is BASIC's own post-LOAD bookkeeping — this is
        // exactly the set of writes DirectInject needs to replicate.
        {
            sb.AppendLine("Diff: RAM bytes changed by LOAD (excluding program body + header buffer):");
            var changed = new List<int>();
            for (int a = 0x1000; a < 0xF000; a++)
            {
                bool inBody = a >= loadAddr && a < endAddr;
                bool inHeader = a >= 0x10F0 && a < 0x1170;
                if (inBody || inHeader) continue;
                if (preLoadRam[a] != ram[a]) changed.Add(a);
            }
            sb.AppendLine($"  {changed.Count} address(es) changed");
            // Cluster consecutive changes for readability.
            int idx = 0;
            while (idx < changed.Count && idx < 512)
            {
                int start = changed[idx];
                int end = start;
                while (idx + 1 < changed.Count && changed[idx + 1] == end + 1)
                {
                    idx++;
                    end = changed[idx];
                }
                idx++;
                sb.Append($"  ${start:X4}-${end:X4}: ");
                for (int a = start; a <= end; a++)
                {
                    sb.Append($"{preLoadRam[a]:X2}→{ram[a]:X2} ");
                }
                sb.AppendLine();
            }
            if (idx < changed.Count) sb.AppendLine($"  …(cutoff at 512 clusters)");
        }

        var path = System.IO.Path.Combine(AppContext.BaseDirectory,
            "mz80a_basic_pointers.txt");
        System.IO.File.WriteAllText(path, sb.ToString());
        _statusLabel.Text = $"Pointer report → {path}";
    }

    private bool MonitorReady()
    {
        if (_monitorReady) return true;
        var v = _machine!.Mem.Vram;
        // MZ display codes: M=$0D O=$0F N=$0E I=$09 T=$14 1=$21 Z=$1A *=$2A
        if (v[4] == 0x0D && v[5] == 0x0F && v[6] == 0x0E && v[7] == 0x09 &&
            v[8] == 0x14 && v[9] == 0x0F && v[10] == 0x12 &&
            v[12] == 0x21 && v[13] == 0x1A && v[14] == 0x2A)
        {
            _monitorReady = true;
        }
        return _monitorReady;
    }
    /// <summary>
    /// Phase 5.3: after the main window is first shown, honour the
    /// [DebugPanes] boot flags by auto-opening each flagged pane via
    /// its existing open handler. Panes that don't apply to the active
    /// machine (Sound Diagnostic + Keyboard Matrix on MZ-80A, Font
    /// Sheet on MZ-80A until 5.4) are silently skipped — the setting
    /// survives so switching machines restores them next boot.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var dp = _settings.DebugPanesAtStartup;
        bool mz700 = _machine != null;
        if (dp.Debugger) OpenDebugger();
        if (dp.MemoryViewer) OpenMemoryViewer();
        if (dp.HidDiagnostic) OpenHidDiag();
        if (dp.FontSheet && mz700) OpenFontSheet();
        if (dp.SoundDiagnostic && mz700) OpenSoundDiag();
        if (dp.KeyboardMatrix && mz700) OpenKeyboardMatrix();
        // Return focus to the main window so the emulator gets input
        // events; the last pane opened would otherwise steal it.
        Activate();
    }

    private void Timer_Tick(object? s, EventArgs e)
    {
        // Sample real gamepad state once per frame, before the emulated
        // CPU runs — values get latched at the VBLK falling edge inside
        // RunFrame, so they need to be fresh by then.
        if (_machine != null) _joystickInput.Poll();
        Active.RunFrame();
        _bootFrames++;
        _debugger?.RefreshIfVisible();
        _memViewer?.RefreshIfVisible();
        RevertStatusIfIdle();
        UpdateTapeLabel();
        RefreshPauseIndicator();

        // MZ-80A path: skip the MZ-700-flavoured joystick indicator,
        // trace log, and dump path. BASIC / cassette autoload now
        // uses dynamic "monitor ready" detection (analogous to
        // MZ-700's MonitorReady) rather than a fixed 5-second wait,
        // so cold-boot to Ready feels as snappy on MZ-80A as on
        // MZ-700.
        if (_machine == null)
        {
            _display.Invalidate();
            if (_mz80a != null)
            {
                // BASIC gets loaded first (as soon as the SA-1510 prompt
                // is up), THEN the cassette waits 60 frames past
                // _basicLoadedFrame so SA-5510 has time to reach its
                // Ready prompt before we overwrite the program area.
                // Same two-phase shape as MZ-700 uses.
                if (_pendingLoadBasic && Mz80aMonitorReady())
                {
                    try
                    {
                        Active.AutoLoadBasic(_settings.BasicFullPath);
                        _statusLabel.Text = "BASIC loaded.";
                        _basicLoadedFrame = _bootFrames;
                    }
                    catch (Exception ex)
                    {
                        _statusLabel.Text = "BASIC load failed: " + ex.Message;
                    }
                    _pendingLoadBasic = false;
                }
                if (_pendingCassette != null)
                {
                    bool viaBasic = _basicLoadedFrame >= 0;
                    // Cassette-via-BASIC needs SA-5510's "Ready" prompt
                    // to be on screen — the keyboard input path only
                    // starts polling after BASIC finishes its ~3.3s
                    // cold init. Using a fixed frame count here (like
                    // the MZ-700 path's 60-frame wait) fires too early
                    // and BASIC eats the LOAD keystrokes into a buffer
                    // it isn't reading yet.
                    bool ready = viaBasic
                        ? Mz80aBasicReady()
                        : Mz80aMonitorReady();
                    if (ready)
                    {
                        try
                        {
                            var img = Hardware.Cassette.Parse(
                                Hardware.CassetteFile.ReadBytes(_pendingCassette));
                            // BASIC-type images: Queue the image so SA-5510's
                            // LOAD command hits our SA-1510 RDINF/RDDAT
                            // traps ($0027/$002A) and the ROM's own load
                            // path maintains BASIC's internal program
                            // pointers. DirectInject bypasses this and
                            // produces Error 19 on RUN. Machine-code images
                            // still DirectInject + jumpExec — SA-1510's
                            // monitor L command sequence isn't wired yet
                            // for MZ-80A and the shortcut works cleanly for
                            // type 01.
                            bool isBasicType = img.Type == 0x02
                                || img.Type == 0x03
                                || img.Type == 0x05;
                            if (isBasicType)
                            {
                                // Queue the image so SA-5510's LOAD hits
                                // the SA-1510 traps, then auto-type LOAD
                                // and (once the trap has fired + BASIC
                                // has settled) RUN. Same shape as the
                                // MZ-700 autorun path. Attempted a
                                // DirectInject + pointer-fixup shortcut
                                // 2026-07-12 to make LOAD invisible; the
                                // $4E4E variable table was pinned but
                                // other state SA-5510's RUN depends on
                                // (Error 16 on GOSUB) resisted synthesis.
                                // Kept as a future item.
                                _mz80a.Cassette.Queue(img);
                                _mz80a.Keyboard.TypeString("LOAD\r");
                                _mz80aLoadTyped = true;
                                _statusLabel.Text = $"Loading {img.Filename}…";
                            }
                            else
                            {
                                _mz80a.Cassette.DirectInject(img,
                                    jumpExec: img.Type == 0x01);
                                _statusLabel.Text = $"Loaded: {img.Filename}";
                            }
                        }
                        catch (Exception ex)
                        {
                            _statusLabel.Text = "Cassette load failed: " + ex.Message;
                        }
                        _pendingCassette = null;
                    }
                }
                // Cassette-autorun sequencing: LOAD was typed above; once
                // the RDDAT trap has fired (DataDelivered latches true),
                // wait 90 frames for BASIC to re-tokenise the loaded
                // program then auto-type RUN. 90 frames ≈ 1.5s is
                // conservative — trap injection is instantaneous, so the
                // window is BASIC's post-load bookkeeping.
                if (_mz80aLoadTyped && !_mz80aRunTyped)
                {
                    if (_mz80aLoadDoneFrame < 0 && _mz80a.Cassette.DataDelivered)
                        _mz80aLoadDoneFrame = _bootFrames;
                    if (_mz80aLoadDoneFrame >= 0 &&
                        _bootFrames - _mz80aLoadDoneFrame >= 60)
                    {
                        _mz80a.Keyboard.TypeString("RUN\r");
                        _mz80aRunTyped = true;
                        _statusLabel.Text = "Running.";
                    }
                }
                // MZ-80A mode indicator. Tracked locally via F11 press
                // events in Mz80aKeyboard.GraphMode — see the note there
                // about program-driven mode changes not being caught.
                if (_bootFrames % 10 == 0)
                    UpdateModeLabel(_mz80a.Keyboard.GraphMode);
                // HID diagnostic supports MZ-80A too (Phase 4, machine-
                // aware). Sound Diagnostic is still MZ-700-only until
                // its NAND / dual-gate assumptions can be revisited.
                _hidDiag?.RefreshIfVisible();
            }
            return;
        }
        _hidDiag?.RefreshIfVisible();
        _soundDiag?.RefreshIfVisible();

        // MZ-700 mode-label update. S-BASIC's keyboard mode flag lives
        // at $0060 bit 4 (set = GRAPH, clear = ALPHA), discovered
        // empirically via the memory-viewer snapshot/diff tool
        // 2026-05-31. Only meaningful while S-BASIC owns the machine
        // (ROM banked out so $0060 is RAM); before BASIC is loaded,
        // $0060 reads from ROM and the indicator would be misleading,
        // so we grey it out. Also surfaces the Font Sheet on
        // ALPHA→GRAPH — GRAPH mode is unusable without the palette
        // since graphic glyphs aren't reachable from any PC key.
        if (_bootFrames % 10 == 0)
        {
            if (_basicLoadedFrame < 0)
            {
                UpdateModeLabel(null);
            }
            else
            {
                bool graph = (_machine!.Mem.Read(0x0060) & 0x10) != 0;
                UpdateModeLabel(graph);
                if (graph && !_wasGraphMode &&
                    (_fontSheet == null || _fontSheet.IsDisposed || !_fontSheet.Visible))
                {
                    OpenFontSheet();
                }
                _wasGraphMode = graph;
            }
        }


        // Inject pending BASIC as soon as the monitor's input prompt is
        // visible — the banner-detection signals that init is complete and
        // the keyboard loop is running, which is what BASIC's startup at
        // $7D79 needs (it does CALL $0033 into monitor ROM expecting a
        // clean stack). Replaces a previous fixed 180-frame wait.
        if (_pendingLoadBasic && MonitorReady())
        {
            try
            {
                _machine.AutoLoadBasic(_settings.BasicFullPath);
                _statusLabel.Text = "BASIC loaded.";
                _pendingLoadBasic = false;
                _basicLoadedFrame = _bootFrames;
            }
            catch (Exception ex)
            {
                // Defence-in-depth: entry-point checks should have caught a
                // missing BASIC, but if the load fails here (file vanished,
                // unreadable, parse error), behave like the menu's Load
                // BASIC — modal error, abandon any dependent pending work.
                _pendingLoadBasic = false;
                _pendingCassette = null;
                _pendingBasicSource = null;
                _statusLabel.Text = "BASIC load failed.";
                MessageBox.Show(this, "BASIC load failed:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        // Cassette injection: wait 60 frames after BASIC was loaded so its
        // banner displays and READY prompt is reached before we auto-type
        // commands. (For pure-monitor MC cassettes, fire as soon as the
        // monitor is ready.) `basicMode` is the runtime answer to "is this
        // cassette going through BASIC?" — true whether BASIC came from
        // --basic, the menu, or auto-load triggered by opening a BASIC .mzf.
        bool basicMode = _pendingLoadBasic || _basicLoadedFrame >= 0;
        bool readyForCassette = basicMode
            ? (_basicLoadedFrame >= 0 && _bootFrames - _basicLoadedFrame >= 60)
            : MonitorReady();
        if (readyForCassette && _pendingCassette != null)
        {
            try
            {
                if (basicMode)
                {
                    // BASIC is loaded; direct-inject the program into RAM at
                    // its load address (without jumping) and fix up program
                    // pointers, mirroring what the menu's LoadCassetteFile
                    // does. We can't use BASIC's LOAD command because S-BASIC
                    // bypasses the monitor's tape routines (the ones we trap
                    // at $0436/$04D8) — its own tape code reads PortC bit 5
                    // directly and has no real cassette to read from here.
                    var img = Hardware.Cassette.Parse(Hardware.CassetteFile.ReadBytes(_pendingCassette));
                    _machine!.Cassette.DirectInject(img, jumpExec: false);
                    if (img.Type == 0x02 || img.Type == 0x05)
                    {
                        _machine!.Cassette.FixupBasicProgramPointers(img.LoadAddr, img.Data.Length);
                        // Auto-RUN: with the program injected and pointers
                        // fixed, BASIC's RUN command preprocesses lengths and
                        // starts execution. End-to-end automation from CLI.
                        _machine!.Keyboard.TypeString("RUN\r");
                        _statusLabel.Text = $"Loaded {img.Filename}. Running.";
                    }
                    else
                    {
                        string usage = img.Type == 0x01 ? $"USR(${img.ExecAddr:X4})" : "RUN";
                        _statusLabel.Text = $"Loaded {img.Filename} into BASIC. Type {usage}.";
                    }
                }
                else
                {
                    // Machine-code cassette at startup: direct-inject into RAM
                    // and jump to the game's execution address. This bypasses
                    // the monitor's tape-LOAD flow (which would need a working
                    // keyboard-driven command prompt).
                    _machine.DirectInjectCassette(_pendingCassette);
                    _statusLabel.Text = $"Loaded: {Path.GetFileName(_pendingCassette)}";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Cassette load failed: " + ex.Message;
            }
            _pendingCassette = null;
        }

        // BASIC source: identical readiness gate as a BASIC cassette —
        // wait for BASIC's READY prompt then auto-type the file in.
        if (_pendingBasicSource != null && _basicLoadedFrame >= 0 && _bootFrames - _basicLoadedFrame >= 60)
        {
            try
            {
                TypeBasicSource(_pendingBasicSource);
                _statusLabel.Text = $"Typing {Path.GetFileName(_pendingBasicSource)}…";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "BASIC source load failed: " + ex.Message;
            }
            _pendingBasicSource = null;
        }

        _display.Invalidate();

        // Trace state every 20 frames to help diagnose boot/load issues
        if (_traceEnabled && (_bootFrames <= 10 || _bootFrames % 20 == 0) && _bootFrames <= _dumpFrame)
        {
            var c0 = _machine!.Pit.Counters[0];
            var c2 = _machine!.Pit.Counters[2];
            _traceLog.AppendLine($"[F{_bootFrames:D4}] PC=${_machine!.Cpu.PC:X4} SP=${_machine!.Cpu.SP:X4} IFF1={_machine!.Cpu.IFF1} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} run={c2.Running} out={c2.Out} INTMSK={_machine!.Ppi.InterruptMask} hdr={_machine!.Cassette.HeaderDelivered} dat={_machine!.Cassette.DataDelivered}");
        }

        if (_dumpPath != null && _bootFrames == _dumpFrame)
        {
            try
            {
                DumpState(_dumpPath);
                if (_traceEnabled)
                {
                    // Append last 256 PC values (oldest first)
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine("Recent PC trace (oldest first):");
                    int start = _machine!.Cpu.PcTraceIdx;
                    for (int i = 0; i < _machine!.Cpu.PcTrace.Length; i++)
                    {
                        sb.Append($"${_machine!.Cpu.PcTrace[(start + i) & 0xFF]:X4} ");
                        if (i % 16 == 15) sb.AppendLine();
                    }
                    _traceLog.Append(sb);
                    if (_machine!.Pit.WriteLog != null)
                    {
                        _traceLog.AppendLine();
                        _traceLog.AppendLine("PIT write log:");
                        _traceLog.Append(_machine!.Pit.WriteLog);
                    }
                    if (_machine!.Mem.BankSwitchLog != null)
                    {
                        _traceLog.AppendLine();
                        _traceLog.AppendLine("Bank-switch log:");
                        _traceLog.Append(_machine!.Mem.BankSwitchLog);
                    }
                    File.WriteAllText(_dumpPath + ".trace", _traceLog.ToString());
                }
            }
            catch (Exception ex) { _statusLabel.Text = "Dump failed: " + ex.Message; }
            Close();
        }
    }

    public int _dumpFrame = 120;
    public bool _traceEnabled = true;
    private readonly System.Text.StringBuilder _traceLog = new();

    private void DumpState(string path)
    {
        using var w = new StreamWriter(path);
        w.WriteLine($"MZ700 state after {_bootFrames} frames");
        w.WriteLine($"CPU: PC=${_machine!.Cpu.PC:X4} SP=${_machine!.Cpu.SP:X4} A=${_machine!.Cpu.A:X2} F=${_machine!.Cpu.F:X2} HL=${_machine!.Cpu.HL:X4} BC=${_machine!.Cpu.BC:X4} DE=${_machine!.Cpu.DE:X4}");
        w.WriteLine($"IM={_machine!.Cpu.IM} IFF1={_machine!.Cpu.IFF1} Halted={_machine!.Cpu.Halted} Cycles={_machine!.Cpu.TotalCycles}");
        w.WriteLine($"PPI PortA=${_machine!.Ppi.PortA:X2} PortCOut=${_machine!.Ppi.PortCOut:X2} PortCIn=${_machine!.Ppi.PortCIn:X2}");
        w.WriteLine($"Mem RomEnabled={_machine!.Mem.RomEnabled} VramIoEnabled={_machine!.Mem.VramIoEnabled}");
        w.WriteLine($"PIT C0.Reload={_machine!.Pit.Counters[0].Reload} C2.Reload={_machine!.Pit.Counters[2].Reload}");
        var sb0 = new System.Text.StringBuilder("RAM @ $1200: ");
        for (int i = 0; i < 32; i++) sb0.Append($"{_machine!.Mem.Read((ushort)(0x1200 + i)):X2} ");
        w.WriteLine(sb0.ToString());
        w.WriteLine($"Tape trap hits: BreakWait={_machine!.Cassette.BreakWaitTrapHits} Header={_machine!.Cassette.HeaderTrapHits} Data={_machine!.Cassette.DataTrapHits} WriteTape={_machine!.Cassette.WriteTapeTrapHits}");
        w.WriteLine();
        w.WriteLine("VRAM (40x25 text codes):");
        for (int row = 0; row < 25; row++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{row:D2}] ");
            for (int col = 0; col < 40; col++)
            {
                byte b = _machine!.Mem.Vram[row * 40 + col];
                sb.Append($"{b:X2} ");
            }
            w.WriteLine(sb.ToString());
        }
        w.WriteLine();
        w.WriteLine("VRAM as ASCII (best-effort):");
        for (int row = 0; row < 25; row++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{row:D2}] ");
            for (int col = 0; col < 40; col++)
            {
                byte b = _machine!.Mem.Vram[row * 40 + col];
                char c = MzDisplayToAscii(b);
                sb.Append(c);
            }
            w.WriteLine(sb.ToString());
        }
    }

    private static char MzDisplayToAscii(byte b)
    {
        // MZ display codes: 0x00=@, 0x01-0x1A=A-Z, 0x20-0x29=0-9 etc. mapping varies
        // Use the standard MZ-700 display-code to ASCII best-effort
        if (b == 0x00) return ' ';
        if (b >= 0x01 && b <= 0x1A) return (char)('A' + (b - 0x01));
        if (b >= 0x20 && b <= 0x29) return (char)('0' + (b - 0x20));
        if (b == 0x2A) return ' ';
        if (b == 0x67) return ' ';
        if (b == 0xCE) return ' '; // MZ "space" in some sets
        if (b >= 0x20 && b <= 0x7E) return (char)b;
        return '.';
    }

    private void Display_Paint(object? sender, PaintEventArgs e)
    {
        // Machine-agnostic framebuffer pick: use whichever machine is
        // active. Both renderers back onto a 320×200 32bppArgb Bitmap
        // so the downstream draw + scanline overlay code is identical.
        var frame = _machine?.Video.Frame ?? _mz80a?.Video.Frame;
        if (frame == null)
        {
            e.Graphics.Clear(Color.Black);
            return;
        }
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.SmoothingMode = SmoothingMode.None;

        var cr = _display.ClientRectangle;
        float sx = (float)cr.Width / VideoRenderer.PixelWidth;
        float sy = (float)cr.Height / VideoRenderer.PixelHeight;
        float scale = Math.Min(sx, sy);
        int w = (int)(VideoRenderer.PixelWidth * scale);
        int h = (int)(VideoRenderer.PixelHeight * scale);
        int x = (cr.Width - w) / 2;
        int y = (cr.Height - h) / 2;
        e.Graphics.DrawImage(frame, new Rectangle(x, y, w, h));

        // Optional CRT-style scanline overlay: a dim line every
        // native-scanline-row, so each MZ pixel-row gets one dark
        // separator beneath it. Skipped at sub-2× rendered scale where
        // the gap is too thin to read as scanlines.
        if (_settings.DisplayScanlines)
        {
            int step = (int)Math.Round(scale);
            if (step >= 2)
            {
                using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                for (int row = step - 1; row < h; row += step)
                    e.Graphics.FillRectangle(dim, x, y + row, w, 1);
            }
        }

        // Pause overlay: dim wash over the whole video area so the
        // frozen frame reads as "on pause" rather than "did the emulator
        // crash?" Painted last so it dims the scanlines too. ~40 %
        // opacity — dark enough to unambiguously signal pause, light
        // enough that the underlying frame is still legible for
        // debugging inspection.
        if (Active.Paused)
        {
            using var pauseDim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            e.Graphics.FillRectangle(pauseDim, x, y, w, h);
        }
    }

    private static bool IsShiftKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        // MZ-80A path: physical-key → matrix mapping only. The rich
        // CharMap / SpecialKeyMap / auto-typer stack is MZ-700-only
        // for now; extending it is Phase 3+ polish.
        if (_mz80a != null)
        {
            if (_mz80a.Keyboard.OnKeyDown(e.KeyData)) e.Handled = true;
            return;
        }
        if (_machine == null) return;
        // e.Shift can momentarily lag on the very first shift keydown, so
        // also detect via the VK code itself.
        bool shift = e.Shift || IsShiftKey(e.KeyCode);
        // Pass KeyData (VK + modifier flags) so the override layer can
        // match modifier-aware bindings; Keyboard internally strips
        // modifiers when consulting SpecialKeyMap and managing holds.
        // Shift state goes inside OnKeyDown — calling SetShift here too
        // would prematurely write the PC-shift-derived $1170 value and
        // race the upcoming OnKeyPress on layouts where the MZ shift
        // requirement disagrees (UK `'` → MZ-shift ON; UK Shift+`'` →
        // `@` → MZ-shift OFF).
        if (_machine!.Keyboard.OnKeyDown(e.KeyData, shift)) e.Handled = true;
    }

    private void OnKeyPress(object? s, KeyPressEventArgs e)
    {
        // MZ-80A: char-driven path added Phase A (2026-07-12). OnKeyDown
        // routes non-printables directly and defers character keys here.
        if (_mz80a != null)
        {
            _mz80a.Keyboard.OnKeyPress(e.KeyChar);
            return;
        }
        if (_machine == null) return;
        _machine!.Keyboard.OnKeyPress(e.KeyChar);
    }

    private void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (_mz80a != null)
        {
            if (_mz80a.Keyboard.OnKeyUp(e.KeyData)) e.Handled = true;
            return;
        }
        if (_machine == null) return;
        bool shift = e.Shift && !IsShiftKey(e.KeyCode);
        if (_machine!.Keyboard.OnKeyUp(e.KeyData, shift)) e.Handled = true;
    }

    private void OnDragEnter(object? s, DragEventArgs e)
    {
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? s, DragEventArgs e)
    {
        if (e.Data == null) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;
        LoadCassetteFile(files[0]);
        // The drag originates from another window (Explorer, etc.), so focus
        // stays with the source unless we explicitly grab it back.
        Activate();
    }

    private void BrowseAndLoad()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "MZ cassette images (*.mzf;*.m12;*.mzt;*.zip)|*.mzf;*.m12;*.mzt;*.zip|All files|*.*",
            Title = "Load cassette image"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadCassetteFile(dlg.FileName);
    }

    private void LoadCassetteFile(string path)
    {
        try
        {
            var img = Hardware.Cassette.Parse(Hardware.CassetteFile.ReadBytes(path));

            // Loading any cassette is treated as a fresh-state operation,
            // mirroring the CLI launch path. If the previous run was BASIC
            // (or a BASIC program is mid-execution), we reset back to the
            // monitor before injecting — leaving stale BASIC state lying
            // around would mean the new program runs against the old
            // interpreter's RAM, which doesn't work for mid-execution and
            // also breaks "load MC cassette while at BASIC READY". The
            // type-byte tells us whether to also auto-load BASIC; the
            // existing pending-cassette path in Timer_Tick handles the
            // rest (monitor banner detection, post-BASIC 60-frame wait,
            // direct-inject + auto-RUN for BASIC, jump-to-exec for MC).
            bool needsBasic = img.Type == 0x02 || img.Type == 0x05;
            bool basicLoaded = _basicLoadedFrame >= 0;

            if (basicLoaded || needsBasic)
            {
                // Pre-flight the BASIC file. If the cassette needs BASIC and
                // it's missing, abort the whole load: a BASIC program can't
                // run without the interpreter, and continuing would only
                // result in junk getting typed into the monitor.
                if (needsBasic && !EnsureBasicAvailable()) return;
                ResetMachine();
                if (needsBasic) _pendingLoadBasic = true;
                _pendingCassette = path;
                _statusLabel.Text = needsBasic
                    ? $"Loading BASIC + {img.Filename}…"
                    : $"Loading {img.Filename}…";
            }
            else if (img.Type == 0x01)
            {
                // Pure monitor + machine-code cassette: direct-inject and
                // jump straight to its exec entry. No reset needed.
                if (_mz80a != null)
                    _mz80a.Cassette.DirectInject(img, jumpExec: true);
                else
                    _machine!.Cassette.DirectInject(img, jumpExec: true);
                _statusLabel.Text = $"Loaded & run: {img.Filename} exec=${img.ExecAddr:X4}";
            }
            else
            {
                // Other type at the monitor: queue for monitor LOAD command
                // (MZ-700) or direct-inject as best-effort (MZ-80A doesn't
                // have the L-command auto-typer path yet). Non-MC .mzf
                // types (03 BASIC data, 04 ASCII, A0/A1 PASCAL) don't
                // have a reliable exec address — inject-only.
                if (_mz80a != null)
                {
                    _mz80a.Cassette.DirectInject(img, jumpExec: false);
                    _statusLabel.Text = $"Loaded (no exec): {img.Filename}";
                }
                else
                {
                    _machine!.Cassette.Queue(img);
                    _statusLabel.Text = $"Queued: {img.Filename}. Type LOAD to fetch.";
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to load cassette:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadBasic()
    {
        if (!EnsureBasicAvailable()) return;
        try
        {
            Active.AutoLoadBasic(_settings.BasicFullPath);
            _basicLoadedFrame = _bootFrames;
            _statusLabel.Text = "BASIC loaded.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "BASIC load failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Pre-flight check used by every entry point that triggers a BASIC
    /// load (menu, CLI, BASIC-cassette auto-load, Load BASIC source). When
    /// the configured BASIC image is missing, shows a modal and returns
    /// false so the caller can abort cleanly — without this, the deferred
    /// load in <see cref="Timer_Tick"/> would surface the failure only as
    /// a status-bar line and the rest of the pending operation would
    /// proceed regardless.
    /// </summary>
    private bool EnsureBasicAvailable()
    {
        var path = _settings.BasicFullPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return true;
        var configured = string.IsNullOrEmpty(_settings.BasicPath)
            ? "(none configured)"
            : $"{_settings.BasicPath}  →  {_settings.BasicFullPath}";
        MessageBox.Show(this,
            "BASIC cassette image (1Z-013B.mzf) not found.\n\n" +
            $"Configured path: {configured}\n\n" +
            $"Place 1Z-013B.mzf under a 'basic' or 'roms' folder next to the executable, " +
            $"or set [Roms] Basic= in {Path.Combine(AppContext.BaseDirectory, "settings.ini")}.",
            "BASIC not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    private void BrowseAndLoadBasicSource()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "BASIC source (*.bas;*.txt)|*.bas;*.txt|All files|*.*",
            Title = "Load BASIC source"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadBasicSourceFile(dlg.FileName);
    }

    /// <summary>
    /// Type a BASIC text source into the running interpreter. Each non-
    /// blank, non-comment line is sent through the keyboard auto-typer
    /// followed by CR. If BASIC isn't currently loaded, the machine is
    /// reset and BASIC + this source are queued for after monitor boot.
    /// Comment lines start with <c>;</c> or <c>'</c> and are stripped on
    /// the host side so they don't waste cycles inside BASIC.
    /// </summary>
    private void LoadBasicSourceFile(string path)
    {
        try
        {
            if (_basicLoadedFrame < 0)
            {
                if (!EnsureBasicAvailable()) return;
                ResetMachine();
                _pendingLoadBasic = true;
                _pendingBasicSource = path;
                _statusLabel.Text = $"Loading BASIC + {Path.GetFileName(path)}…";
                return;
            }
            TypeBasicSource(path);
            _statusLabel.Text = $"Typing {Path.GetFileName(path)}…";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to load BASIC source:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TypeBasicSource(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('\'')) continue;
            _machine!.Keyboard.TypeString(line + "\r");
        }
    }

    private void ResetMachine()
    {
        Active.Reset();
        _bootFrames = 0;
        _monitorReady = false;
        _basicLoadedFrame = -1;
        // MZ-80A cassette autorun flags — must reset so a second
        // BASIC cassette load (drag/drop or menu after the first)
        // gets its LOAD-then-RUN sequence, rather than reading the
        // previous run's "already ran" state.
        _mz80aLoadTyped = false;
        _mz80aLoadDoneFrame = -1;
        _mz80aRunTyped = false;
        _statusLabel.Text = "Reset.";
    }

    private void SwitchMachine(MachineType target)
    {
        if (target == _settings.Type) return; // already on it — no-op
        var name = target == MachineType.MZ700 ? "MZ-700" : "MZ-80A";
        var result = MessageBox.Show(
            $"Restart MZRaku to run Sharp {name}?\n\n" +
            "This is a one-off switch for this session. Your default machine on next natural launch (Settings → [Machine] DefaultMachine=) is unchanged.",
            "Switch machine",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        // Close any owned diagnostic windows first — WinForms' owned-
        // form teardown during shutdown enumerates Application.OpenForms
        // and can hit an InvalidOperationException if forms close mid-
        // enumeration. Belt-and-braces: clear each Owner then dispose.
        void SafeCloseChild(Form? f)
        {
            if (f == null || f.IsDisposed) return;
            f.Owner = null;
            f.Close();
            f.Dispose();
        }
        SafeCloseChild(_hidDiag);
        SafeCloseChild(_soundDiag);
        SafeCloseChild(_memViewer);
        SafeCloseChild(_fontSheet);
        SafeCloseChild(_debugger);

        // Phase 5.1a: menu switches are ad-hoc — they do NOT rewrite
        // [Machine] DefaultMachine=. Launch a fresh process with the
        // target's --mz700 / --mz80a CLI flag so this run boots into
        // the chosen machine while the persisted default stays put.
        // Any existing --mz700 / --mz80a in our own args is stripped
        // (target's flag replaces it); other flags (--basic,
        // --scanlines, --display, cassette path) are preserved.
        var origArgs = Environment.GetCommandLineArgs();
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
        for (int i = 1; i < origArgs.Length; i++)
        {
            var a = origArgs[i];
            if (a.Equals("--mz700", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Equals("--mz80a", StringComparison.OrdinalIgnoreCase)) continue;
            psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add(target == MachineType.MZ700 ? "--mz700" : "--mz80a");
        System.Diagnostics.Process.Start(psi);
        Environment.Exit(0);
    }

    private void OpenSettings(SettingsForm.Tab tab = SettingsForm.Tab.Startup)
    {
        using var dlg = new SettingsForm(_settings, _joystickInput, _machine, tab);
        dlg.Applied += OnSettingsApplied;
        dlg.ShowDialog(this);
    }

    private void OnSettingsApplied()
    {
        // Display scale change: re-size the client area and update the
        // View menu's checked state. ApplyDisplayScale is a no-op if the
        // value didn't actually change.
        ApplyDisplayScale(_settings.DisplayScale);
        // Mirror the scanlines checkbox state onto the View → Scanlines
        // menu item so the two stay in sync regardless of which path
        // the user toggled from. Also count Settings Apply as an
        // explicit acceptance of current state — a CLI override no
        // longer needs reverting on close.
        if (_scanlinesMenuItem != null) _scanlinesMenuItem.Checked = _settings.DisplayScanlines;
        _scanlinesRestoreOnClose = null;
        // MZ-80A live-apply (Phase 5.2): both toggles push through
        // immediately when MZ-80A is the active machine, matching how
        // the retired View → Green screen menu item behaved. When
        // MZ-700 is active the settings persist silently and take
        // effect on the next MZ-80A boot.
        if (_mz80a != null)
        {
            ApplyMz80aScreenColor();
            _mz80a.Keyboard.InvertLetterShift = _settings.Mz80aInvertLetterShift;
        }
        _display.Invalidate();
        // Joystick button bindings can be re-pushed live; ROM paths take
        // effect on the next Reset, so we don't touch the running machine.
        _joystickInput.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
    }

    private void OpenDebugger()
    {
        if (_debugger == null || _debugger.IsDisposed)
        {
            _debugger = new DebuggerForm(Active, ResetMachine, _settings);
            // First open ever: park just to the right of the main window.
            // The form ctor honours any saved geometry, so this only
            // applies when nothing's been persisted yet.
            if (!_settings.DebuggerWindow.HasGeometry)
                _debugger.Location = new Point(Bounds.Right + 8, Bounds.Top);
        }
        _debugger.Owner = this;
        _debugger.Show();
        _debugger.BringToFront();
    }

    private void OpenMemoryViewer()
    {
        if (_memViewer == null || _memViewer.IsDisposed)
        {
            _memViewer = new MemoryViewerForm(Active, _settings);
            // First open ever: park below the main window so it doesn't
            // fight the debugger for the right-side slot.
            if (!_settings.MemoryViewerWindow.HasGeometry)
                _memViewer.Location = new Point(Bounds.Left, Bounds.Bottom + 8);
        }
        _memViewer.Owner = this;
        _memViewer.Show();
        _memViewer.BringToFront();
    }

    private void OpenFontSheet()
    {
        if (_machine == null) { NotAvailableOnMz80a("Font Sheet"); return; }
        if (_fontSheet == null || _fontSheet.IsDisposed)
            _fontSheet = new FontSheetForm(_machine);
        _fontSheet.Owner = this;
        _fontSheet.Show();
        _fontSheet.BringToFront();
    }

    private void OpenKeyboardMatrix()
    {
        if (_machine == null) { NotAvailableOnMz80a("Keyboard Matrix"); return; }
        if (_matrixForm == null || _matrixForm.IsDisposed)
            _matrixForm = new KeyboardMatrixForm(_machine);
        _matrixForm.Owner = this;
        // Deliberately not BringToFront — that activates the window and
        // steals focus from the emulator; we want to watch the highlight
        // pulse while typing into the main window.
        _matrixForm.Show();
    }

    private void OpenHidDiag()
    {
        // Pick whichever machine is active. Both MZ-700 and MZ-80A
        // support HID Diagnostic since the form was made machine-aware
        // (v1.1.0 Phase 4).
        IMachine? active = _machine != null ? _machine : _mz80a;
        if (active == null) return;
        if (_hidDiag == null || _hidDiag.IsDisposed)
        {
            _hidDiag = new HidDiagnosticForm(active, _joystickInput);
            // First open: park it to the right of the main window so it
            // doesn't fight the debugger for screen space.
            _hidDiag.Location = new Point(Bounds.Right + 8, Bounds.Top + 240);
        }
        _hidDiag.Owner = this;
        _hidDiag.Show();
        // Deliberately not BringToFront — that activates the window and
        // steals focus from the emulator, which defeats the diagnostic's
        // purpose (watching live input from the main window). The form's
        // ShowWithoutActivation override stops Show() from grabbing focus
        // on first open; for subsequent re-opens of an already-visible
        // form, we explicitly re-activate the main window so the user can
        // keep typing without clicking back.
        Activate();
    }

    private void OpenSoundDiag()
    {
        if (_machine == null) { NotAvailableOnMz80a("Sound Diagnostic"); return; }
        if (_soundDiag == null || _soundDiag.IsDisposed)
        {
            _soundDiag = new SoundDiagnosticForm(_machine);
            _soundDiag.Location = new Point(Bounds.Right + 8, Bounds.Top);
        }
        _soundDiag.Owner = this;
        _soundDiag.Show();
        // Same focus-preserving pattern as the HID Diagnostic — keep
        // typing from the main window unobstructed.
        Activate();
    }

    /// <summary>
    /// Shows a friendly "not available on MZ-80A" popup for any
    /// diagnostic pane that reaches into MZ-700-specific hardware
    /// (Sound, PPI PortC bit meanings, matrix layout, PCG font). These
    /// come back online as MZ-80A equivalents in later phases.
    /// </summary>
    private void NotAvailableOnMz80a(string pane)
    {
        MessageBox.Show(this,
            $"{pane} is currently MZ-700-only. An MZ-80A equivalent will land in a later phase.",
            "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("MZRaku.ico", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_MENU = 0x12;          // Alt key
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
