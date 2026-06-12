using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

public sealed class MainForm : Form
{
    private readonly MZ700 _machine = new();
    private readonly Hardware.JoystickInput _joystickInput;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _joyStatus = new() { Spring = false };
    private readonly ToolStripStatusLabel _modeLabel = new()
    {
        Spring = false,
        Text = "ALPHA",
        AutoSize = false,
        Width = 56,
        TextAlign = ContentAlignment.MiddleCenter,
    };
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
    private FontSheetForm? _fontSheet;
    // Tracks the previous GRAPH-mode bit so we can detect ALPHA→GRAPH
    // transitions and auto-surface the Font Sheet — the only way to
    // reach MZ-only graphic glyphs (no PC-key equivalent exists). Opens
    // unconditionally on each transition (if not already visible) since
    // GRAPH mode is effectively unusable without it.
    private bool _wasGraphMode;
    private KeyboardMatrixForm? _matrixForm;

    public MainForm(string? cassettePath, bool autoLoadBasic, string? dumpPath = null)
    {
        _initialCassette = cassettePath;
        _autoLoadBasic = autoLoadBasic;
        _dumpPath = dumpPath;
        _joystickInput = new Hardware.JoystickInput(_machine.Joystick);
        _joystickInput.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
        _machine.Keyboard.Overrides = _settings.KeyOverrides;
        CharMap.Overrides = _settings.CharMapOverrides;

        Text = "Sharp MZ-700 Emulator";
        Icon = LoadEmbeddedIcon();
        KeyPreview = true;
        AllowDrop = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
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

        _statusLabel.Spring = true;
        // ToolStripStatusLabel defaults to MiddleCenter; status messages
        // should sit consistently at the left edge so they're predictable
        // to read and don't jump as text length changes.
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_joyStatus);
        _status.Items.Add(_modeLabel);
        _statusLabel.Text = "Ready.";
        _joyStatus.Text = "Joy: --";

        // SAVE-tape trap surfaces save outcomes via this event. Fires on
        // the UI thread (OnPreStep is called from Timer_Tick → RunFrame),
        // so we can touch the status label directly.
        _machine.Cassette.OnSaved += msg => _statusLabel.Text = msg;
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
        ApplyDisplayScale(_settings.DisplayScale);

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
        };
        FormClosing += (_, _) => { _timer.Stop(); _debugger?.Dispose(); _memViewer?.Dispose(); _hidDiag?.Dispose(); _machine.Sound.Dispose(); };
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
        file.DropDownItems.Add(new ToolStripMenuItem("&Settings…", null, (_, _) => OpenSettings()) { ShortcutKeys = Keys.Control | Keys.S });
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
        debug.DropDownItems.Add(new ToolStripMenuItem("&Debugger…", null, (_, _) => OpenDebugger()) { ShortcutKeys = Keys.Control | Keys.D });
        debug.DropDownItems.Add(new ToolStripMenuItem("&Memory Viewer…", null, (_, _) => OpenMemoryViewer()) { ShortcutKeys = Keys.Control | Keys.M });
        debug.DropDownItems.Add(new ToolStripMenuItem("&HID Diagnostic…", null, (_, _) => OpenHidDiag()) { ShortcutKeys = Keys.Control | Keys.H });
        debug.DropDownItems.Add(new ToolStripMenuItem("Keyboard &Matrix…", null, (_, _) => OpenKeyboardMatrix()));
        debug.DropDownItems.Add(new ToolStripSeparator());
        debug.DropDownItems.Add(new ToolStripMenuItem("Run &Z80 Test (ZEXDOC/ZEXALL)…", null, (_, _) => OpenZ80Test()));
        menu.Items.Add(debug);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About…", null, (_, _) =>
        {
            using var dlg = new AboutForm();
            dlg.ShowDialog(this);
        }));
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void ApplyDisplayScale(int scale)
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
        if (_settings.DisplayScale != scale)
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
                var configured = string.IsNullOrEmpty(_settings.MonitorRomPath)
                    ? "(none configured)"
                    : $"{_settings.MonitorRomPath}  →  {_settings.MonitorRomFullPath}";
                throw new FileNotFoundException(
                    $"Monitor ROM (1z-013a.rom) not found.\n\n" +
                    $"Configured path: {configured}\n\n" +
                    $"Place 1z-013a.rom under a 'roms' folder next to the executable, " +
                    $"or set [Roms] Monitor= in {Path.Combine(AppContext.BaseDirectory, "settings.ini")}.");
            }
            _machine.LoadRoms(_settings.MonitorRomFullPath, _settings.FontFullPath);
            _machine.Reset();
            _machine.Cpu.PcTraceEnabled = _traceEnabled;
            _machine.Pit.WriteLog = _traceEnabled ? new System.Text.StringBuilder() : null;
            _machine.Mem.BankSwitchLog = _traceEnabled ? new System.Text.StringBuilder() : null;
            _machine.Sound.Start();

            // Auto-load BASIC if requested explicitly (--basic) OR if the
            // initial cassette is a BASIC program (type 0x02 / 0x05). The
            // type peek means the user doesn't have to know whether a
            // given .mzf is MC or BASIC — they just point the emulator at
            // it and the right boot path runs.
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
    private bool _monitorReady;
    private int _basicLoadedFrame = -1;

    /// <summary>
    /// Detect "monitor finished booting" by spotting the "MONITOR 1Z*"
    /// banner the 1Z-013A ROM writes to VRAM row 0 once init is complete
    /// and the keyboard input loop is active. This is exactly the state
    /// BASIC's startup at $7D79 needs — the call into monitor at $0033
    /// works cleanly only after the monitor has set up its stack and
    /// stopped clearing RAM around $1200.
    /// </summary>
    private bool MonitorReady()
    {
        if (_monitorReady) return true;
        var v = _machine.Mem.Vram;
        // MZ display codes: M=$0D O=$0F N=$0E I=$09 T=$14 1=$21 Z=$1A *=$2A
        if (v[4] == 0x0D && v[5] == 0x0F && v[6] == 0x0E && v[7] == 0x09 &&
            v[8] == 0x14 && v[9] == 0x0F && v[10] == 0x12 &&
            v[12] == 0x21 && v[13] == 0x1A && v[14] == 0x2A)
        {
            _monitorReady = true;
        }
        return _monitorReady;
    }
    private void Timer_Tick(object? s, EventArgs e)
    {
        // Sample real gamepad state once per frame, before the emulated
        // CPU runs — values get latched at the VBLK falling edge inside
        // RunFrame, so they need to be fresh by then.
        _joystickInput.Poll();
        _machine.RunFrame();
        _bootFrames++;
        _debugger?.RefreshIfVisible();
        _memViewer?.RefreshIfVisible();
        _hidDiag?.RefreshIfVisible();

        // Refresh joystick indicator every ~10 frames (~6 Hz) — enough
        // to confirm at a glance whether XInput is seeing a controller.
        if (_bootFrames % 10 == 0)
        {
            var s0 = _machine.Joystick.Sticks[0];
            var s1 = _machine.Joystick.Sticks[1];
            string Fmt(Hardware.Joystick.StickState s, int n) =>
                s.Active
                    ? $"{n}[X{s.AxisX:D3} Y{s.AxisY:D3}{(s.Sw1 ? " A" : "")}{(s.Sw2 ? " B" : "")}]"
                    : $"{n}-";
            _joyStatus.Text = $"Joy: {Fmt(s0, 1)} {Fmt(s1, 2)}";

            // S-BASIC's keyboard mode flag, discovered empirically via the
            // memory-viewer snapshot/diff tool 2026-05-31: bit 4 of $0060
            // set = GRAPH mode, cleared = ALPHA. Only meaningful while
            // S-BASIC owns the machine (ROM banked out so $0060 is RAM);
            // before BASIC is loaded, $0060 reads from ROM and the
            // indicator would be misleading, so we grey it out with "—".
            if (_basicLoadedFrame < 0)
            {
                if (_modeLabel.Text != "—")
                {
                    _modeLabel.Text = "—";
                    _modeLabel.ForeColor = SystemColors.GrayText;
                    _modeLabel.BackColor = SystemColors.Control;
                }
            }
            else
            {
                bool graph = (_machine.Mem.Read(0x0060) & 0x10) != 0;
                if (graph && _modeLabel.Text != "GRAPH")
                {
                    _modeLabel.Text = "GRAPH";
                    _modeLabel.ForeColor = Color.White;
                    _modeLabel.BackColor = Color.MediumVioletRed;
                }
                else if (!graph && _modeLabel.Text != "ALPHA")
                {
                    _modeLabel.Text = "ALPHA";
                    _modeLabel.ForeColor = SystemColors.ControlText;
                    _modeLabel.BackColor = SystemColors.Control;
                }
                // ALPHA→GRAPH transition: surface the Font Sheet, since
                // graphic glyphs aren't reachable from any PC key. Opens
                // every transition (if not already visible) — GRAPH mode
                // is effectively unusable without the palette, so always
                // putting it in front is the right default.
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
                    _machine.Cassette.DirectInject(img, jumpExec: false);
                    if (img.Type == 0x02 || img.Type == 0x05)
                    {
                        _machine.Cassette.FixupBasicProgramPointers(img.LoadAddr, img.Data.Length);
                        // Auto-RUN: with the program injected and pointers
                        // fixed, BASIC's RUN command preprocesses lengths and
                        // starts execution. End-to-end automation from CLI.
                        _machine.Keyboard.TypeString("RUN\r");
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
            var c0 = _machine.Pit.Counters[0];
            var c2 = _machine.Pit.Counters[2];
            _traceLog.AppendLine($"[F{_bootFrames:D4}] PC=${_machine.Cpu.PC:X4} SP=${_machine.Cpu.SP:X4} IFF1={_machine.Cpu.IFF1} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} run={c2.Running} out={c2.Out} INTMSK={_machine.Ppi.InterruptMask} hdr={_machine.Cassette.HeaderDelivered} dat={_machine.Cassette.DataDelivered}");
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
                    int start = _machine.Cpu.PcTraceIdx;
                    for (int i = 0; i < _machine.Cpu.PcTrace.Length; i++)
                    {
                        sb.Append($"${_machine.Cpu.PcTrace[(start + i) & 0xFF]:X4} ");
                        if (i % 16 == 15) sb.AppendLine();
                    }
                    _traceLog.Append(sb);
                    if (_machine.Pit.WriteLog != null)
                    {
                        _traceLog.AppendLine();
                        _traceLog.AppendLine("PIT write log:");
                        _traceLog.Append(_machine.Pit.WriteLog);
                    }
                    if (_machine.Mem.BankSwitchLog != null)
                    {
                        _traceLog.AppendLine();
                        _traceLog.AppendLine("Bank-switch log:");
                        _traceLog.Append(_machine.Mem.BankSwitchLog);
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
        w.WriteLine($"CPU: PC=${_machine.Cpu.PC:X4} SP=${_machine.Cpu.SP:X4} A=${_machine.Cpu.A:X2} F=${_machine.Cpu.F:X2} HL=${_machine.Cpu.HL:X4} BC=${_machine.Cpu.BC:X4} DE=${_machine.Cpu.DE:X4}");
        w.WriteLine($"IM={_machine.Cpu.IM} IFF1={_machine.Cpu.IFF1} Halted={_machine.Cpu.Halted} Cycles={_machine.Cpu.TotalCycles}");
        w.WriteLine($"PPI PortA=${_machine.Ppi.PortA:X2} PortCOut=${_machine.Ppi.PortCOut:X2} PortCIn=${_machine.Ppi.PortCIn:X2}");
        w.WriteLine($"Mem RomEnabled={_machine.Mem.RomEnabled} VramIoEnabled={_machine.Mem.VramIoEnabled}");
        w.WriteLine($"PIT C0.Reload={_machine.Pit.Counters[0].Reload} C2.Reload={_machine.Pit.Counters[2].Reload}");
        var sb0 = new System.Text.StringBuilder("RAM @ $1200: ");
        for (int i = 0; i < 32; i++) sb0.Append($"{_machine.Mem.Read((ushort)(0x1200 + i)):X2} ");
        w.WriteLine(sb0.ToString());
        w.WriteLine($"Tape trap hits: BreakWait={_machine.Cassette.BreakWaitTrapHits} Header={_machine.Cassette.HeaderTrapHits} Data={_machine.Cassette.DataTrapHits} WriteTape={_machine.Cassette.WriteTapeTrapHits}");
        w.WriteLine();
        w.WriteLine("VRAM (40x25 text codes):");
        for (int row = 0; row < 25; row++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{row:D2}] ");
            for (int col = 0; col < 40; col++)
            {
                byte b = _machine.Mem.Vram[row * 40 + col];
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
                byte b = _machine.Mem.Vram[row * 40 + col];
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
        var frame = _machine.Video.Frame;
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
    }

    private static bool IsShiftKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        // e.Shift can momentarily lag on the very first shift keydown, so
        // also detect via the VK code itself.
        bool shift = e.Shift || IsShiftKey(e.KeyCode);
        _machine.Keyboard.SetShift(shift);
        // Pass KeyData (VK + modifier flags) so the override layer can
        // match modifier-aware bindings; Keyboard internally strips
        // modifiers when consulting SpecialKeyMap and managing holds.
        if (_machine.Keyboard.OnKeyDown(e.KeyData, shift)) e.Handled = true;
    }

    private void OnKeyPress(object? s, KeyPressEventArgs e)
    {
        _machine.Keyboard.OnKeyPress(e.KeyChar);
    }

    private void OnKeyUp(object? s, KeyEventArgs e)
    {
        bool shift = e.Shift && !IsShiftKey(e.KeyCode);
        _machine.Keyboard.SetShift(shift);
        if (_machine.Keyboard.OnKeyUp(e.KeyData, shift)) e.Handled = true;
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
                _machine.Cassette.DirectInject(img, jumpExec: true);
                _statusLabel.Text = $"Loaded & run: {img.Filename} exec=${img.ExecAddr:X4}";
            }
            else
            {
                // Other type at the monitor: queue for monitor LOAD command.
                _machine.Cassette.Queue(img);
                _statusLabel.Text = $"Queued: {img.Filename}. Type LOAD to fetch.";
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
            _machine.AutoLoadBasic(_settings.BasicFullPath);
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
            _machine.Keyboard.TypeString(line + "\r");
        }
    }

    private void ResetMachine()
    {
        _machine.Reset();
        _bootFrames = 0;
        _monitorReady = false;
        _basicLoadedFrame = -1;
        _statusLabel.Text = "Reset.";
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings, _joystickInput, _machine);
        dlg.Applied += OnSettingsApplied;
        dlg.ShowDialog(this);
    }

    private void OnSettingsApplied()
    {
        // Display scale change: re-size the client area and update the
        // View menu's checked state. ApplyDisplayScale is a no-op if the
        // value didn't actually change.
        ApplyDisplayScale(_settings.DisplayScale);
        // Joystick button bindings can be re-pushed live; ROM paths take
        // effect on the next Reset, so we don't touch the running machine.
        _joystickInput.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
    }

    private void OpenDebugger()
    {
        if (_debugger == null || _debugger.IsDisposed)
        {
            _debugger = new DebuggerForm(_machine, ResetMachine);
            // First open: park it just to the right of the main window.
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
            _memViewer = new MemoryViewerForm(_machine);
            // First open: park it below the main window so it doesn't fight
            // the debugger for the right-side slot.
            _memViewer.Location = new Point(Bounds.Left, Bounds.Bottom + 8);
        }
        _memViewer.Owner = this;
        _memViewer.Show();
        _memViewer.BringToFront();
    }

    private void OpenFontSheet()
    {
        if (_fontSheet == null || _fontSheet.IsDisposed)
            _fontSheet = new FontSheetForm(_machine);
        _fontSheet.Owner = this;
        _fontSheet.Show();
        _fontSheet.BringToFront();
    }

    private void OpenKeyboardMatrix()
    {
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
        if (_hidDiag == null || _hidDiag.IsDisposed)
        {
            _hidDiag = new HidDiagnosticForm(_machine, _joystickInput);
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

    private void OpenZ80Test()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Z80 test .com (ZEXDOC / ZEXALL)",
            Filter = "CP/M COM|*.com|All files|*.*",
            InitialDirectory = FindToolsCpmDir() ?? AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Stop the 60Hz tick so the test thread has exclusive access to
        // the CPU. Resumed when the test form closes.
        _timer.Stop();
        var form = new Z80TestForm(_machine, dlg.FileName);
        form.FormClosed += (_, _) => _timer.Start();
        form.Owner = this;
        form.Show(this);
    }

    private static string? FindToolsCpmDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tools", "CPM");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("MZ700Emul.ico", StringComparison.OrdinalIgnoreCase));
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
