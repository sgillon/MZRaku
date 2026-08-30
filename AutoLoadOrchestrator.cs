using System;
using System.IO;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Owns the two per-machine startup pipelines MainForm's Timer_Tick
/// used to run inline: wait for the ROM to be ready → auto-load
/// BASIC (if requested) → wait for BASIC's Ready prompt → inject
/// the pending cassette or type a BASIC source, with auto-RUN
/// where the flow permits it.
///
/// Split out of MainForm in v1.2 audit F-055 — the two per-machine
/// blocks were subtly different state machines sitting side-by-side
/// in one 220-line frame handler, and every bug that hit one had
/// to be checked against the other.
///
/// State the orchestrator owns:
/// - Whether BASIC is pending (from --basic CLI or auto-detect on
///   a BASIC .mzf).
/// - The pending cassette path + pending BASIC source path.
/// - _basicLoadedFrame — set the frame BASIC finished loading; the
///   cassette / source pipelines both use this + a delay to wait
///   for BASIC's Ready prompt.
/// - MZ-80A cassette-autorun bookkeeping (_mz80aLoadTyped +
///   _mz80aLoadDoneFrame + _mz80aRunTyped).
/// - _wasGraphMode for the MZ-700 GRAPH-mode auto-Font-Sheet.
///
/// Depends on the host (MainForm) for:
/// - Two ROM-ready detectors (MZ-700 and MZ-80A).
/// - MZ-80A BASIC-ready detector.
/// - Status-label writer + fatal-error dialog.
/// - Font Sheet opener (called on MZ-700 ALPHA→GRAPH transitions).
/// - BASIC source typer (uses MainForm's cursor-anchor logic).
/// </summary>
internal sealed class AutoLoadOrchestrator
{
    private readonly MZ700? _machine;
    private readonly MZ80A? _mz80a;
    private readonly MZ800? _mz800;
    private readonly Settings _settings;
    private readonly Func<bool> _monitorReady;
    private readonly Func<bool> _mz80aMonitorReady;
    private readonly Func<bool> _mz80aBasicReady;
    private readonly Func<bool> _mz800MonitorReady;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _showFatal;   // MessageBox for BASIC-load failure
    private readonly Action _openFontSheet;       // MZ-700 GRAPH auto-surface
    private readonly Action<string> _typeBasicSource;
    private readonly Action<bool?> _updateModeLabel;   // null / false / true

    private bool _pendingLoadBasic;
    private string? _pendingCassette;
    private string? _pendingBasicSource;
    private int _basicLoadedFrame = -1;
    private bool _mz80aLoadTyped;
    private int _mz80aLoadDoneFrame = -1;
    private bool _mz80aRunTyped;
    private bool _wasGraphMode;

    public AutoLoadOrchestrator(
        MZ700? machine,
        MZ80A? mz80a,
        MZ800? mz800,
        Settings settings,
        Func<bool> monitorReady,
        Func<bool> mz80aMonitorReady,
        Func<bool> mz80aBasicReady,
        Func<bool> mz800MonitorReady,
        Action<string> setStatus,
        Action<string> showFatal,
        Action openFontSheet,
        Action<string> typeBasicSource,
        Action<bool?> updateModeLabel)
    {
        _machine = machine;
        _mz80a = mz80a;
        _mz800 = mz800;
        _settings = settings;
        _monitorReady = monitorReady;
        _mz80aMonitorReady = mz80aMonitorReady;
        _mz80aBasicReady = mz80aBasicReady;
        _mz800MonitorReady = mz800MonitorReady;
        _setStatus = setStatus;
        _showFatal = showFatal;
        _openFontSheet = openFontSheet;
        _typeBasicSource = typeBasicSource;
        _updateModeLabel = updateModeLabel;
    }

    public bool PendingLoadBasic
    {
        get => _pendingLoadBasic;
        set => _pendingLoadBasic = value;
    }
    public string? PendingCassette
    {
        get => _pendingCassette;
        set => _pendingCassette = value;
    }
    public string? PendingBasicSource
    {
        get => _pendingBasicSource;
        set => _pendingBasicSource = value;
    }
    public int BasicLoadedFrame
    {
        get => _basicLoadedFrame;
        set => _basicLoadedFrame = value;
    }

    /// <summary>Reset all autoload state — called from MainForm.ResetMachine.</summary>
    public void ResetForFreshMachine()
    {
        _basicLoadedFrame = -1;
        _mz80aLoadTyped = false;
        _mz80aLoadDoneFrame = -1;
        _mz80aRunTyped = false;
        _wasGraphMode = false;
    }

    /// <summary>Called once per host frame from MainForm.Timer_Tick.</summary>
    public void OnFrame(int bootFrames)
    {
        if (_machine != null) OnMz700Frame(bootFrames);
        else if (_mz80a != null) OnMz80aFrame(bootFrames);
        else if (_mz800 != null) OnMz800Frame(bootFrames);
    }

    // ---- MZ-700 pipeline ------------------------------------------------

    private void OnMz700Frame(int bootFrames)
    {
        // MZ-700 mode-label update. S-BASIC's keyboard mode flag
        // lives at $0060 bit 4 (set = GRAPH, clear = ALPHA),
        // discovered empirically via the memory-viewer snapshot/diff
        // tool 2026-05-31. Only meaningful while S-BASIC owns the
        // machine (ROM banked out so $0060 is RAM); before BASIC is
        // loaded, $0060 reads from ROM and the indicator would be
        // misleading, so we grey it out. Also surfaces the Font
        // Sheet on ALPHA→GRAPH — GRAPH mode is unusable without the
        // palette since graphic glyphs aren't reachable from any PC
        // key.
        if (bootFrames % 10 == 0)
        {
            if (_basicLoadedFrame < 0)
            {
                _updateModeLabel(null);
            }
            else
            {
                bool graph = (_machine!.Mem.Read(0x0060) & 0x10) != 0;
                _updateModeLabel(graph);
                if (graph && !_wasGraphMode)
                    _openFontSheet();
                _wasGraphMode = graph;
            }
        }

        // Inject pending BASIC as soon as the monitor's input prompt
        // is visible — the banner-detection signals that init is
        // complete and the keyboard loop is running, which is what
        // BASIC's startup at $7D79 needs (it does CALL $0033 into
        // monitor ROM expecting a clean stack).
        if (_pendingLoadBasic && _monitorReady())
        {
            try
            {
                _machine!.AutoLoadBasic(_settings.BasicFullPath);
                _setStatus("BASIC loaded.");
                _pendingLoadBasic = false;
                _basicLoadedFrame = bootFrames;
            }
            catch (Exception ex)
            {
                // Defence-in-depth: entry-point checks should have
                // caught a missing BASIC, but if the load fails here
                // (file vanished, unreadable, parse error), behave
                // like the menu's Load BASIC — modal error, abandon
                // any dependent pending work.
                _pendingLoadBasic = false;
                _pendingCassette = null;
                _pendingBasicSource = null;
                _setStatus("BASIC load failed.");
                _showFatal("BASIC load failed:\n" + ex.Message);
            }
        }

        // Cassette injection: wait 60 frames after BASIC was loaded
        // so its banner displays and READY prompt is reached before
        // we auto-type commands. (For pure-monitor MC cassettes,
        // fire as soon as the monitor is ready.) `basicMode` is
        // the runtime answer to "is this cassette going through
        // BASIC?" — true whether BASIC came from --basic, the
        // menu, or auto-load triggered by opening a BASIC .mzf.
        bool basicMode = _pendingLoadBasic || _basicLoadedFrame >= 0;
        bool readyForCassette = basicMode
            ? (_basicLoadedFrame >= 0 && bootFrames - _basicLoadedFrame >= 60)
            : _monitorReady();
        if (readyForCassette && _pendingCassette != null)
        {
            try
            {
                if (basicMode)
                {
                    // BASIC is loaded; direct-inject the program
                    // into RAM at its load address (without jumping)
                    // and fix up program pointers, mirroring what
                    // the menu's LoadCassetteFile does. Can't use
                    // BASIC's LOAD command because S-BASIC bypasses
                    // the monitor's tape routines (the ones trapped
                    // at $0436/$04D8) — its own tape code reads
                    // PortC bit 5 directly and has no real cassette.
                    var img = MzfImage.Parse(CassetteFile.ReadBytes(_pendingCassette));
                    _machine!.Cassette.DirectInject(img, jumpExec: false);
                    if (img.Type == 0x02 || img.Type == 0x05)
                    {
                        _machine.Cassette.FixupBasicProgramPointers(img.LoadAddr, img.Data.Length);
                        // Auto-RUN: with the program injected and
                        // pointers fixed, BASIC's RUN preprocesses
                        // lengths and starts execution. End-to-end
                        // automation from CLI.
                        _machine.Keyboard.AutoType.TypeString("RUN\r");
                        _setStatus($"Loaded {img.Filename}. Running.");
                    }
                    else
                    {
                        string usage = img.Type == 0x01 ? $"USR(${img.ExecAddr:X4})" : "RUN";
                        _setStatus($"Loaded {img.Filename} into BASIC. Type {usage}.");
                    }
                }
                else
                {
                    // Machine-code cassette at startup: direct-inject
                    // into RAM and jump to the game's execution
                    // address. Bypasses the monitor's tape-LOAD flow
                    // (which would need a working keyboard-driven
                    // command prompt).
                    _machine!.DirectInjectCassette(_pendingCassette);
                    _setStatus($"Loaded: {Path.GetFileName(_pendingCassette)}");
                }
            }
            catch (Exception ex)
            {
                _setStatus("Cassette load failed: " + ex.Message);
            }
            _pendingCassette = null;
        }

        // BASIC source: identical readiness gate as a BASIC cassette
        // — wait for BASIC's READY prompt then auto-type the file in.
        if (_pendingBasicSource != null && _basicLoadedFrame >= 0 && bootFrames - _basicLoadedFrame >= 60)
        {
            try
            {
                _typeBasicSource(_pendingBasicSource);
                _setStatus($"Typing {Path.GetFileName(_pendingBasicSource)}…");
            }
            catch (Exception ex)
            {
                _setStatus("BASIC source load failed: " + ex.Message);
            }
            _pendingBasicSource = null;
        }
    }

    // ---- MZ-80A pipeline ------------------------------------------------

    private void OnMz80aFrame(int bootFrames)
    {
        // BASIC gets loaded first (as soon as the SA-1510 prompt is
        // up), THEN the cassette waits 60 frames past
        // _basicLoadedFrame so SA-5510 has time to reach its Ready
        // prompt before we overwrite the program area. Same
        // two-phase shape as MZ-700 uses.
        if (_pendingLoadBasic && _mz80aMonitorReady())
        {
            try
            {
                _mz80a!.AutoLoadBasic(_settings.BasicFullPath);
                _setStatus("BASIC loaded.");
                _basicLoadedFrame = bootFrames;
            }
            catch (Exception ex)
            {
                _setStatus("BASIC load failed: " + ex.Message);
            }
            _pendingLoadBasic = false;
        }
        if (_pendingCassette != null)
        {
            bool viaBasic = _basicLoadedFrame >= 0;
            // Cassette-via-BASIC needs SA-5510's "Ready" prompt to
            // be on screen — the keyboard input path only starts
            // polling after BASIC finishes its ~3.3s cold init.
            // Using a fixed frame count (like the MZ-700 path's
            // 60-frame wait) fires too early and BASIC eats the
            // LOAD keystrokes into a buffer it isn't reading yet.
            bool ready = viaBasic ? _mz80aBasicReady() : _mz80aMonitorReady();
            if (ready)
            {
                try
                {
                    var img = MzfImage.Parse(CassetteFile.ReadBytes(_pendingCassette));
                    // BASIC-type images: Queue the image so SA-5510's
                    // LOAD hits our SA-1510 RDINF/RDDAT traps
                    // ($0027/$002A) and the ROM's own load path
                    // maintains BASIC's internal program pointers.
                    // DirectInject bypasses this and produces Error
                    // 19 on RUN. Machine-code images still
                    // DirectInject + jumpExec — SA-1510's monitor L
                    // command sequence isn't wired for MZ-80A and
                    // the shortcut works cleanly for type 01.
                    bool isBasicType = img.Type == 0x02
                        || img.Type == 0x03
                        || img.Type == 0x05;
                    if (isBasicType)
                    {
                        // Queue the image so SA-5510's LOAD hits the
                        // SA-1510 traps, then auto-type LOAD and
                        // (once the trap has fired + BASIC has
                        // settled) RUN. Same shape as MZ-700's
                        // autorun path. Attempted a DirectInject +
                        // pointer-fixup shortcut 2026-07-12 to make
                        // LOAD invisible; the $4E4E variable table
                        // was pinned but other state SA-5510's RUN
                        // depends on (Error 16 on GOSUB) resisted
                        // synthesis. Kept as a future item.
                        _mz80a!.Cassette.Queue(img);
                        _mz80a.Keyboard.TypeString("LOAD\r");
                        _mz80aLoadTyped = true;
                        _setStatus($"Loading {img.Filename}…");
                    }
                    else
                    {
                        _mz80a!.Cassette.DirectInject(img, jumpExec: img.Type == 0x01);
                        _setStatus($"Loaded: {img.Filename}");
                    }
                }
                catch (Exception ex)
                {
                    _setStatus("Cassette load failed: " + ex.Message);
                }
                _pendingCassette = null;
            }
        }
        // Cassette-autorun sequencing: LOAD was typed above; once
        // the RDDAT trap has fired (DataDelivered latches true),
        // wait 60 frames for BASIC to re-tokenise the loaded
        // program then auto-type RUN. Trap injection is
        // instantaneous, so the window is BASIC's post-load
        // bookkeeping.
        if (_mz80aLoadTyped && !_mz80aRunTyped)
        {
            if (_mz80aLoadDoneFrame < 0 && _mz80a!.Cassette.DataDelivered)
                _mz80aLoadDoneFrame = bootFrames;
            if (_mz80aLoadDoneFrame >= 0 &&
                bootFrames - _mz80aLoadDoneFrame >= 60)
            {
                _mz80a!.Keyboard.TypeString("RUN\r");
                _mz80aRunTyped = true;
                _setStatus("Running.");
            }
        }

        // MZ-80A mode indicator. Tracked locally via F11 press events
        // in Mz80aKeyboard.GraphMode — see the note there about
        // program-driven mode changes not being caught.
        if (bootFrames % 10 == 0)
            _updateModeLabel(_mz80a!.Keyboard.GraphMode);
    }

    // ---- MZ-800 pipeline ------------------------------------------------

    /// <summary>
    /// Phase 4c (v1.3.0): BASIC via 1Z-016.mzf goes through the same
    /// shape as MZ-700's pipeline — wait for the boot menu / monitor
    /// ready, hand off to <see cref="MZ800.AutoLoadBasic"/> which
    /// writes to Ram[] and switches to bank config D_AllRam so the
    /// binary's byte-0 (JP to BASIC cold-boot) actually runs. BASIC
    /// source typing (<see cref="_pendingBasicSource"/>) is Phase 4d;
    /// surface a one-shot note so the user knows why nothing's typed.
    /// </summary>
    private void OnMz800Frame(int bootFrames)
    {
        if (_pendingLoadBasic && _mz800MonitorReady())
        {
            try
            {
                _mz800!.AutoLoadBasic(_settings.BasicFullPath);
                _setStatus("BASIC loaded.");
                _pendingLoadBasic = false;
            }
            catch (Exception ex)
            {
                _pendingLoadBasic = false;
                _pendingCassette = null;
                _pendingBasicSource = null;
                _setStatus("BASIC load failed.");
                _showFatal("BASIC load failed:\n" + ex.Message);
            }
        }

        if (_pendingBasicSource != null)
        {
            _setStatus("BASIC source typing not yet supported on MZ-800 (Phase 4d).");
            _pendingBasicSource = null;
        }

        if (_pendingCassette != null && _mz800MonitorReady())
        {
            try
            {
                // MC-only path: DirectInject to the image's load address
                // and jump to its exec entry. The IPL / boot menu is
                // already up (that's what MonitorReady detects), so the
                // banks are in DRAM-friendly state and injection lands
                // where a monitor LOAD would have put it. Bypasses the
                // C / M menu selection entirely.
                var img = MzfImage.Parse(CassetteFile.ReadBytes(_pendingCassette));
                if (img.Type == 0x01)
                {
                    _mz800!.DirectInjectCassette(_pendingCassette);
                    _setStatus($"Loaded: {img.Filename}");
                }
                else
                {
                    // Non-MC without BASIC has no target on MZ-800 in
                    // Phase 4 scope. Inject at load address without
                    // jumping so the debugger / memory viewer can
                    // still see the payload.
                    _mz800!.Cassette.DirectInject(img, jumpExec: false);
                    _setStatus($"Loaded (no exec, type {img.Type:X2}): {img.Filename}");
                }
            }
            catch (Exception ex)
            {
                _setStatus("Cassette load failed: " + ex.Message);
            }
            _pendingCassette = null;
        }
    }
}
