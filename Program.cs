using System;
using System.Windows.Forms;

namespace MZRaku;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? cassettePath = null;
        bool autoLoadBasic = false;
        string? dumpPath = null;
        int dumpFrame = 120;
        string? keysPath = null;
        string? insertPath = null;
        string? settingsPath = null;
        int? displayScaleOverride = null;
        bool startFullScreen = false;
        bool? scanlinesOverride = null;
        MachineType? machineOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--basic", StringComparison.OrdinalIgnoreCase) || a.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                autoLoadBasic = true;
            }
            else if (a.Equals("--mz700", StringComparison.OrdinalIgnoreCase))
            {
                machineOverride = MachineType.MZ700;
            }
            else if (a.Equals("--mz80a", StringComparison.OrdinalIgnoreCase))
            {
                machineOverride = MachineType.MZ80A;
            }
            else if (a.Equals("--mz800", StringComparison.OrdinalIgnoreCase))
            {
                machineOverride = MachineType.MZ800;
            }
            else if (a.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase))
            {
                dumpPath = a.Substring(7);
            }
            else if (a.StartsWith("--dumpframe=", StringComparison.OrdinalIgnoreCase))
            {
                dumpFrame = int.Parse(a.Substring(12));
            }
            else if (a.StartsWith("--keys=", StringComparison.OrdinalIgnoreCase))
            {
                keysPath = a.Substring(7);
            }
            else if (a.StartsWith("--insert=", StringComparison.OrdinalIgnoreCase))
            {
                // Insert a cassette for the machine's own LOAD, as File →
                // Insert cassette does.
                insertPath = a.Substring(9);
            }
            else if (a.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                // Run with another INI, read-only: nothing this run
                // changes is written back (Settings.ReadOnlyPath).
                settingsPath = a.Substring(11);
            }
            else if (a.Equals("--scanlines", StringComparison.OrdinalIgnoreCase))
            {
                scanlinesOverride = true;
            }
            else if (a.StartsWith("--scanlines=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Substring(12).Trim();
                if (v.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || v == "1")
                {
                    scanlinesOverride = true;
                }
                else if (v.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || v == "0")
                {
                    scanlinesOverride = false;
                }
                else
                {
                    MessageBox.Show(
                        $"--scanlines value '{v}' isn't recognised. Expected on or off.",
                        "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (a.StartsWith("--display=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Substring(10).Trim();
                if (v.Equals("full", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("fullscreen", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("fs", StringComparison.OrdinalIgnoreCase))
                {
                    startFullScreen = true;
                }
                else if (int.TryParse(v, out var n) && n >= 1 && n <= 3)
                {
                    displayScaleOverride = n;
                }
                else
                {
                    MessageBox.Show(
                        $"--display value '{v}' isn't recognised. Expected 1, 2, 3, or full.",
                        "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h" || a == "/?")
            {
                MessageBox.Show(
                    "MZRaku — Sharp MZ-700 / MZ-80A / MZ-800 emulator\n\n" +
                    "Usage: MZRaku.exe [--mz700|--mz80a|--mz800] [--basic] [--display=N]\n" +
                    "                  [--scanlines[=off]] [--settings=<ini>]\n" +
                    "                  [--insert=<cassette>] [--keys=<file>]\n" +
                    "                  [path\\to\\cassette.mzf|.zip]\n\n" +
                    "  --mz700         Emulate the Sharp MZ-700 for this run.\n" +
                    "  --mz80a         Emulate the Sharp MZ-80A for this run.\n" +
                    "  --mz800         Emulate the Sharp MZ-800 for this run.\n" +
                    "                  Each overrides [Machine] DefaultMachine in\n" +
                    "                  settings.ini for this launch only; change the\n" +
                    "                  default in Settings → Startup. Without a flag\n" +
                    "                  the default machine runs (MZ-700 out of the box).\n" +
                    "  --basic         Force BASIC to be loaded at startup. Usually not\n" +
                    "                  needed: BASIC cassettes auto-load BASIC anyway.\n" +
                    "  --display=N     Override the persisted window scale for this run:\n" +
                    "                  1, 2, or 3 picks the matching size; 'full' (or\n" +
                    "                  'fs') opens full-screen. settings.ini is not\n" +
                    "                  modified — Alt+Enter or the View menu still toggle.\n" +
                    "  --scanlines     Force the CRT-style scanlines overlay on for this\n" +
                    "                  run. --scanlines=off forces it off. Without the\n" +
                    "                  flag the persisted Settings → Display value wins.\n" +
                    "  --settings=<ini>  Run with another settings file, read-only:\n" +
                    "                  nothing this run changes is written back to it\n" +
                    "                  (or to settings.ini). The title bar names it.\n" +
                    "  --insert=<cassette>  Insert a cassette for the machine's own\n" +
                    "                  LOAD, as File → Insert cassette does: type L in\n" +
                    "                  the monitor or LOAD in BASIC to read it.\n" +
                    "                  MZ-700 and MZ-800.\n" +
                    "  --keys=<file>   Play scripted keystrokes, one command per line at\n" +
                    "                  a frame count since launch: '420 type \"RUN\"',\n" +
                    "                  '440 key Return', or down/up <Key> to hold one.\n" +
                    "                  Full syntax: docs/usage/keys-script.md in the\n" +
                    "                  MZRaku repository on GitHub.\n" +
                    "  <cassette>      Automatically load a cassette image at startup.\n" +
                    "                  Accepts .mzf/.m12/.mzt or a .zip containing one.\n" +
                    "                  BASIC programs trigger BASIC auto-load; machine-\n" +
                    "                  code images run directly under the monitor.\n\n" +
                    "Sharp firmware is not included. Put it in a 'roms' (or 'basic')\n" +
                    "folder next to the executable:\n" +
                    "  MZ-700  1z-013a.rom, mz700fon.int, 1Z-013B.mzf\n" +
                    "  MZ-80A  SA-1510.rom, SA-CG.rom, SA-5510.mzf\n" +
                    "  MZ-800  MZ800.ROM, 1Z-016.mzf\n\n" +
                    "At runtime you may also drag-and-drop a .mzf or .zip file onto the\n" +
                    "window or use the File menu to load one.",
                    "MZRaku");
                return;
            }
            else if (!a.StartsWith("-"))
            {
                cassettePath = a;
            }
        }

        // Cross-check the canonical matrix reference against its
        // consumer files (Mz700SpecialKeyMap / CharMap / Mz700KeyboardLayout).
        // Silent if all four agree; logs to debug output and shows a
        // warning dialog otherwise. The reference was introduced to
        // catch the drift that had been letting slot bugs hide for
        // weeks at a time.
        MatrixValidation.RunAndLog((text, title) => MessageBox.Show(
            text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        // HostKey must still mirror WinForms Keys: the front end casts
        // between them and saved key overrides use the names.
        HostKeyParity.RunAndLog();

        // --keys=<file>: scripted host input (regression suite). A bad
        // script exits with code 2 before the emulator starts; the
        // message goes to stderr, plus a dialog unless this is a --dump
        // run, which must never block on one.
        KeyScript? keyScript = null;
        if (keysPath != null)
        {
            try
            {
                keyScript = KeyScript.Load(keysPath);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                var msg = $"--keys script {keysPath}: {ex.Message}";
                Console.Error.WriteLine(msg);
                if (dumpPath == null)
                    MessageBox.Show(msg, "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.Exit(2);
            }
        }

        // --settings=<path>: a file that can't be read stops here, like a
        // bad --keys script, rather than running on defaults.
        Settings settings;
        try
        {
            settings = Settings.Load(settingsPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            var msg = $"--settings file {settingsPath}: {ex.Message}";
            Console.Error.WriteLine(msg);
            if (dumpPath == null)
                MessageBox.Show(msg, "MZRaku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.Exit(2);
            return;
        }

        ApplicationConfiguration.Initialize();
        var form = new MainForm(settings, cassettePath, autoLoadBasic, dumpPath, dumpFrame,
            displayScaleOverride, startFullScreen, scanlinesOverride, machineOverride, keyScript, insertPath);
        Application.Run(form);
    }
}
