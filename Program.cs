using System;
using System.Windows.Forms;
using MZRaku.Hardware;

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
                    "                  [path\\to\\cassette.mzf|.zip]\n\n" +
                    "  --mz700         Emulate the Sharp MZ-700 for this run (default).\n" +
                    "  --mz80a         Emulate the Sharp MZ-80A for this run. Overrides\n" +
                    "                  the persisted [Machine] Type in settings.ini for\n" +
                    "                  this launch only. Change the default via System →\n" +
                    "                  Machine → MZ-80A.\n" +
                    "  --mz800         Emulate the Sharp MZ-800 (v1.3.0 in-progress).\n" +
                    "                  MZ-700-mode text + keyboard + cassette LOAD work;\n" +
                    "                  MZ-800-native bitmap graphics + PSG sound arrive\n" +
                    "                  in later phases.\n" +
                    "  --basic         Force BASIC to be loaded at startup. Usually not\n" +
                    "                  needed: BASIC cassettes auto-load BASIC anyway.\n" +
                    "  --display=N     Override the persisted window scale for this run:\n" +
                    "                  1, 2, or 3 picks the matching size; 'full' (or\n" +
                    "                  'fs') opens full-screen. settings.ini is not\n" +
                    "                  modified — Alt+Enter or the View menu still toggle.\n" +
                    "  --scanlines     Force the CRT-style scanlines overlay on for this\n" +
                    "                  run. --scanlines=off forces it off. Without the\n" +
                    "                  flag the persisted Settings → Display value wins.\n" +
                    "  <cassette>      Automatically load a cassette image at startup.\n" +
                    "                  Accepts .mzf/.m12/.mzt or a .zip containing one.\n" +
                    "                  BASIC programs trigger BASIC auto-load; machine-\n" +
                    "                  code images run directly under the monitor.\n\n" +
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
        // consumer files (SpecialKeyMap / CharMap / MzKeyboardLayout).
        // Silent if all four agree; logs to debug output otherwise. The
        // reference was introduced to catch the drift that had been
        // letting slot bugs hide for weeks at a time.
        MatrixValidation.RunAndLog();

        ApplicationConfiguration.Initialize();
        var form = new MainForm(cassettePath, autoLoadBasic, dumpPath, dumpFrame,
            displayScaleOverride, startFullScreen, scanlinesOverride, machineOverride);
        Application.Run(form);
    }
}
