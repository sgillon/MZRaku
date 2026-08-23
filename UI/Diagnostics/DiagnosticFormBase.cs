using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Shared scaffolding for the read-only diagnostic windows
/// (<see cref="HidDiagnosticForm"/> and
/// <see cref="SoundDiagnosticForm"/> today; MZ-80A sound diagnostic
/// and any future v1.3-era pane land on the same shape).
///
/// Owns three things the concrete forms all want the same way:
/// <list type="bullet">
/// <item>Window-chrome defaults: <see cref="FormBorderStyle.SizableToolWindow"/>,
/// <c>ShowInTaskbar = false</c>, <c>KeyPreview = false</c>, and the
/// <see cref="ShowWithoutActivation"/> override that keeps focus on
/// the main emulator window when the pane opens.</item>
/// <item>A monospace/grouping visual toolkit —
/// <see cref="AutoSizeMonoLabel"/>, <see cref="FillMonoLabel"/>,
/// <see cref="AutoGroup"/>, <see cref="FillGroup"/> — sized the way
/// every diagnostic pane wants (row-shaped groups collapsing to
/// contents; body-shaped groups filling their cell).</item>
/// <item>Copy / Save handlers wired to an abstract
/// <see cref="BuildFullDump"/>, plus a shared status-label field
/// that the subclass docks wherever it likes.</item>
/// </list>
///
/// FontSheetForm deliberately doesn't inherit — its layout diverges
/// too far (image + cell hit-testing rather than a labels-and-groups
/// stack). Bring extraction opportunities that widen the base
/// carefully; per-pane cost is fine, drift across similarly-shaped
/// panes isn't.
/// </summary>
internal abstract class DiagnosticFormBase : Form
{
    /// <summary>
    /// Bottom-of-form status label. Concrete forms place it inside
    /// their own layout (typically alongside the Copy / Save
    /// buttons). Copy / Save handlers below write "Copied…" /
    /// "Saved to …" / failure text into it.
    /// </summary>
    protected readonly Label StatusLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(FontFamily.GenericSansSerif, 8.5f),
        ForeColor = SystemColors.GrayText,
    };

    // Don't steal keystrokes / activation from the main window — every
    // diagnostic in this family exists to watch the main window's
    // live flow. Applied by ctor for chrome; the ShowWithoutActivation
    // override handles the activation half.
    protected override bool ShowWithoutActivation => true;

    protected DiagnosticFormBase()
    {
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        KeyPreview = false;
    }

    /// <summary>
    /// Subclass provides the plain-text snapshot Copy / Save will
    /// place on the clipboard / write to disk. Called on demand from
    /// the two handlers below.
    /// </summary>
    protected abstract string BuildFullDump();

    /// <summary>
    /// Monospace label sized to its content — used inside AutoSize
    /// group boxes for the fixed-length header panes (host input,
    /// mapping, per-counter reference cross-check).
    /// </summary>
    protected static SmoothLabel AutoSizeMonoLabel() => new()
    {
        AutoSize = true,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Margin = new Padding(4),
    };

    /// <summary>
    /// Monospace label that fills its cell — used inside Percent(100)
    /// group boxes for the body panes (matrix state, live PIT state).
    /// </summary>
    protected static SmoothLabel FillMonoLabel() => new()
    {
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        AutoSize = false,
        Padding = new Padding(4),
        TextAlign = ContentAlignment.TopLeft,
    };

    /// <summary>
    /// GroupBox that sizes to its content — for header rows whose
    /// row style is AutoSize.
    /// </summary>
    protected static GroupBox AutoGroup(string title, Control content)
    {
        var gb = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 16, 6, 6),
        };
        gb.Controls.Add(content);
        return gb;
    }

    /// <summary>
    /// GroupBox that fills its cell — for the body row whose row
    /// style is Percent(100).
    /// </summary>
    protected static GroupBox FillGroup(string title, Control content)
    {
        var gb = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 16, 6, 6),
        };
        gb.Controls.Add(content);
        return gb;
    }

    /// <summary>
    /// Places <see cref="BuildFullDump"/>'s output on the clipboard,
    /// writing "Copied…" / failure text to <see cref="StatusLabel"/>.
    /// </summary>
    protected void CopyDumpToClipboard()
    {
        try
        {
            Clipboard.SetText(BuildFullDump());
            StatusLabel.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Copy failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Prompts for a target file and writes <see cref="BuildFullDump"/>'s
    /// output there. <paramref name="defaultFilename"/> pre-fills
    /// the Save dialog; <paramref name="title"/> is the dialog
    /// caption. Errors surface in <see cref="StatusLabel"/>.
    /// </summary>
    protected void SaveDumpToFile(string defaultFilename, string title = "Save diagnostic snapshot")
    {
        using var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = defaultFilename,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, BuildFullDump());
            StatusLabel.Text = $"Saved to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }
    }
}
