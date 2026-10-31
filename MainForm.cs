using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MZRaku;

public sealed class MainForm : Form
{
    // The session owns the machine (v1.4 Phase 2). Exactly one of
    // _session.Mz700 / Mz80a / Mz800 is non-null; machine-specific
    // accesses are gated on it, and IMachine-surface calls use
    // _session.Active, which works for all three.
    private readonly EmulatorSession _session;
    private readonly IJoystickSource _joystickInput;
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
    // MZ-800 only, left of the mode pane: display mode (MZ-700 /
    // 320×200 / 640×200).
    private readonly ToolStripStatusLabel _displayModeLabel = new()
    {
        Spring = false,
        AutoSize = false,
        Width = 64,
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
    private readonly PictureBox _display = new();
    private readonly Settings _settings = Settings.Load();
    private readonly ToolStripMenuItem[] _scaleMenuItems = new ToolStripMenuItem[3];
    private readonly string? _initialCassette;
    private readonly bool _autoLoadBasic;
    private readonly string? _dumpPath;
    private bool _started;
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
    // display group. EmulatorSession applies it at construction and in
    // ApplySettings (from OnSettingsApplied).
    private ToolStripMenuItem? _pauseMenuItem;
    private readonly bool _startFullScreen;
    // Captures the pre-override scanlines value when --scanlines was
    // passed on the CLI. The natural-close FormClosing handler restores
    // it before Save so a per-session override doesn't sneak into
    // settings.ini. Cleared the moment the user explicitly toggles
    // (View → Scanlines, Ctrl+L) — that's now their persisted choice.
    private bool? _scanlinesRestoreOnClose;
    // GRAPH-mode transition tracking (was _wasGraphMode) now lives on
    // the session's autoload orchestrator, which raises GraphModeEntered
    // for the Font Sheet auto-surface.
    private KeyboardMatrixForm? _matrixForm;

    public MainForm(string? cassettePath, bool autoLoadBasic, string? dumpPath = null,
        int dumpFrame = 120,
        int? displayScaleOverride = null, bool startFullScreen = false,
        bool? scanlinesOverride = null, MachineType? machineOverride = null,
        KeyScript? keyScript = null)
    {
        _initialCassette = cassettePath;
        _autoLoadBasic = autoLoadBasic;
        _dumpPath = dumpPath;
        _dumpFrame = dumpFrame;
        _startFullScreen = startFullScreen;
        // CLI --scanlines on/off wins for this session. Snapshot the
        // persisted value so the natural-close path can restore it.
        if (scanlinesOverride.HasValue)
        {
            _scanlinesRestoreOnClose = _settings.DisplayScanlines;
            _settings.DisplayScanlines = scanlinesOverride.Value;
        }
        // Machine selection: a --mz700/--mz80a/--mz800 CLI flag wins for
        // this run over the persisted [Machine] DefaultMachine. Not
        // written back to settings.ini (Settings → Startup is the
        // persist path).
        if (machineOverride.HasValue) _settings.CurrentMachine = machineOverride.Value;
        // The session resolves ROM paths, creates the machine and
        // applies its settings.
        _session = new EmulatorSession(_settings);

        // The host gamepad drives the session's joystick (the session
        // polls it each frame, only on the MZ-700 and MZ-800).
        _joystickInput = new JoystickInput(_session.Joystick);
        _joystickInput.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
        _session.JoystickSource = _joystickInput;
        // --keys=<file>: scripted keys go through the same path as real
        // ones (InjectScriptedKey), hotkeys first.
        if (keyScript != null) _session.UseKeyScript(keyScript, InjectScriptedKey);

        Text = TitleBase;
        Icon = EmbeddedResources.LoadIcon();
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
        // ALPHA/GRAPH mode indicator (right, fixed). The MZ-800 adds a
        // display-mode pane before the mode indicator. Order matters —
        // leftmost item first.
        _machineLabel.Text = _session.MachineLabel;
        _status.Items.Add(_machineLabel);
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_tapeLabel);
        if (_session.Mz800 != null) _status.Items.Add(_displayModeLabel);
        _status.Items.Add(_modeLabel);
        if (_session.Mz800 != null)
        {
            _status.ShowItemToolTips = true;
            _modeLabel.ToolTipText = "MZ-800 BASIC keyboard mode: ALPHA (normal), " +
                                     "LOCK (shift lock, SHIFT+ALPHA) or GRAPH";
            UpdateMz800ModeLabel();
        }

        // Session events. All fire on the UI thread (frames run from
        // Timer_Tick), so the handlers can touch controls directly.
        _session.StatusMessage += msg => _statusLabel.Text = msg;
        _session.FatalError += msg =>
            MessageBox.Show(this, msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        _session.GraphModeEntered += () =>
        {
            if (_fontSheet == null || _fontSheet.IsDisposed || !_fontSheet.Visible)
                OpenFontSheet();
        };
        _session.GraphModeSampled += UpdateModeLabel;
        _session.DumpComplete += Close;
        if (_session.Mz800 != null)
            _session.FrameRan += frame => { if (frame % 10 == 0) UpdateMz800ModeLabel(); };

        // Every direct assignment to _statusLabel.Text starts the
        // idle-clear timer, so every call site sitewide participates
        // without needing a helper wrapper. Empty-string assignments
        // (our own idle revert) mark the tracker as -1 to stop
        // retriggering.
        _statusLabel.TextChanged += (_, _) =>
        {
            _statusLastSetFrame =
                string.IsNullOrEmpty(_statusLabel.Text) ? -1 : _session.FrameCount;
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
            //
            // --dump runs skip it: they take no keyboard input, and the
            // regression suite runs several at once in the background,
            // where the injected Alt would land in whichever window has
            // focus.
            if (_dumpPath == null)
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(Handle);
                Activate();
                Focus();
            }
            Start();

            // --display=full from the CLI flips into borderless mode
            // after the windowed form has settled (Shown fires after
            // the first paint), so the saved windowed bounds are still
            // valid for restoring later via Alt+Enter.
            if (_startFullScreen) EnterFullScreen();
        };
        FormClosing += (_, _) =>
        {
            // First, so a close from inside a frame (a scripted Alt+F4,
            // a finished --dump) also skips the rest of that frame.
            _session.Stop();
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
            _session.Active.Sound.Dispose();
        };
        // After FormClosing: a paint can still arrive until the window goes.
        FormClosed += (_, _) => _displayBitmap?.Dispose();
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        // File — genuine file operations only. Emulator lifecycle and
        // configuration moved to the System menu (2nd position).
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Load cassette...", null, (_, _) => BrowseAndLoad()) { ShortcutKeys = Keys.Control | Keys.O });
        // Insert cassette (v1.3.0 Phase 4a, widened in 7.5): mount a tape
        // for the machine's own LOAD to read through the tape traps,
        // instead of the direct-inject shortcut Load cassette uses — the
        // authentic MZ LOAD experience. MZ-700: the monitor's L. MZ-800:
        // the IPL's C option, the monitor's L or BASIC's LOAD, with a
        // whole multi-file tape. MZ-80A skips it (SA-5510 goes through
        // BASIC's LOAD).
        file.DropDownItems.Add(new ToolStripMenuItem("Insert cassette for &LOAD...", null, (_, _) => BrowseAndQueue()));
        file.DropDownItems.Add(new ToolStripMenuItem("Load &BASIC", null, (_, _) => LoadBasic()) { ShortcutKeys = Keys.Control | Keys.B });
        file.DropDownItems.Add(new ToolStripMenuItem("Load BASIC &source...", null, (_, _) => BrowseAndLoadBasicSource()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.B });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        menu.Items.Add(file);

        // System — emulator machine management. Grouped as
        // (Machine + Reset + Pause) for what-machine-and-in-what-state,
        // separator, Settings for configuration.
        var system = new ToolStripMenuItem("&System");

        // Machine selection — radio pair. Selecting the non-active
        // machine writes settings.ini and prompts a restart; no live
        // switch (the debugger, memory viewer, sound diagnostic all
        // hold references to the current machine, so tearing it
        // down mid-run is messier than an application restart).
        var machine = new ToolStripMenuItem("&Machine");
        var mz700Item = new ToolStripMenuItem("MZ-&700")
        {
            Checked = _settings.CurrentMachine == MachineType.MZ700,
        };
        var mz80aItem = new ToolStripMenuItem("MZ-&80A")
        {
            Checked = _settings.CurrentMachine == MachineType.MZ80A,
        };
        var mz800Item = new ToolStripMenuItem("MZ-8&00")
        {
            Checked = _settings.CurrentMachine == MachineType.MZ800,
        };
        mz700Item.Click += (_, _) => SwitchMachine(MachineType.MZ700);
        mz80aItem.Click += (_, _) => SwitchMachine(MachineType.MZ80A);
        mz800Item.Click += (_, _) => SwitchMachine(MachineType.MZ800);
        machine.DropDownItems.Add(mz700Item);
        machine.DropDownItems.Add(mz80aItem);
        machine.DropDownItems.Add(mz800Item);
        system.DropDownItems.Add(machine);

        system.DropDownItems.Add(new ToolStripMenuItem("&Reset", null, (_, _) => _session.Reset()) { ShortcutKeys = Keys.Control | Keys.R });

        // Pause / resume: global shortcut for the same IMachine.Pause /
        // Resume toggle the Debugger's pause button drives.
        // Pause and Scroll Lock are both caught in ProcessCmdKey (menu
        // ShortcutKeys can't take a bare non-modifier key); bind both
        // so keyboards without a physical Pause key (e.g. Logitech
        // MX Keys) can still reach it.
        _pauseMenuItem = new ToolStripMenuItem("&Pause emulator", null, (_, _) => TogglePause())
        {
            ShortcutKeyDisplayString = "Pause / ScrLk",
            Checked = _session.Active.Paused,
        };
        system.DropDownItems.Add(_pauseMenuItem);

        system.DropDownItems.Add(new ToolStripSeparator());

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
        system.DropDownItems.Add(settings);
        menu.Items.Add(system);

        var view = new ToolStripMenuItem("&View");
        for (int i = 0; i < 3; i++)
        {
            int scale = i + 1;
            var item = new ToolStripMenuItem($"&{scale}× ({Mz700Video.PixelWidth * scale}×{Mz700Video.PixelHeight * scale})",
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

        // Debug — developer / diagnostic tools only. Pause emulator
        // moved to the System menu; it's a runtime-lifecycle action
        // rather than a diagnostic.
        var debug = new ToolStripMenuItem("&Debug");
        debug.DropDownItems.Add(new ToolStripMenuItem("&Debugger…", null, (_, _) => OpenDebugger()) { ShortcutKeys = Keys.Control | Keys.D });
        debug.DropDownItems.Add(new ToolStripMenuItem("&Memory Viewer…", null, (_, _) => OpenMemoryViewer()) { ShortcutKeys = Keys.Control | Keys.M });
        debug.DropDownItems.Add(new ToolStripMenuItem("&HID Diagnostic…", null, (_, _) => OpenHidDiag()) { ShortcutKeys = Keys.Control | Keys.H });
        debug.DropDownItems.Add(new ToolStripMenuItem("&Sound Diagnostic…", null, (_, _) => OpenSoundDiag()));
        debug.DropDownItems.Add(new ToolStripMenuItem("Keyboard &Matrix…", null, (_, _) => OpenKeyboardMatrix()));
        menu.Items.Add(debug);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About…", null, (_, _) =>
        {
            using var dlg = new AboutForm(_settings.CurrentMachine);
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A --dump run swallows real keys here too, so no hotkey or
        // menu shortcut fires (see EmulatorSession.IgnoresHostKeys).
        if (_session.IgnoresHostKeys) return true;
        // Alt+Enter is the full-screen toggle. Catching it here keeps it
        // out of Windows' own Alt-handling (which would otherwise pop
        // the system menu or eat the keystroke).
        if (keyData == (Keys.Alt | Keys.Enter))
        {
            ToggleFullScreen();
            return true;
        }
        // Alt+F4 must close the emulator. F4 alone is wired to MZ F4 via
        // Mz700SpecialKeyMap so the normal OnKeyDown path marks it as handled
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
    /// Flip IMachine.Paused. Menu check mark, title-bar suffix, and
    /// display dim overlay all follow via RefreshPauseIndicator on
    /// the next frame.
    /// </summary>
    private void TogglePause()
    {
        if (_session.Active.Paused) _session.Active.Resume();
        else _session.Active.Pause();
    }

    private void ApplyDisplayScale(int scale, bool persist = true)
    {
        if (scale < 1) scale = 1;
        if (scale > 3) scale = 3;
        int chrome = (MainMenuStrip?.Height ?? 0) + _status.Height;
        ClientSize = new Size(
            Mz700Video.PixelWidth * scale,
            Mz700Video.PixelHeight * scale + chrome);
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
            // ROMs, reset, sound, the --dump hooks and recorder, and the
            // autoload orchestrator. A missing monitor ROM throws, with
            // its message, into the catch below.
            _session.Start(() => new WinmmWaveOut(Sound.OutputFormat), _dumpPath, _dumpFrame);

            // Auto-load BASIC if requested explicitly (--basic) OR if the
            // initial cassette is a BASIC program (type 0x02 / 0x05), then
            // the cassette. A missing BASIC shows as a modal now, at
            // startup, rather than a quiet status line later.
            if (_session.QueueStartupLoad(_initialCassette, _autoLoadBasic)
                == EmulatorSession.LoadResult.BasicMissing)
                ShowBasicMissing();
            _pace.Restart();
            _pacedFrames = 0;
            _timer.Start();
            _statusLabel.Text = "Running.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to start emulator:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // Tracks the last observed IMachine.Paused so Timer_Tick can react
    // only on transitions — title-bar suffix + display overlay flip
    // together, without either dependency having to know about the
    // other. See RefreshPauseIndicator.
    private bool _wasPaused;
    private const string TitleBase = "MZRaku";
    // Autoload state (BASIC pending, cassette pending, BASIC source
    // pending, _basicLoadedFrame, MZ-80A cassette-autorun tracking,
    // _wasGraphMode) and the ready checks it waits on all live on the
    // session.

    // TAPE chip state tracking. Snapshots of the cassette's trap-hit
    // counters between frames so a fresh hit (delta) can extend the
    // flash window; _tapeFlashEndFrame is the deadline, in
    // _session.FrameCount terms, after which the chip decays back
    // to Steady (if a cassette is still queued) or Idle.
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
        // Shared trap-state reads collapse to IMachine.Cassette
        // (CassetteTrapBase surface). WriteTapeTrapHits is
        // MZ-700-only (SA-1510 SAVE traps not wired yet); MZ-80A
        // and MZ-800 score zero.
        var cass = _session.Active.Cassette;
        bool pending = cass.Pending != null;
        int headerHits = cass.HeaderTrapHits;
        int dataHits   = cass.DataTrapHits;
        int writeHits  = _session.Mz700?.Cassette.WriteTapeTrapHits ?? 0;

        // Any fresh trap hit extends the flash window. Also handles the
        // MZ-700 SAVE path (WriteTapeTrapHits) even though it has no
        // Pending image.
        if (headerHits != _tapeLastHeaderHits ||
            dataHits   != _tapeLastDataHits   ||
            writeHits  != _tapeLastWriteHits)
        {
            _tapeFlashEndFrame  = _session.FrameCount + TapeFlashFrames;
            _tapeLastHeaderHits = headerHits;
            _tapeLastDataHits   = dataHits;
            _tapeLastWriteHits  = writeHits;
        }

        TapeState want =
            _session.FrameCount < _tapeFlashEndFrame ? TapeState.Flash :
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
    /// React to IMachine.Paused changes: title-bar suffix + display
    /// invalidate so Display_Paint's overlay flips in sync. Runs every
    /// frame; the transition guard keeps it cheap when the state's
    /// steady.
    /// </summary>
    private void RefreshPauseIndicator()
    {
        bool paused = _session.Active.Paused;
        if (paused == _wasPaused) return;
        _wasPaused = paused;
        Text = paused ? TitleBase + " — PAUSED" : TitleBase;
        if (_pauseMenuItem != null) _pauseMenuItem.Checked = paused;
        // Silence the wave output on pause. IMachine.Sound covers all
        // three machines (F-061).
        _session.Active.Sound.Muted = paused;
        _display.Invalidate();
    }

    /// <summary>
    /// Updates the right-hand mode indicator. <paramref name="graph"/>
    /// null → grey "—" (mode unknowable / not-yet-meaningful), false
    /// → normal "ALPHA", true → highlighted "GRAPH". Handles the
    /// session's GraphModeSampled, raised every 10 frames on the MZ-700
    /// and MZ-80A.
    /// </summary>
    private void UpdateModeLabel(bool? graph) =>
        SetModeLabel(graph is null ? "—" : graph.Value ? "GRAPH" : "ALPHA");

    // "—" greyed (mode unknowable), "GRAPH" highlighted, "LOCK" (MZ-800
    // shift lock) highlighted, anything else plain.
    private void SetModeLabel(string text)
    {
        if (_modeLabel.Text == text) return;
        _modeLabel.Text = text;
        (_modeLabel.ForeColor, _modeLabel.BackColor) = text switch
        {
            "—"     => (SystemColors.GrayText, SystemColors.Control),
            "GRAPH" => (Color.White, Color.MediumVioletRed),
            "LOCK"  => (Color.White, Color.SteelBlue),
            _       => (SystemColors.ControlText, SystemColors.Control),
        };
    }

    /// <summary>
    /// MZ-800 status panes, called every 10 frames. The mode pane shows
    /// MZ-800 BASIC's keyboard mode (ALPHA / LOCK for shift lock /
    /// GRAPH, "—" when 1Z-016 isn't running). The display-mode pane
    /// shows "MZ-700" (compatibility text mode), "320×200" or
    /// "640×200", since titles switch between them and it's otherwise
    /// invisible.
    /// </summary>
    private void UpdateMz800ModeLabel()
    {
        SetModeLabel(_session.Mz800!.BasicKeyboardMode switch
        {
            0 => "ALPHA",
            1 => "LOCK",
            2 => "GRAPH",
            _ => "—",
        });

        var m = _session.Mz800.Mem;
        string text = m.Mz700Mode ? "MZ-700" : m.Is640BitmapMode ? "640×200" : "320×200";
        if (_displayModeLabel.Text == text) return;
        _displayModeLabel.Text = text;
        _displayModeLabel.ToolTipText = m.Mz700Mode
            ? "Display mode: MZ-700 compatibility (40×25 text)"
            : $"Display mode: MZ-800 {text} bitmap";
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
        if (_session.FrameCount - _statusLastSetFrame < StatusIdleFrames) return;
        _statusLabel.Text = "";   // TextChanged resets _statusLastSetFrame
    }

    /// <summary>
    /// Phase 5.3: after the main window is first shown, honour the
    /// [DebugPanes] boot flags by auto-opening each flagged pane via
    /// its existing open handler. Panes that don't apply to the active
    /// machine are silently skipped — the setting survives so switching machines
    /// restores them next boot.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var dp = _settings.DebugPanesAtStartup;
        if (dp.Debugger) OpenDebugger();
        if (dp.MemoryViewer) OpenMemoryViewer();
        if (dp.HidDiagnostic) OpenHidDiag();
        if (dp.FontSheet) OpenFontSheet();
        if (dp.SoundDiagnostic && SoundDiagnosticForm.Supports(_session.Active.Kind)) OpenSoundDiag();
        if (dp.KeyboardMatrix) OpenKeyboardMatrix();
        // Return focus to the main window so the emulator gets input
        // events; the last pane opened would otherwise steal it. Not
        // for --dump runs, which take no keyboard input (see Shown).
        if (_dumpPath == null) Activate();
    }

    // Frame pacing. The WinForms timer can't deliver 60 ticks/s — on
    // Windows 11 a 16 ms interval measured ~40 ticks/s even with a 1 ms
    // system timer resolution, so one-frame-per-tick ran every machine
    // at ~2/3 speed. Each tick now runs as many emulated frames as
    // real elapsed time calls for (usually 1-2) against a Stopwatch;
    // UI refresh happens once per tick. Found in v1.3.0 Phase 6.1 when
    // the MZ-800 PSG, rendered in emulated time, underran its buffer.
    private readonly System.Diagnostics.Stopwatch _pace = new();
    private long _pacedFrames;
    // More than this many frames behind (a debugger stall, window drag,
    // a slow machine) → resync to now instead of fast-forwarding.
    private const int MaxCatchUpFrames = 4;

    private void Timer_Tick(object? s, EventArgs e)
    {
        long due = (long)(_pace.Elapsed.TotalSeconds * _session.Active.FramesPerSecond);
        long behind = due - _pacedFrames;
        if (_session.Active.Paused || behind > MaxCatchUpFrames)
        {
            // Paused: RunFrame only re-renders, one per tick is plenty.
            _pacedFrames = due - 1;
            behind = 1;
        }
        for (long i = 0; i < behind; i++)
        {
            _pacedFrames++;
            _session.RunFrame();
            if (IsDisposed) return;   // --dump completion closes the form
        }
        if (behind <= 0) return;

        _debugger?.RefreshIfVisible();
        _memViewer?.RefreshIfVisible();
        RevertStatusIfIdle();
        UpdateTapeLabel();
        RefreshPauseIndicator();

        _hidDiag?.RefreshIfVisible();
        _soundDiag?.RefreshIfVisible();
        _display.Invalidate();
    }

    private readonly int _dumpFrame;

    // The core's frame buffer copied into a bitmap for GDI+ to scale;
    // recreated only when the frame size changes (MZ-800 320 ↔ 640).
    private Bitmap? _displayBitmap;

    private void Display_Paint(object? sender, PaintEventArgs e)
    {
        // Machine-agnostic framebuffer pick via IMachine.VideoFrame
        // (F-061). MZ-700 + MZ-80A + MZ-800 320-mode/MZ-700-mode all
        // hand back a 320×200 frame buffer; MZ-800 640-mode hands
        // back a 640×200 one. Scaling uses a fixed 320×200 reference
        // area regardless of which frame came back — that matches how
        // real MZ-800 CRT hardware paints 320-mode pixels 2× wide and
        // 640-mode pixels at native density into the same physical
        // display area.
        var frameBuffer = _session.Active.VideoFrame;
        if (frameBuffer == null)
        {
            e.Graphics.Clear(Color.Black);
            return;
        }
        if (_displayBitmap == null || _displayBitmap.Width != frameBuffer.Width || _displayBitmap.Height != frameBuffer.Height)
        {
            _displayBitmap?.Dispose();
            _displayBitmap = FrameBufferBitmap.ToBitmap(frameBuffer);
        }
        else
        {
            FrameBufferBitmap.CopyTo(frameBuffer, _displayBitmap);
        }
        var frame = _displayBitmap;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.SmoothingMode = SmoothingMode.None;

        var cr = _display.ClientRectangle;
        const int RefWidth = Mz700Video.PixelWidth;   // 320 — the CRT's "column count" reference
        const int RefHeight = Mz700Video.PixelHeight; // 200
        float sx = (float)cr.Width / RefWidth;
        float sy = (float)cr.Height / RefHeight;
        float scale = Math.Min(sx, sy);
        int w = (int)(RefWidth * scale);
        int h = (int)(RefHeight * scale);
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
        if (_session.Active.Paused)
        {
            using var pauseDim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            e.Graphics.FillRectangle(pauseDim, x, y, w, h);
        }
    }

    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    /// <summary>
    /// One --keys= event, through the same path as a real keystroke:
    /// ProcessCmdKey (hotkeys and menu shortcuts) first, then KeyDown
    /// and KeyPress unless it consumed the key, and KeyUp.
    /// </summary>
    private void InjectScriptedKey(KeyScript.Event ev)
    {
        var keyData = (Keys)ev.KeyData;
        if (ev.Kind == KeyScript.EventKind.Up)
        {
            OnKeyUp(this, new KeyEventArgs(keyData));
            return;
        }
        int wm = (keyData & Keys.Alt) != 0 ? WM_SYSKEYDOWN : WM_KEYDOWN;
        var msg = Message.Create(Handle, wm, (IntPtr)(int)(keyData & Keys.KeyCode), IntPtr.Zero);
        if (ProcessCmdKey(ref msg, keyData)) return;
        OnKeyDown(this, new KeyEventArgs(keyData));
        if (ev.Text is char c) OnKeyPress(this, new KeyPressEventArgs(c));
    }

    // Key events go to the session (which ignores them during --dump
    // runs). KeyData keeps the modifier flags; HostKey has Keys'
    // values, so a cast converts it.
    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (_session.KeyDown((HostKey)e.KeyData)) e.Handled = true;
    }

    private void OnKeyPress(object? s, KeyPressEventArgs e) => _session.KeyPress(e.KeyChar);

    private void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (_session.KeyUp((HostKey)e.KeyData)) e.Handled = true;
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

    /// <summary>
    /// File → Insert cassette for LOAD (see
    /// <see cref="EmulatorSession.InsertCassette"/>). The MZ-80A is
    /// filtered out with an explanation.
    /// </summary>
    private void BrowseAndQueue()
    {
        if (!_session.CanInsertCassette)
        {
            MessageBox.Show(this,
                "Insert cassette is available on MZ-700 and MZ-800 only.\n" +
                "MZ-80A cassette LOAD runs through BASIC's SA-5510, not " +
                "the SA-1510 monitor's L command — use Load cassette instead.",
                "Not supported on this machine",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Filter = "MZ cassette images (*.mzf;*.m12;*.mzt;*.zip)|*.mzf;*.m12;*.mzt;*.zip|All files|*.*",
            Title = "Insert cassette for LOAD"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _session.InsertCassette(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to queue cassette:\n" + ex.Message,
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // File → Load cassette and drag-and-drop.
    private void LoadCassetteFile(string path)
    {
        try
        {
            if (_session.LoadCassette(path) == EmulatorSession.LoadResult.BasicMissing)
                ShowBasicMissing();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to load cassette:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadBasic()
    {
        try
        {
            if (_session.LoadBasic() == EmulatorSession.LoadResult.BasicMissing)
                ShowBasicMissing();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "BASIC load failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// The modal for a load the session refused because the BASIC image
    /// is missing (<see cref="EmulatorSession.LoadResult.BasicMissing"/>).
    /// </summary>
    private void ShowBasicMissing()
    {
        MessageBox.Show(this, _session.BasicMissingMessage,
            "BASIC not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    // File → Load BASIC source (see EmulatorSession.LoadBasicSource).
    private void LoadBasicSourceFile(string path)
    {
        try
        {
            if (_session.LoadBasicSource(path) == EmulatorSession.LoadResult.BasicMissing)
                ShowBasicMissing();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to load BASIC source:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SwitchMachine(MachineType target)
    {
        if (target == _settings.CurrentMachine) return; // already on it — no-op
        var name = target switch
        {
            MachineType.MZ700 => "MZ-700",
            MachineType.MZ80A => "MZ-80A",
            MachineType.MZ800 => "MZ-800",
            _ => target.ToString(),
        };
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
            if (a.Equals("--mz800", StringComparison.OrdinalIgnoreCase)) continue;
            psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add(target switch
        {
            MachineType.MZ700 => "--mz700",
            MachineType.MZ80A => "--mz80a",
            MachineType.MZ800 => "--mz800",
            _ => "--mz700",
        });
        System.Diagnostics.Process.Start(psi);
        // Close() runs the FormClosing handler that snapshots the window
        // rectangle into _settings.MainWindow and persists via Save().
        // Previously Environment.Exit(0) here bypassed that handler and
        // the switched-into machine's window jumped to the default spot.
        Close();
    }

    private void OpenSettings(SettingsForm.Tab tab = SettingsForm.Tab.Startup)
    {
        using var dlg = new SettingsForm(_settings, _joystickInput, _session.Mz700, tab, _session.Mz80a, _session.Mz800);
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
        // The machine side: MZ-80A screen colour and letter shift,
        // MZ-800 VRAM expansion, gamepad buttons.
        _session.ApplySettings();
        _display.Invalidate();
    }

    private void OpenDebugger()
    {
        if (_debugger == null || _debugger.IsDisposed)
        {
            _debugger = new DebuggerForm(_session.Active, _session.Reset, _settings);
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
            _memViewer = new MemoryViewerForm(_session.Active, _settings);
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
        if (_fontSheet == null || _fontSheet.IsDisposed)
            _fontSheet = new FontSheetForm(_session.Active);
        _fontSheet.Owner = this;
        _fontSheet.Show();
        _fontSheet.BringToFront();
    }

    private void OpenKeyboardMatrix()
    {
        // Build the editor context for whichever machine is running.
        // KeyboardMatrixForm itself has been machine-agnostic since it
        // was ported to IKeyboardEditorContext; the MZ-80A refusal here
        // was a pre-Phase-5.5a artefact.
        IKeyboardEditorContext? context =
            _session.Mz700 != null ? new Mz700KeyboardEditorContext(_session.Mz700, _settings.Mz700CharMapOverrides, _settings.Mz700KeyOverrides) :
            _session.Mz80a != null ? new Mz80aKeyboardEditorContext(_session.Mz80a, _settings.Mz80aCharMapOverrides, _settings.Mz80aKeyOverrides) :
            _session.Mz800 != null ? new Mz800KeyboardEditorContext(_session.Mz800, _settings.Mz800CharMapOverrides, _settings.Mz800KeyOverrides) :
            null;
        if (context == null) return;
        if (_matrixForm == null || _matrixForm.IsDisposed)
            _matrixForm = new KeyboardMatrixForm(context);
        _matrixForm.Owner = this;
        // Deliberately not BringToFront — that activates the window and
        // steals focus from the emulator; we want to watch the highlight
        // pulse while typing into the main window.
        _matrixForm.Show();
    }

    private void OpenHidDiag()
    {
        // Pick whichever machine is active. MZ-700 / MZ-80A since the
        // form was made machine-aware (v1.1.0 Phase 4); MZ-800 since
        // v1.3.0 Phase 8.
        IMachine active = _session.Active;
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
        if (!SoundDiagnosticForm.Supports(_session.Active.Kind)) { NotAvailableOnThisMachine("Sound Diagnostic", "MZ-700 and MZ-800"); return; }
        if (_soundDiag == null || _soundDiag.IsDisposed)
        {
            _soundDiag = new SoundDiagnosticForm(_session.Active);
            _soundDiag.Location = new Point(Bounds.Right + 8, Bounds.Top);
        }
        _soundDiag.Owner = this;
        _soundDiag.Show();
        // Same focus-preserving pattern as the HID Diagnostic — keep
        // typing from the main window unobstructed.
        Activate();
    }

    /// <summary>
    /// Shows a friendly "not available on this machine" popup for a
    /// diagnostic pane that reaches into machine-specific hardware.
    /// <paramref name="machines"/> names the machines it does support.
    /// </summary>
    private void NotAvailableOnThisMachine(string pane, string machines)
    {
        MessageBox.Show(this,
            $"{pane} is currently available on the {machines} only; it isn't available on the {_session.MachineLabel} yet.",
            "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_MENU = 0x12;          // Alt key
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
