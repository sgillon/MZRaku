using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Help → About dialog. Surfaces project name, version (read from the
/// assembly's InformationalVersion — bump &lt;Version&gt; in the csproj
/// to change), build date, GitHub link, and ROM / AI acknowledgements.
/// </summary>
public sealed class AboutForm : Form
{
    private const string GitHubUrl = "https://github.com/sgillon/MZRaku";
    private const string LauncherDocUrl = "https://github.com/sgillon/MZRaku/blob/main/docs/usage/launcher-setup.md";

    public AboutForm()
    {
        Text = "About MZRaku";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = SystemColors.Window;
        Icon = LoadEmbeddedIcon();
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SystemColors.Window,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // body labels
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // close button

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);

        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.OK,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 8, 0, 0),
        };
        close.Click += (_, _) => Close();
        root.Controls.Add(close, 0, 2);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(root);
    }

    private static Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var iconPic = new PictureBox
        {
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 12, 0),
        };
        using (var ic = LoadEmbeddedIcon())
            if (ic != null) iconPic.Image = ic.ToBitmap();
        header.Controls.Add(iconPic, 0, 0);

        var textStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            WrapContents = false,
        };
        textStack.Controls.Add(new Label
        {
            Text = "MZRaku",
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0),
        });
        textStack.Controls.Add(new Label
        {
            Text = $"Version {GetInformationalVersion()}",
            AutoSize = true,
            ForeColor = SystemColors.ControlDarkDark,
            Margin = new Padding(0, 2, 0, 0),
        });
        textStack.Controls.Add(new Label
        {
            Text = $"Built {GetBuildDate():yyyy-MM-dd}",
            AutoSize = true,
            ForeColor = SystemColors.ControlDarkDark,
            Margin = new Padding(0, 1, 0, 0),
        });
        header.Controls.Add(textStack, 1, 0);
        return header;
    }

    private static Control BuildBody()
    {
        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        body.Controls.Add(new Label
        {
            Text = "A Sharp MZ-700 emulator written in C#/.NET 8.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        });

        body.Controls.Add(BuildLinkRow("Project:", GitHubUrl, GitHubUrl));
        body.Controls.Add(BuildLinkRow("Launcher setup:", "docs/usage/launcher-setup.md", LauncherDocUrl));

        body.Controls.Add(new Label
        {
            Text = "ROM and BASIC images are © Sharp Corporation and are "
                + "not distributed with this project — see the Quickstart "
                + "in README.md for the files you need to supply.",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.ControlDarkDark,
            Margin = new Padding(0, 12, 0, 8),
        });

        body.Controls.Add(new Label
        {
            Text = "Emulator code is AI-generated, written collaboratively "
                + "with Claude (Anthropic).",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.ControlDarkDark,
            Margin = new Padding(0, 0, 0, 0),
        });

        return body;
    }

    private static Control BuildLinkRow(string label, string text, string url)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 4),
            WrapContents = false,
        };
        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 3, 6, 0),
        });
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        row.Controls.Add(link);
        return row;
    }

    private static string GetInformationalVersion()
    {
        var asm = typeof(AboutForm).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // The SDK appends "+<gitsha>" to InformationalVersion when a
            // source-link / git build is in play; strip it for display.
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "(unknown)";
    }

    private static DateTime GetBuildDate()
    {
        // Assembly.Location returns "" under PublishSingleFile, so use
        // the host exe's mtime via AppContext.BaseDirectory. The exe is
        // re-stamped on every build, so its modified time is a faithful
        // build-date proxy.
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "MZRaku.exe");
            if (File.Exists(exe)) return File.GetLastWriteTime(exe);
        }
        catch { /* fall through */ }
        return DateTime.Now;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't open browser:\n" + ex.Message, "About",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var asm = typeof(AboutForm).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("MZRaku.ico", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }
}
