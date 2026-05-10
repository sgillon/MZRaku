using System;
using System.Windows.Forms;

namespace MZ700Emul;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? cassettePath = null;
        bool autoLoadBasic = false;
        string? dumpPath = null;
        int dumpFrame = 120;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--basic", StringComparison.OrdinalIgnoreCase) || a.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                autoLoadBasic = true;
            }
            else if (a.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase))
            {
                dumpPath = a.Substring(7);
            }
            else if (a.StartsWith("--dumpframe=", StringComparison.OrdinalIgnoreCase))
            {
                dumpFrame = int.Parse(a.Substring(12));
            }
            else if (a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h" || a == "/?")
            {
                MessageBox.Show(
                    "MZ-700 Emulator\n\n" +
                    "Usage: MZ700Emul.exe [--basic] [path\\to\\cassette.mzf|.zip]\n\n" +
                    "  --basic        Automatically load BASIC interpreter at startup\n" +
                    "  <cassette>     Automatically load a cassette image at startup.\n" +
                    "                 Accepts .mzf/.m12/.mzt or a .zip containing one.\n\n" +
                    "At runtime you may also drag-and-drop a .mzf or .zip file onto the\n" +
                    "window or use the File menu to load one.",
                    "MZ-700 Emulator");
                return;
            }
            else if (!a.StartsWith("-"))
            {
                cassettePath = a;
            }
        }

        ApplicationConfiguration.Initialize();
        var form = new MainForm(cassettePath, autoLoadBasic, dumpPath);
        form._dumpFrame = dumpFrame;
        Application.Run(form);
    }
}
