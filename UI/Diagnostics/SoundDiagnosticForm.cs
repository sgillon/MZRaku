using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Live "what's the PIT actually doing right now" view, designed to
/// support the sound-investigation arc kicked off in v0.0.9-preview
/// work (boot tone missing, MUSIC timings off).
///
/// Three panes:
/// 1. **PIT state** — per-counter mode / reload / value / running /
///    gate / out, refreshed each frame.
/// 2. **Reference cross-check** — for each counter, what
///    <see cref="Mz700SoundReference"/> says the topology / mode /
///    gate source should be. Lets the user spot drift between the
///    live emulation and the canonical reference at a glance.
/// 3. **Event log** — small ring buffer of recent PIT writes and PC3
///    (speaker gate) transitions, timestamped by host frame. The
///    log is what surfaces "boot tone short and we lost it" — if
///    the writes are visible here but no sound came out, the bug is
///    in the synthesis pipeline rather than the ROM-to-PIT path.
///
/// Opened from Debug → Sound Diagnostic. Read-only, no controls.
/// </summary>
public sealed class SoundDiagnosticForm : Form
{
    private readonly MZ700 _machine;

    private readonly SmoothLabel _stateLabel = AutoSizeMonoLabel();
    private readonly SmoothLabel _referenceLabel = AutoSizeMonoLabel();
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        WordWrap = false,
        BackColor = Color.White,
    };
    private readonly Label _statusLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(FontFamily.GenericSansSerif, 8.5f),
        ForeColor = SystemColors.GrayText,
    };

    private readonly Queue<string> _eventLog = new();
    private const int EventLogCap = 40;
    private int _frame;

    // Don't steal focus from the main emulator window when this form
    // opens — the whole point is to watch what the running emulator's
    // doing.
    protected override bool ShowWithoutActivation => true;

    public SoundDiagnosticForm(MZ700 machine)
    {
        _machine = machine;

        Text = "Sound Diagnostic";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(420, 380);
        ShowInTaskbar = false;
        KeyPreview = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(6),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(AutoGroup("PIT state (live)", _stateLabel), 0, 0);
        root.Controls.Add(AutoGroup("Reference cross-check", _referenceLabel), 0, 1);
        root.Controls.Add(FillGroup("Event log (newest at bottom — selectable / copy with Ctrl+C)", _logBox), 0, 2);
        root.Controls.Add(BuildButtonRow(), 0, 3);
        Controls.Add(root);

        _machine.Pit.OnWrite += OnPitWrite;
        _machine.Ppi.SpeakerGateChanged += OnSpeakerGate;
        _machine.Io.OnE008Write += OnE008Write;
        FormClosed += (_, _) =>
        {
            _machine.Pit.OnWrite -= OnPitWrite;
            _machine.Ppi.SpeakerGateChanged -= OnSpeakerGate;
            _machine.Io.OnE008Write -= OnE008Write;
        };
    }

    private static SmoothLabel AutoSizeMonoLabel() => new()
    {
        AutoSize = true,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Margin = new Padding(2),
    };

    private static SmoothLabel FillMonoLabel() => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Margin = new Padding(2),
    };

    private static GroupBox AutoGroup(string title, Control content)
    {
        var gb = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 16, 6, 6),
        };
        gb.Controls.Add(content);
        return gb;
    }

    private static GroupBox FillGroup(string title, Control content)
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

    private Control BuildButtonRow()
    {
        var copyBtn = new Button { Text = "Copy", AutoSize = true, Margin = new Padding(3) };
        copyBtn.Click += (_, _) => CopyToClipboard();
        var saveBtn = new Button { Text = "Save…", AutoSize = true, Margin = new Padding(3) };
        saveBtn.Click += (_, _) => SaveToFile();
        var clearBtn = new Button { Text = "Clear log", AutoSize = true, Margin = new Padding(3) };
        clearBtn.Click += (_, _) => { _eventLog.Clear(); _logBox.Text = ""; };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
        };
        flow.Controls.Add(copyBtn);
        flow.Controls.Add(saveBtn);
        flow.Controls.Add(clearBtn);
        flow.Controls.Add(_statusLabel);
        return flow;
    }

    /// <summary>Called once per frame by MainForm.Timer_Tick.</summary>
    public void RefreshIfVisible()
    {
        _frame++;
        if (!Visible) return;
        _stateLabel.Text = BuildStateText();
        _referenceLabel.Text = BuildReferenceText();
        // Refresh log only when it's actually changed so the user can
        // make a text selection in the box without it being wiped each
        // frame.
        var current = BuildLogText();
        if (_logBox.Text != current)
        {
            int prevSelStart = _logBox.SelectionStart;
            int prevSelLen = _logBox.SelectionLength;
            _logBox.Text = current;
            // Scroll to the newest entry (bottom).
            _logBox.SelectionStart = _logBox.Text.Length;
            _logBox.ScrollToCaret();
            // Restore any user selection that was in range.
            if (prevSelLen > 0 && prevSelStart + prevSelLen <= current.Length)
            {
                _logBox.SelectionStart = prevSelStart;
                _logBox.SelectionLength = prevSelLen;
            }
        }
    }

    private string BuildStateText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cnt  Mode  Reload   Value   Running  Gate  OUT");
        for (int i = 0; i < 3; i++)
        {
            var c = _machine.Pit.Counters[i];
            sb.AppendLine($"C{i}    {c.Mode,2}    ${c.Reload:X4}   ${c.Value:X4}    {(c.Running ? "yes" : "no ")}      {(c.Gate ? 1 : 0)}     {(c.Out ? 1 : 0)}");
        }
        sb.AppendLine();
        bool pc3 = _machine.Ppi.SpeakerGate;
        bool hardGate = _machine.Sound.HardGate;
        double cnt0Freq = (_machine.Pit.Counters[0].Reload >= 2)
            ? 895_000.0 / _machine.Pit.Counters[0].Reload : 0;
        sb.AppendLine($"PPI PC3 (soft gate)     : {(pc3 ? "1 (open)" : "0 (mute)")}");
        sb.AppendLine($"$E008 D0 (hard gate)    : {(hardGate ? "1 (open)" : "0 (mute)")}");
        sb.AppendLine($"Audible (soft AND hard) : {(pc3 && hardGate ? "yes" : "no ")}");
        sb.AppendLine($"Counter 0 freq (calc'd) : {cnt0Freq:0} Hz");
        return sb.ToString();
    }

    private static string BuildReferenceText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cnt  Clock          Gate              Mode  Purpose");
        foreach (var spec in Mz700SoundReference.Counters)
        {
            string clock = spec.Clock switch
            {
                Mz700SoundReference.ClockSource.Soin895kHz       => "895 kHz SOIN",
                Mz700SoundReference.ClockSource.HBlank15p6kHz    => "15.6 kHz HBLNK",
                Mz700SoundReference.ClockSource.CascadeFromOut1  => "C1.OUT1 cascade",
                _ => "?",
            };
            string gate = spec.Gate switch
            {
                Mz700SoundReference.GateSource.AlwaysHigh           => "+5V (always)",
                Mz700SoundReference.GateSource.FlipFlopGate0FromPc3 => "FF2 (PC3-latch)",
                _ => "?",
            };
            sb.AppendLine($"C{(int)spec.Counter}    {clock,-14} {gate,-17} {(int)spec.ProgrammedMode}     {spec.Purpose}");
        }
        sb.AppendLine();
        sb.AppendLine("Speaker NAND: NAND(C0.OUT, FF1.Q)  → audio amp");
        sb.AppendLine("  FF1.Q ← D0 latched on every write to $E008 (cleared by RESET)");
        return sb.ToString();
    }

    private string BuildLogText() => string.Join("\n", _eventLog);

    private void OnPitWrite(int reg, byte val)
    {
        string entry;
        if (reg == 3)
        {
            int sc = (val >> 6) & 3;
            int rw = (val >> 4) & 3;
            int mode = (val >> 1) & 7;
            entry = $"[F{_frame,5}] CTRL ${val:X2}: counter={sc} rw={rw} mode={mode}{(rw == 0 ? " (LATCH)" : "")}";
        }
        else
        {
            entry = $"[F{_frame,5}] C{reg} <- ${val:X2}";
        }
        Push(entry);
    }

    private void OnSpeakerGate(bool open) =>
        Push($"[F{_frame,5}] PPI PC3 → {(open ? "1 (soft gate open)" : "0 (soft gate mute)")}");

    private void OnE008Write(byte val) =>
        Push($"[F{_frame,5}] $E008 ← ${val:X2}  (hard gate D0 = {(val & 1)})");

    private void Push(string entry)
    {
        _eventLog.Enqueue(entry);
        while (_eventLog.Count > EventLogCap) _eventLog.Dequeue();
    }

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetText(BuildFullDump());
            _statusLabel.Text = "Copied to clipboard.";
        }
        catch (Exception ex) { _statusLabel.Text = $"Copy failed: {ex.Message}"; }
    }

    private void SaveToFile()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text|*.txt|All files|*.*",
            FileName = $"sound-diag-frame{_frame}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, BuildFullDump());
            _statusLabel.Text = $"Saved to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex) { _statusLabel.Text = $"Save failed: {ex.Message}"; }
    }

    private string BuildFullDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Sound Diagnostic dump (frame {_frame}) --");
        sb.AppendLine();
        sb.AppendLine("PIT state:");
        sb.AppendLine(BuildStateText());
        sb.AppendLine();
        sb.AppendLine("Reference cross-check:");
        sb.AppendLine(BuildReferenceText());
        sb.AppendLine();
        sb.AppendLine("Event log (oldest first):");
        foreach (var line in _eventLog) sb.AppendLine(line);
        return sb.ToString();
    }
}
