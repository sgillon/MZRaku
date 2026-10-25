using System;
using System.IO;

namespace MZRaku.Core;

/// <summary>
/// One running machine and everything that drives it, independent of
/// any front end (v1.4 design decision D8). A session holds a single
/// machine for its whole lifetime: switching machine means a new
/// session (the WinForms front end relaunches the process).
///
/// Built in v1.4 Phase 2 out of MainForm. It owns creating the machine
/// and applying its settings, start (ROMs, sound, the --dump hooks and
/// recorder, autoload), reset, the ready checks autoload waits on,
/// running frames, loading cassettes, BASIC and BASIC source, host keys
/// and the --keys script, and applying changed settings. The front end
/// keeps the window, dialogs, frame pacing, pause and the audio and
/// gamepad devices it hands in.
/// </summary>
public sealed class EmulatorSession
{
    private readonly Settings _settings;

    /// <summary>
    /// Create the machine <see cref="Settings.CurrentMachine"/> names and
    /// apply its settings. ROM paths are resolved first (and saved if
    /// that found new ones); the ROMs themselves load at start.
    /// </summary>
    public EmulatorSession(Settings settings)
    {
        _settings = settings;
        // EnsureRomPaths scans for both machines' ROMs, so the CLI
        // override finding e.g. SA-1510.rom under roms/ works even
        // when settings.ini had never seen an MZ-80A launch before.
        // No-op if the previous Load() already populated both sides.
        if (_settings.EnsureRomPaths()) _settings.Save();
        // Construct exactly one of the three machines.
        if (_settings.CurrentMachine == MachineType.MZ700)
            Mz700 = new MZ700();
        else if (_settings.CurrentMachine == MachineType.MZ80A)
        {
            Mz80a = new MZ80A();
            Mz80a.Keyboard.InvertLetterShift = _settings.Mz80aInvertLetterShift;
            ApplyMz80aScreenColor();
        }
        else /* MachineType.MZ800 */
        {
            Mz800 = new MZ800();
            Mz800.Mem.VramExpansion = _settings.Mz800VramExpansion;
        }
        Active = (IMachine?)Mz700 ?? (IMachine?)Mz80a ?? Mz800!;

        // A host gamepad feeds the machine's stick state: the MZ-700's
        // MZ-1X03 or the MZ-800's $F0/$F1 ports. The MZ-80A has no
        // joystick; a throwaway keeps Joystick non-null for the front
        // end's gamepad source, and frame stepping doesn't poll it.
        Joystick = Mz700?.Joystick ?? Mz800?.Joystick ?? new Joystick();

        // Keyboard-override wiring. All three machines have the same
        // 10-strobe × 8-bit matrix topology so KeyOverride is shared;
        // Settings persists one instance per machine at
        // [KeyOverrides.MZ700] / [KeyOverrides.MZ80A] / [KeyOverrides.MZ800].
        // Char-map layers are split too: CharMap, Mz80aCharMap and
        // Mz800CharMap.
        if (Mz700 != null)
        {
            Mz700.Keyboard.Overrides = _settings.KeyOverrides;
            CharMap.Overrides = _settings.CharMapOverrides;
        }
        if (Mz80a != null)
        {
            Mz80a.Keyboard.Overrides = _settings.Mz80aKeyOverrides;
        }
        Mz80aCharMap.Overrides = _settings.Mz80aCharMapOverrides;
        if (Mz800 != null)
        {
            Mz800.Keyboard.Overrides = _settings.Mz800KeyOverrides;
        }
        Mz800CharMap.Overrides = _settings.Mz800CharMapOverrides;

        // Cassette trap events surface load / save outcomes as status
        // messages. They fire during RunFrame, on the caller's thread.
        // OnSaved is MZ-700-only for now — Mz80aCassette doesn't yet
        // expose SAVE traps. OnLoaded fires on all three machines.
        if (Mz700 != null)
        {
            Mz700.Cassette.OnSaved += msg => StatusMessage?.Invoke(msg);
            Mz700.Cassette.OnLoaded += msg => StatusMessage?.Invoke(msg);
        }
        if (Mz80a != null)
        {
            Mz80a.Cassette.OnLoaded += msg => StatusMessage?.Invoke(msg);
        }
        if (Mz800 != null)
        {
            Mz800.Cassette.OnLoaded += msg => StatusMessage?.Invoke(msg);
        }
    }

    /// <summary>A short message for the user, such as a load result.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>
    /// A failure the user must acknowledge (BASIC failing to load); the
    /// WinForms front end shows it as a dialog.
    /// </summary>
    public event Action<string>? FatalError;

    /// <summary>
    /// The MZ-700 or MZ-800 keyboard just switched from ALPHA to GRAPH.
    /// Graphic glyphs have no PC key, so the front end surfaces its
    /// Font Sheet.
    /// </summary>
    public event Action? GraphModeEntered;

    /// <summary>
    /// The MZ-700 / MZ-80A GRAPH-mode state, sampled every 10 frames:
    /// null when it can't be known yet (BASIC not loaded), false for
    /// ALPHA, true for GRAPH.
    /// </summary>
    public event Action<bool?>? GraphModeSampled;

    /// <summary>A --dump run has written its files and is finished.</summary>
    public event Action? DumpComplete;

    /// <summary>
    /// A frame has run and autoload has had its turn; scripted keys and
    /// the dump come after. Carries <see cref="FrameCount"/>.
    /// </summary>
    public event Action<int>? FrameRan;

    /// <summary>Frames run since start or the last <see cref="Reset"/>.</summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// Set once <see cref="Stop"/> has been called; <see cref="RunFrame"/>
    /// then does nothing.
    /// </summary>
    public bool Stopped { get; private set; }

    /// <summary>
    /// The host gamepad, polled once per frame on the MZ-700 and MZ-800
    /// (never during a --dump run). Null for none.
    /// </summary>
    public IJoystickSource? JoystickSource { get; set; }

    private KeyScript? _keyScript;
    private Action<KeyScript.Event>? _deliverScriptedKey;
    private bool _deliveringScriptedKey;

    /// <summary>
    /// Play a --keys script: after each frame, the events now due go to
    /// <paramref name="deliver"/>, which feeds them in as host keys
    /// (through <see cref="KeyDown"/>, <see cref="KeyPress"/> and
    /// <see cref="KeyUp"/>, after any front-end hotkeys). Keys it
    /// delivers get through even when <see cref="IgnoresHostKeys"/>.
    /// </summary>
    public void UseKeyScript(KeyScript script, Action<KeyScript.Event> deliver)
    {
        _keyScript = script;
        _deliverScriptedKey = ev =>
        {
            _deliveringScriptedKey = true;
            try { deliver(ev); }
            finally { _deliveringScriptedKey = false; }
        };
    }

    /// <summary>
    /// True while real keys must be ignored: --dump runs ignore the
    /// real keyboard, as they do the gamepad, so a captured frame
    /// doesn't depend on keys pressed while its window happens to have
    /// focus (the regression suite opens several at once). Scripted
    /// --keys input still gets through. Front ends check it before
    /// their own hotkeys too.
    /// </summary>
    public bool IgnoresHostKeys => DumpTrace != null && !_deliveringScriptedKey;

    // Key events go straight to the machine's keyboard (IKeyboardInput),
    // which owns the mapping and the PC-shift handling. The key keeps
    // its modifier flags so the override layer can match bindings such
    // as Control+G.

    /// <summary>A host key went down. True if the machine used it.</summary>
    public bool KeyDown(HostKey key) =>
        !IgnoresHostKeys && Active.KeyboardInput.OnKeyDown(key);

    /// <summary>The character a host key press typed.</summary>
    public void KeyPress(char c)
    {
        if (!IgnoresHostKeys) Active.KeyboardInput.OnKeyPress(c);
    }

    /// <summary>A host key came up. True if the machine used it.</summary>
    public bool KeyUp(HostKey key) =>
        !IgnoresHostKeys && Active.KeyboardInput.OnKeyUp(key);

    private AutoLoadOrchestrator AutoLoad { get; set; } = null!;   // created in Start()
    private DumpTraceRecorder? DumpTrace { get; set; }

    // Exactly one of these is populated, from Settings.CurrentMachine.
    // Machine-specific hardware (Sound, Ppi, Pit, Cassette, Keyboard,
    // Video, ...) is reached through the populated one; the IMachine
    // surface (Cpu, Mem, RunFrame, Reset, LoadRoms, AutoLoad*, debugger
    // controls) through Active, which works for all three.
    public MZ700? Mz700 { get; }
    public MZ80A? Mz80a { get; }
    public MZ800? Mz800 { get; }
    public IMachine Active { get; }

    /// <summary>The joystick a host gamepad drives (a detached one on the MZ-80A).</summary>
    public Joystick Joystick { get; }

    /// <summary>"MZ-700", "MZ-80A" or "MZ-800".</summary>
    public string MachineLabel =>
        Mz800 != null ? "MZ-800" :
        Mz80a != null ? "MZ-80A" :
                        "MZ-700";

    /// <summary>
    /// Push the settings that change live into the running machine,
    /// after the user applies them: the MZ-80A screen colour and
    /// inverted letter shift, the MZ-800 VRAM expansion board, and the
    /// gamepad button numbers. The other machines' settings are only
    /// saved, and take effect when that machine next starts; ROM paths
    /// wait for the next Reset.
    /// </summary>
    public void ApplySettings()
    {
        if (Mz80a != null)
        {
            ApplyMz80aScreenColor();
            Mz80a.Keyboard.InvertLetterShift = _settings.Mz80aInvertLetterShift;
        }
        // The MZ-1R25 board toggles straight away, so the same title
        // can be compared with and without it.
        if (Mz800 != null) Mz800.Mem.VramExpansion = _settings.Mz800VramExpansion;
        JoystickSource?.SetButtonIndices(_settings.JoyButton1Index, _settings.JoyButton2Index);
    }

    /// <summary>
    /// Push the MZ-80A green-screen setting into its video. No-op on the
    /// other machines.
    /// </summary>
    private void ApplyMz80aScreenColor()
    {
        if (Mz80a == null) return;
        if (_settings.Mz80aGreenScreen)
        {
            Mz80a.Video.ForegroundArgb = unchecked((int)0xFF00FF00);
            Mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
        else
        {
            Mz80a.Video.ForegroundArgb = unchecked((int)0xFFFFFFFF);
            Mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
    }

    /// <summary>
    /// Load the ROMs, reset the machine and start sound on the output
    /// <paramref name="openAudio"/> returns. With a
    /// <paramref name="dumpPath"/>, also turn on the trace hooks and
    /// record the dump at <paramref name="dumpFrame"/>. Throws
    /// <see cref="FileNotFoundException"/>, with a message for the user,
    /// if the monitor ROM is missing.
    /// </summary>
    public void Start(Func<IAudioOut> openAudio, string? dumpPath, int dumpFrame)
    {
        if (string.IsNullOrEmpty(_settings.MonitorRomFullPath) || !File.Exists(_settings.MonitorRomFullPath))
        {
            var expected = ExpectedFirmware(_settings.CurrentMachine).Monitor;
            var configured = string.IsNullOrEmpty(_settings.MonitorRomPath)
                ? "(none configured)"
                : $"{_settings.MonitorRomPath}  →  {_settings.MonitorRomFullPath}";
            throw new FileNotFoundException(
                $"Monitor ROM ({expected}) not found.\n\n" +
                $"Configured path: {configured}\n\n" +
                $"Place {expected} under a 'roms' folder next to the executable, " +
                $"or set [Roms.{_settings.CurrentMachine}] Monitor= in {Path.Combine(AppContext.BaseDirectory, "settings.ini")}.");
        }
        Active.LoadRoms(_settings.MonitorRomFullPath, _settings.FontFullPath);
        Active.Reset();
        // PC-trace ring + PIT / bank-switch write logs only exist
        // to feed the --dump=<file> report. Off unless --dump was
        // asked for — otherwise we'd allocate and grow those
        // StringBuilders forever on the hot CPU / PIT-write path
        // (see [[project-feature-backlog]]).
        bool tracing = dumpPath != null;
        Active.Cpu.PcTraceEnabled = tracing;
        // Pit / Mem diagnostic hooks are MZ-700-specific for now —
        // MZ-80A equivalents arrive with the sound diagnostic
        // MZ-80A peer ([[project-mz80a-clk1-rate]]).
        if (Mz700 != null)
        {
            Mz700.Pit.WriteLog = tracing ? new System.Text.StringBuilder() : null;
            Mz700.Mem.BankSwitchLog = tracing ? new System.Text.StringBuilder() : null;
        }
        // MZ-700 / MZ-80A sound is synthesised in real time, so a
        // dump records its inputs instead (<dump>.snd.txt). The
        // MZ-800 renders in emulated time and captures a .wav below.
        if (tracing && (Mz700 != null || Mz80a != null))
        {
            var cpu = Active.Cpu;
            Active.Sound.StartEventLog(() => cpu.TotalCycles);
        }
        // MZ-800 gets the same hooks (both classes carry
        // WriteLog + BankSwitchLog StringBuilder fields). Phase 2.5
        // boot-hang investigation reads these via --dump=.
        if (Mz800 != null)
        {
            Mz800.Pit.WriteLog = tracing ? new System.Text.StringBuilder() : null;
            Mz800.Mem.BankSwitchLog = tracing ? new System.Text.StringBuilder() : null;
            // Log every $8000-$BFFF write and
            // every CRTC / palette register OUT. Fed into the .trace
            // file by DumpTraceRecorder.
            Mz800.Mem.VideoWriteLog = tracing ? new System.Text.StringBuilder() : null;
            Mz800.Io.CrtcWriteLog = tracing ? new System.Text.StringBuilder() : null;
            // Log every DMD write that switches between MZ-700 and
            // MZ-800 mode, for offline analysis of how software
            // changes mode.
            Mz800.Mem.ModeFlipLog = tracing ? new System.Text.StringBuilder() : null;
            // Log PIO / PSG / PPI writes so the interrupt
            // setup (PIO vector + control words, PPI PC0/PC2 masks)
            // software programs is visible offline.
            Mz800.Io.IntIoWriteLog = tracing ? new System.Text.StringBuilder() : null;
            // Phase 6.1: capture rendered audio for a .wav alongside the dump.
            Mz800.AudioCapture = tracing ? new System.IO.MemoryStream() : null;
        }
        Active.Sound.Start(openAudio());

        // Dump-and-trace recorder (v1.2 audit F-055). Only for --dump;
        // the trace hooks above are gated the same way.
        if (dumpPath != null)
        {
            DumpTrace = new DumpTraceRecorder(dumpPath, dumpFrame, Active, MachineLabel);
            DumpTrace.OnError += msg => StatusMessage?.Invoke(msg);
            DumpTrace.OnDumpComplete += () => DumpComplete?.Invoke();
        }

        // Autoload orchestrator (v1.2 audit F-055). Owns the two
        // per-machine BASIC + cassette + BASIC-source pipelines.
        AutoLoad = new AutoLoadOrchestrator(
            machine: Mz700,
            mz80a: Mz80a,
            mz800: Mz800,
            settings: _settings,
            monitorReady: MonitorReady,
            mz80aMonitorReady: Mz80aMonitorReady,
            mz80aBasicReady: Mz80aBasicReady,
            mz800MonitorReady: Mz800MonitorReady,
            setStatus: msg => StatusMessage?.Invoke(msg),
            showFatal: msg => FatalError?.Invoke(msg),
            openFontSheet: () => GraphModeEntered?.Invoke(),
            typeBasicSource: TypeBasicSource,
            updateModeLabel: graph => GraphModeSampled?.Invoke(graph));
    }

    /// <summary>
    /// Reset the machine and forget everything that tracked its boot:
    /// the ready checks start watching afresh and the autoload state
    /// (BASIC loaded, MZ-80A cassette autorun, GRAPH-mode tracker) is
    /// cleared.
    /// </summary>
    public void Reset()
    {
        Active.Reset();
        FrameCount = 0;
        _monitorReady = false;
        AutoLoad.ResetForFreshMachine();
        StatusMessage?.Invoke("Reset.");
    }

    // -- Loading ---------------------------------------------------------
    // Each load reports progress through StatusMessage and throws, with a
    // message for the user, when a file can't be read or loaded.

    /// <summary>What a load did.</summary>
    public enum LoadResult
    {
        /// <summary>Done, or queued to finish once the machine is ready.</summary>
        Done,
        /// <summary>
        /// Nothing done, because it needs BASIC and the BASIC image is
        /// missing; <see cref="BasicMissingMessage"/> says what to do.
        /// </summary>
        BasicMissing,
    }

    /// <summary>
    /// Whether the configured BASIC image exists. Checked by every entry
    /// point that triggers a BASIC load (menu, CLI, BASIC-cassette
    /// auto-load, Load BASIC source) before it changes anything, so a
    /// missing BASIC aborts the operation cleanly; otherwise the deferred
    /// load would surface the failure only as a status line many frames
    /// later and the rest of the pending operation would go ahead.
    /// </summary>
    public bool BasicAvailable
    {
        get
        {
            var path = _settings.BasicFullPath;
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }
    }

    /// <summary>What to tell the user when BASIC is missing.</summary>
    public string BasicMissingMessage
    {
        get
        {
            var configured = string.IsNullOrEmpty(_settings.BasicPath)
                ? "(none configured)"
                : $"{_settings.BasicPath}  →  {_settings.BasicFullPath}";
            var expected = ExpectedFirmware(_settings.CurrentMachine).Basic;
            return $"BASIC cassette image ({expected}) not found.\n\n" +
                   $"Configured path: {configured}\n\n" +
                   $"Place {expected} under a 'basic' or 'roms' folder next to the executable, " +
                   $"or set [Roms.{_settings.CurrentMachine}] Basic= in {Path.Combine(AppContext.BaseDirectory, "settings.ini")}.";
        }
    }

    /// <summary>
    /// Whether a cassette of this type is a BASIC program, so loading it
    /// boots BASIC first. MZ-700: S-BASIC text (02) and 05. MZ-800: 1Z-016
    /// programs are type 05; an MZ-700 BASIC (02) program would need
    /// S-BASIC in MZ-700 mode, which isn't automated.
    /// </summary>
    private bool NeedsBasic(byte type)
        => Mz800 != null ? type == 0x05 : type == 0x02 || type == 0x05;

    /// <summary>
    /// Queue what the command line asked for, to run once the machine has
    /// booted: BASIC if <paramref name="loadBasic"/> (--basic) or the
    /// cassette is a BASIC program, then the cassette. Call after
    /// <see cref="Start"/>. If BASIC is wanted but missing, BASIC isn't
    /// queued, nor is a cassette that needs it.
    /// </summary>
    public LoadResult QueueStartupLoad(string? cassettePath, bool loadBasic)
    {
        // The type peek means the user doesn't have to know whether a
        // given .mzf is MC or BASIC — they just point the emulator at
        // it and the right boot path runs.
        bool cassetteNeedsBasic = false;
        if (cassettePath != null)
        {
            try
            {
                var img = MzfImage.Parse(CassetteFile.ReadBytes(cassettePath));
                if (NeedsBasic(img.Type)) { loadBasic = true; cassetteNeedsBasic = true; }
            }
            catch { /* let the autoload path surface the error with a clearer status */ }
            AutoLoad.PendingCassette = cassettePath;
        }
        if (!loadBasic) return LoadResult.Done;
        // Pre-flight the BASIC file so the failure shows up at startup
        // (parity with the menu's Load BASIC) rather than as a quiet
        // status line many frames later. If BASIC is missing we cancel
        // the auto-load — and the pending cassette too, when it can't run
        // without BASIC.
        if (!BasicAvailable)
        {
            if (cassetteNeedsBasic) AutoLoad.PendingCassette = null;
            return LoadResult.BasicMissing;
        }
        // Run a few frames so the monitor boots before injecting BASIC
        AutoLoad.PendingLoadBasic = true;
        return LoadResult.Done;
    }

    /// <summary>
    /// Load a cassette image and run it, the way the File menu's Load
    /// cassette and drag-and-drop do: straight in where the machine
    /// allows, else after a reset (booting BASIC first for a BASIC
    /// program).
    /// </summary>
    public LoadResult LoadCassette(string path)
    {
        var img = MzfImage.Parse(CassetteFile.ReadBytes(path));

        // Loading any cassette is treated as a fresh-state operation,
        // mirroring the CLI launch path. If the previous run was BASIC
        // (or a BASIC program is mid-execution), we reset back to the
        // monitor before injecting — leaving stale BASIC state lying
        // around would mean the new program runs against the old
        // interpreter's RAM, which doesn't work for mid-execution and
        // also breaks "load MC cassette while at BASIC READY". The
        // type-byte tells us whether to also auto-load BASIC; the
        // autoload orchestrator's pending-cassette path handles the
        // rest (monitor banner detection, post-BASIC 60-frame wait,
        // direct-inject + auto-RUN for BASIC, jump-to-exec for MC).
        // The MZ-800 always resets: a running title leaves display
        // mode, palette, VRAM planes, scroll, bank, PIO and PSG state
        // behind, and a new title that assumes a fresh boot draws over
        // it. The pending path waits for the IPL before loading.
        bool needsBasic = NeedsBasic(img.Type);
        bool basicLoaded = AutoLoad.BasicLoadedFrame >= 0;

        if (basicLoaded || needsBasic || Mz800 != null)
        {
            // Pre-flight the BASIC file. If the cassette needs BASIC and
            // it's missing, abort the whole load: a BASIC program can't
            // run without the interpreter, and continuing would only
            // result in junk getting typed into the monitor.
            if (needsBasic && !BasicAvailable) return LoadResult.BasicMissing;
            Reset();
            if (needsBasic) AutoLoad.PendingLoadBasic = true;
            AutoLoad.PendingCassette = path;
            StatusMessage?.Invoke(needsBasic
                ? $"Loading BASIC + {img.Filename}…"
                : $"Loading {img.Filename}…");
        }
        else if (img.Type == 0x01)
        {
            // Pure monitor + machine-code cassette: direct-inject and
            // jump straight to its exec entry. No reset needed.
            if (Mz80a != null)
                Mz80a.Cassette.DirectInject(img, jumpExec: true);
            else
                Mz700!.Cassette.DirectInject(img, jumpExec: true);
            StatusMessage?.Invoke($"Loaded & run: {img.Filename} exec=${img.ExecAddr:X4}");
        }
        else
        {
            // Other type at the monitor: queue for monitor LOAD command
            // (MZ-700) or direct-inject as best-effort (MZ-80A doesn't
            // have the L-command auto-typer path yet). Non-MC
            // .mzf types (03 BASIC data, 04 ASCII, A0/A1 PASCAL) don't
            // have a reliable exec address — inject-only.
            if (Mz80a != null)
            {
                Mz80a.Cassette.DirectInject(img, jumpExec: false);
                StatusMessage?.Invoke($"Loaded (no exec): {img.Filename}");
            }
            else
            {
                Mz700!.Cassette.Queue(img);
                StatusMessage?.Invoke($"Queued: {img.Filename}. Type LOAD to fetch.");
            }
        }
        return LoadResult.Done;
    }

    /// <summary>
    /// Whether <see cref="InsertCassette"/> works on this machine: the
    /// MZ-700 and MZ-800. The MZ-80A's SA-5510 loads through BASIC's
    /// LOAD, not the SA-1510 monitor's L command.
    /// </summary>
    public bool CanInsertCassette => Mz700 != null || Mz800 != null;

    /// <summary>
    /// Insert a cassette for the emulated machine's own LOAD to read
    /// through the tape traps — no direct-inject, no jump; the user
    /// drives the load. MZ-700: one image for the monitor's L command.
    /// MZ-800: the whole tape (every file of a zip / .mzt), read by the
    /// IPL's C option, the monitor's L, or 1Z-016 BASIC's LOAD. Only
    /// where <see cref="CanInsertCassette"/>.
    /// </summary>
    public void InsertCassette(string path)
    {
        if (Mz700 != null)
        {
            var img = MzfImage.Parse(CassetteFile.ReadBytes(path));
            Mz700.Cassette.Queue(img);
            StatusMessage?.Invoke($"Queued: {img.Filename}. Type L in Monitor mode to fetch.");
        }
        else
        {
            var tape = CassetteFile.ReadTape(path);
            Mz800!.Cassette.Mount(tape);
            StatusMessage?.Invoke(tape.Count == 1
                ? $"Tape inserted: {tape[0].Filename}."
                : $"Tape inserted: {tape.Count} files, first {tape[0].Filename}.");
        }
    }

    /// <summary>Load BASIC into the running machine now.</summary>
    public LoadResult LoadBasic()
    {
        if (!BasicAvailable) return LoadResult.BasicMissing;
        Active.AutoLoadBasic(_settings.BasicFullPath);
        AutoLoad.BasicLoadedFrame = FrameCount;
        StatusMessage?.Invoke("BASIC loaded.");
        return LoadResult.Done;
    }

    /// <summary>
    /// Type a BASIC text source into the interpreter (see
    /// <see cref="TypeBasicSource"/>). If BASIC isn't loaded yet, the
    /// machine is reset and BASIC + this source are queued for after
    /// monitor boot.
    /// </summary>
    public LoadResult LoadBasicSource(string path)
    {
        if (AutoLoad.BasicLoadedFrame < 0)
        {
            if (!BasicAvailable) return LoadResult.BasicMissing;
            Reset();
            AutoLoad.PendingLoadBasic = true;
            AutoLoad.PendingBasicSource = path;
            StatusMessage?.Invoke($"Loading BASIC + {Path.GetFileName(path)}…");
            return LoadResult.Done;
        }
        TypeBasicSource(path);
        StatusMessage?.Invoke($"Typing {Path.GetFileName(path)}…");
        return LoadResult.Done;
    }

    /// <summary>
    /// Run one emulated frame plus the per-frame work around it:
    /// gamepad sampling, the autoload pipelines, scripted keys and the
    /// dump. Stops part-way if a scripted key or the dump calls
    /// <see cref="Stop"/>. The caller paces frames.
    /// </summary>
    public void RunFrame()
    {
        if (Stopped) return;
        // Sample real gamepad state once per frame, before the emulated
        // CPU runs — the MZ-700 latches them at the VBLK falling edge
        // inside RunFrame; the MZ-800 reads them live through $F0/$F1.
        // --dump runs skip it: a captured frame must not depend on
        // whatever controller happens to be plugged in.
        if ((Mz700 != null || Mz800 != null) && DumpTrace == null) JoystickSource?.Poll();
        Active.RunFrame();
        FrameCount++;

        // BASIC + cassette + BASIC-source pipelines both machines run
        // (v1.2 audit F-055 extraction). Also handles the per-frame
        // GRAPH-mode indicator + Font Sheet auto-surface for MZ-700
        // and the F11-driven mode indicator for MZ-80A.
        AutoLoad.OnFrame(FrameCount);
        FrameRan?.Invoke(FrameCount);

        // Scripted host input (--keys=): events due after this frame.
        if (_keyScript != null) _keyScript.OnFrame(_deliverScriptedKey!);
        if (Stopped) return;   // a scripted Alt+F4 closed the front end

        // Dump-and-trace flow (v1.2 audit F-055 extraction). No-op
        // when --dump wasn't passed; otherwise emits the periodic
        // trace line and raises DumpComplete at the configured frame.
        DumpTrace?.OnFrame(FrameCount);
    }

    /// <summary>
    /// Stop running frames, including the rest of the current one. The
    /// front end calls this as it closes.
    /// </summary>
    public void Stop() => Stopped = true;

    /// <summary>The Sharp firmware file names each machine expects —
    /// the names <see cref="Settings.EnsureRomPaths"/> auto-detects.</summary>
    public static (string Monitor, string Basic) ExpectedFirmware(MachineType machine) => machine switch
    {
        MachineType.MZ80A => ("SA-1510.rom", "SA-5510.mzf"),
        MachineType.MZ800 => ("MZ800.ROM",   "1Z-016.mzf"),
        _                 => ("1z-013a.rom", "1Z-013B.mzf"),
    };

    /// <summary>
    /// Type a BASIC text source into the running interpreter: each
    /// non-blank line goes through the machine's keyboard auto-typer,
    /// followed by CR. Lines starting with <c>;</c> or <c>'</c> are
    /// comments, stripped here so they don't waste cycles inside BASIC.
    /// </summary>
    public void TypeBasicSource(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('\'')) continue;
            TypeIntoActiveMachine(line + "\r");
        }
    }

    // Each machine's keyboard auto-typer: the MZ-700 / MZ-800 share the
    // scan-driven KeyboardAutoTyper; the MZ-80A's is time-based.
    private void TypeIntoActiveMachine(string text)
    {
        if (Mz700 != null) Mz700.Keyboard.AutoType.TypeString(text);
        else if (Mz800 != null) Mz800.Keyboard.AutoType.TypeString(text);
        else Mz80a?.Keyboard.TypeString(text);
    }

    // -- Ready checks ----------------------------------------------------
    // The autoload pipelines wait on these before injecting BASIC or a
    // cassette. Each watches VRAM for what the ROM draws once its boot
    // init is done. Once the monitor is seen ready it stays ready until
    // the next Reset.

    private bool _monitorReady;

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
        var v = Mz700!.Mem.Vram;
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
        if (Mz80a == null) return false;
        var v = Mz80a.Mem.Vram;
        if (v[40] != 0)
        {
            _monitorReady = true;
        }
        return _monitorReady;
    }

    /// <summary>
    /// MZ-800 monitor/boot-menu ready detector. Phase 4 (v1.3.0) uses
    /// this only for MC cassette autoload — we wait until the 1Z-016B
    /// IPL has finished painting its boot menu ("Make ready CMT /
    /// Please push key / C/Cassette tape / M/Monitor") to VRAM before
    /// injecting, so the memory banks are in a DRAM-friendly state and
    /// the CPU is in the boot menu's stable keyboard-wait loop.
    ///
    /// Detection: anchor on the MZ display-code sequence for "MONITOR"
    /// — M=$0D O=$0F N=$0E I=$09 T=$14 O=$0F R=$12 — which is
    /// unambiguously the boot-menu bottom line. Also accept the
    /// lowercase bank-1 variant (M/onitor prints "onitor" as bank-1
    /// codes $8F $8E $89 $94 $8F $92) since the 1Z-016B IPL uses
    /// mixed case. Earlier "M/" digraph check turned out to be too
    /// fragile — the menu row's actual layout has spaces or bank
    /// differences we hadn't confirmed against VRAM.
    /// </summary>
    private bool Mz800MonitorReady()
    {
        if (_monitorReady) return true;
        if (Mz800 == null) return false;
        var v = Mz800.Mem.Vram;
        // "ONITOR" (uppercase O-N-I-T-O-R) as bank-0 codes.
        byte[] onitorU = { 0x0F, 0x0E, 0x09, 0x14, 0x0F, 0x12 };
        // Same as bank-1 (lowercase). MZ bank-1 = bank-0 + $80.
        byte[] onitorL = { 0x8F, 0x8E, 0x89, 0x94, 0x8F, 0x92 };
        for (int i = 0; i + onitorU.Length < v.Length; i++)
        {
            if (Match(v, i, onitorU) || Match(v, i, onitorL))
            {
                _monitorReady = true;
                break;
            }
        }
        return _monitorReady;

        static bool Match(byte[] v, int i, byte[] pat)
        {
            for (int k = 0; k < pat.Length; k++) if (v[i + k] != pat[k]) return false;
            return true;
        }
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
        if (Mz80a == null) return false;
        var v = Mz80a.Mem.Vram;
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
}
