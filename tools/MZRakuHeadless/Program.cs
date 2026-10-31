namespace MZRakuHeadless;

/// <summary>
/// MZRakuHeadless: runs one emulator session as fast as it will go, with
/// no window and no sound device, and writes the --dump files at the
/// dump frame. It takes the app's command line, so the regression suite
/// runs the same cases through either (`regress.py --headless`) and
/// compares both against the same goldens.
///
/// What the app's window would add is handled here instead:
/// - scripted keys go through the app's hotkeys first, as they do in
///   MainForm.ProcessCmdKey: the ones that change emulation act, and the
///   ones that only open windows or dialogs are swallowed;
/// - errors the app would show in a dialog go to stderr, and the run
///   ends with an exit code (1 can't start, 2 bad --keys script,
///   3 fatal error while running).
/// Settings come from settings.ini next to the executable, as the app's
/// do.
/// </summary>
internal static class Program
{
    private const string Usage =
        "Usage: MZRakuHeadless [--mz700|--mz80a|--mz800] [--basic] [--keys=<file>]\n" +
        "                      --dump=<path> [--dumpframe=N] [cassette.mzf|.zip]\n\n" +
        "Runs unpaced and writes the same --dump files as MZRaku.exe at frame N\n" +
        "(default 120), then exits. Window options (--display, --scanlines) are\n" +
        "accepted and ignored. Settings come from settings.ini next to this\n" +
        "executable.";

    private static int Main(string[] args)
    {
        string? cassettePath = null;
        bool autoLoadBasic = false;
        string? dumpPath = null;
        int dumpFrame = 120;
        string? keysPath = null;
        MachineType? machineOverride = null;

        foreach (var a in args)
        {
            if (a.Equals("--basic", StringComparison.OrdinalIgnoreCase) || a.Equals("-b", StringComparison.OrdinalIgnoreCase))
                autoLoadBasic = true;
            else if (a.Equals("--mz700", StringComparison.OrdinalIgnoreCase))
                machineOverride = MachineType.MZ700;
            else if (a.Equals("--mz80a", StringComparison.OrdinalIgnoreCase))
                machineOverride = MachineType.MZ80A;
            else if (a.Equals("--mz800", StringComparison.OrdinalIgnoreCase))
                machineOverride = MachineType.MZ800;
            else if (a.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase))
                dumpPath = a.Substring(7);
            else if (a.StartsWith("--dumpframe=", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(a.Substring(12), out dumpFrame) || dumpFrame < 1)
                    return Fail(1, $"--dumpframe value '{a.Substring(12)}' isn't a frame number.");
            }
            else if (a.StartsWith("--keys=", StringComparison.OrdinalIgnoreCase))
                keysPath = a.Substring(7);
            else if (a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h" || a == "/?")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            // Anything else starting with '-' is ignored, as the app
            // ignores options it doesn't know: the window options and the
            // regression suite's --cassette marker.
            else if (!a.StartsWith("-"))
                cassettePath = a;
        }

        // Without a dump frame an unpaced run would never end.
        if (dumpPath == null)
            return Fail(1, "--dump=<path> is required.\n\n" + Usage);

        MatrixValidation.RunAndLog((text, title) => Console.Error.WriteLine($"{title}: {text}"));

        KeyScript? keyScript = null;
        if (keysPath != null)
        {
            try
            {
                keyScript = KeyScript.Load(keysPath);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return Fail(2, $"--keys script {keysPath}: {ex.Message}");
            }
        }

        var settings = Settings.Load();
        if (machineOverride.HasValue) settings.CurrentMachine = machineOverride.Value;
        var session = new EmulatorSession(settings);
        int exitCode = 0;

        session.StatusMessage += msg => Console.WriteLine($"[{session.FrameCount,5}] {msg}");
        session.FatalError += msg =>
        {
            Console.Error.WriteLine(msg);
            exitCode = 3;
            session.Stop();
        };
        session.DumpComplete += session.Stop;
        if (keyScript != null)
            session.UseKeyScript(keyScript, ev => DeliverScriptedKey(session, ev));

        try
        {
            session.Start(() => new NullAudioOut(), dumpPath, dumpFrame);
            if (session.QueueStartupLoad(cassettePath, autoLoadBasic) == EmulatorSession.LoadResult.BasicMissing)
                Console.Error.WriteLine(session.BasicMissingMessage);
        }
        catch (Exception ex)
        {
            return Fail(1, "Failed to start emulator:\n" + ex.Message);
        }

        // Unpaced: frames run back to back until the dump is written, a
        // scripted Alt+F4 stops the session, or a fatal error.
        while (!session.Stopped)
            session.RunFrame();

        return exitCode;
    }

    /// <summary>
    /// One --keys= event, the way MainForm.InjectScriptedKey delivers it:
    /// a key going down meets the app's hotkeys first, then reaches the
    /// machine as KeyDown plus KeyPress for its character.
    /// </summary>
    private static void DeliverScriptedKey(EmulatorSession session, KeyScript.Event ev)
    {
        if (ev.Kind == KeyScript.EventKind.Up)
        {
            session.KeyUp(ev.KeyData);
            return;
        }
        if (Hotkey(session, ev.KeyData)) return;
        session.KeyDown(ev.KeyData);
        if (ev.Text is char c) session.KeyPress(c);
    }

    /// <summary>
    /// The app's hotkeys and menu shortcuts (MainForm.ProcessCmdKey and
    /// BuildMenu). True if the key was one, so it doesn't reach the
    /// machine. Keep in step with MainForm.
    /// </summary>
    private static bool Hotkey(EmulatorSession session, HostKey key)
    {
        const HostKey Ctrl = HostKey.Control, CtrlShift = HostKey.Control | HostKey.Shift;
        switch (key)
        {
            // These change emulation, so they act here too.
            case HostKey.Alt | HostKey.F4:
                session.Stop();
                return true;
            case HostKey.Pause:
            case HostKey.Scroll:
                if (session.Active.Paused) session.Active.Resume();
                else session.Active.Pause();
                return true;
            case Ctrl | HostKey.R:
                session.Reset();
                return true;
            case Ctrl | HostKey.B:
                try
                {
                    if (session.LoadBasic() == EmulatorSession.LoadResult.BasicMissing)
                        Console.Error.WriteLine(session.BasicMissingMessage);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("BASIC load failed:\n" + ex.Message);
                }
                return true;

            // These only open windows or dialogs, or change the view:
            // nothing to do without a window, but the machine never sees
            // them in the app either.
            case HostKey.Alt | HostKey.Enter:                    // full-screen
            case Ctrl | HostKey.O:                               // Load cassette...
            case CtrlShift | HostKey.B:                          // Load BASIC source...
            case Ctrl | HostKey.S:                               // Settings tabs
            case CtrlShift | HostKey.R:
            case CtrlShift | HostKey.D:
            case CtrlShift | HostKey.K:
            case CtrlShift | HostKey.J:
            case Ctrl | HostKey.D1:                              // window scale
            case Ctrl | HostKey.D2:
            case Ctrl | HostKey.D3:
            case Ctrl | HostKey.L:                               // scanlines
            case Ctrl | HostKey.G:                               // Font Sheet
            case Ctrl | HostKey.D:                               // Debugger
            case Ctrl | HostKey.M:                               // Memory Viewer
            case Ctrl | HostKey.H:                               // HID Diagnostic
                return true;
        }
        return false;
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}
