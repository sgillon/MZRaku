using System.Drawing;
using System.Windows.Forms;

namespace MZ700Emul;

/// <summary>
/// Temporary host for <see cref="KeyboardMatrixGrid"/> during Phase A
/// of the keyboard-editor build. Once the grid is wired into Settings →
/// Keyboard at A7, this form goes away (it stays for now as a quick
/// look at the matrix without opening Settings).
/// </summary>
public sealed class KeyboardMatrixForm : Form
{
    private readonly KeyboardMatrixGrid _grid;
    private readonly CheckBox _showCoverage;
    private readonly Button _resetCoverage;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Tells WinForms not to activate the window on Show — emulator main
    // window keeps keyboard focus while the matrix opens, so the user
    // can watch the last-matched highlight pulse as they type.
    protected override bool ShowWithoutActivation => true;

    public KeyboardMatrixForm(MZ700 machine)
    {
        Text = "Keyboard Matrix";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScroll = true;

        _grid = new KeyboardMatrixGrid(machine);

        // Top bar — coverage toggle + reset. Accumulated state lives on
        // the grid and is naturally wiped when the user closes the form
        // (it's disposed; MainForm.OpenKeyboardMatrix creates a fresh
        // instance on next open).
        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 4, 4, 4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
        };
        _showCoverage = new CheckBox
        {
            Text = "Show coverage",
            AutoSize = true,
            Padding = new Padding(0, 4, 12, 0),
        };
        _showCoverage.CheckedChanged += (_, _) =>
        {
            _grid.ShowCoverage = _showCoverage.Checked;
            _grid.Invalidate();
            // The matrix form has no keyboard-driven functions, so hand
            // focus back to the emulator main window after any click on
            // our controls — saves the user a click to resume typing.
            Owner?.Activate();
        };
        _resetCoverage = new Button { Text = "Reset", AutoSize = true };
        _resetCoverage.Click += (_, _) =>
        {
            _grid.ResetCoverage();
            Owner?.Activate();
        };
        topBar.Controls.Add(_showCoverage);
        topBar.Controls.Add(_resetCoverage);

        Controls.Add(_grid);    // added first → no dock, positioned manually
        Controls.Add(topBar);   // added last → docks first to the top
        _grid.Location = new Point(0, topBar.Height);
        ClientSize = new Size(_grid.Width + 4, topBar.Height + _grid.Height + 4);

        // 100 ms tick is enough for the last-matched highlight to feel
        // live without burning cycles redrawing 80 cells faster than
        // human eyes can perceive.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _refreshTimer.Tick += (_, _) => _grid.Invalidate();
        _refreshTimer.Start();

        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        };
    }
}
