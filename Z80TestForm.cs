using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MZ700Emul;

/// <summary>
/// Modeless companion window that runs a CP/M Z80 test ROM (ZEXDOC,
/// ZEXALL) and streams its console output into a monospace TextBox.
/// Drives <see cref="Z80TestRunner"/>; UI thread receives chunks via
/// BeginInvoke so the test stays on its own background thread.
/// </summary>
public sealed class Z80TestForm : Form
{
    private readonly MZ700 _machine;
    private readonly string _comPath;
    private readonly TextBox _output;
    private readonly Button _stopBtn;
    private readonly Button _saveBtn;
    private readonly Button _closeBtn;
    private readonly Label _statusLbl;
    private Z80TestRunner? _runner;
    private CancellationTokenSource? _cts;
    private bool _completed;

    public Z80TestForm(MZ700 machine, string comPath)
    {
        _machine = machine;
        _comPath = comPath;

        Text = $"Z80 Test — {Path.GetFileName(comPath)}";
        Width = 760;
        Height = 580;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(400, 240);

        _output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            WordWrap = false,
            BackColor = Color.White,
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        _closeBtn = new Button { Text = "Close", Width = 80 };
        _closeBtn.Click += (_, _) => Close();
        _saveBtn = new Button { Text = "Save…", Width = 80 };
        _saveBtn.Click += OnSave;
        _stopBtn = new Button { Text = "Stop", Width = 80 };
        _stopBtn.Click += OnStop;
        bottom.Controls.Add(_closeBtn);
        bottom.Controls.Add(_saveBtn);
        bottom.Controls.Add(_stopBtn);

        _statusLbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Text = "Running…",
        };

        // Add fill control last so docked controls claim their space first.
        Controls.Add(_output);
        Controls.Add(bottom);
        Controls.Add(_statusLbl);

        Shown += OnShown;
        FormClosing += OnFormClosing;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        _runner = new Z80TestRunner(_machine,
            chunk => BeginInvoke(new Action(() => AppendOutput(chunk))),
            cancelled => BeginInvoke(new Action(() => OnComplete(cancelled))),
            _cts.Token);
        try
        {
            _runner.Start(_comPath);
        }
        catch (Exception ex)
        {
            _statusLbl.Text = "Failed: " + ex.Message;
            _stopBtn.Enabled = false;
        }
    }

    private void AppendOutput(string chunk)
    {
        if (IsDisposed) return;
        // CP/M emits CR+LF; TextBox wants Environment.NewLine for line breaks.
        var s = chunk.Replace("\r\n", "\n").Replace("\r", "\n")
                     .Replace("\n", Environment.NewLine);
        _output.AppendText(s);
    }

    private void OnComplete(bool cancelled)
    {
        if (IsDisposed) return;
        _completed = true;
        _statusLbl.Text = cancelled ? "Stopped." : "Complete.";
        _stopBtn.Enabled = false;
    }

    private void OnStop(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _runner?.Stop();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // If the test is still running, cancel and wait briefly so the
        // runner restores machine state before the form is gone.
        if (!_completed)
        {
            _cts?.Cancel();
            _runner?.Stop();
            _runner?.Join(2000);
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save Z80 test output",
            Filter = "Text|*.txt|All files|*.*",
            FileName = Path.GetFileNameWithoutExtension(_comPath)
                       + $"-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try { File.WriteAllText(dlg.FileName, _output.Text); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message,
                    "Z80 Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
