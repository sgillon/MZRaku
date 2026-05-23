using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Z80Core;

namespace MZ700Emul;

/// <summary>
/// Debugger window. Phase 1 — execution control (pause/resume/single-step/
/// step-frame), live Z80 register view, address-based breakpoints. Phase 2
/// adds a disassembly pane with PC highlight, breakpoint markers, and
/// double-click breakpoint toggle.
///
/// Opened from MainForm's Debug menu; MainForm pumps
/// <see cref="RefreshIfVisible"/> once per frame. Everything runs on the
/// WinForms UI thread — "paused" means <see cref="MZ700.RunFrame"/>
/// early-returns, never that a thread is blocked, so the window stays
/// responsive.
///
/// The whole window is laid out inside a single root TableLayoutPanel
/// (3 columns × 3 rows) so there is no Dock z-order ambiguity.
/// </summary>
public sealed class DebuggerForm : Form
{
    // $E000-$E00F is the MZ-700 PPI/PIT I/O window — reads have hardware
    // side effects (PIT counter latches, keyboard scan). Disassembly and
    // raw byte display must never disturb hardware state, so report zero
    // there. Passed to the (otherwise machine-agnostic) Z80 disassembler.
    private static readonly Func<ushort, bool> IsMzIoWindow =
        a => a >= 0xE000 && a <= 0xE00F;

    private readonly MZ700 _machine;
    private readonly Action _resetMachine;

    private readonly Button _btnPause = new();
    private readonly Button _btnStep = new();
    private readonly Button _btnStepFrame = new();
    private readonly Button _btnReset = new();
    private readonly SmoothLabel _regLabel = new();

    private readonly TextBox _gotoAddr = new();
    private readonly Button _btnGoto = new();
    private readonly CheckBox _chkFollow = new() { Text = "Follow PC", Checked = true, AutoSize = true };
    private readonly SmoothListBox _disasmList = new();

    private readonly TextBox _bpAddr = new();
    private readonly Button _bpAdd = new();
    private readonly Button _bpRemove = new();
    private readonly ListBox _bpList = new();
    private readonly SmoothLabel _statusLabel = new();

    private readonly Font _mono;

    private struct DisasmLine { public ushort Addr; public int Len; public string Text; }
    private readonly List<DisasmLine> _lines = new();
    private ushort _viewBase;

    // Track previous draw inputs so per-frame Refresh only repaints what
    // actually changed — avoids the flicker you get from forcing a full
    // owner-draw repaint of the listbox every frame.
    private ushort _lastDrawnPC = 0xFFFF;
    private bool _lastDrawnPaused;
    private int _lastDrawnBpVersion;
    private int _bpVersion;

    public DebuggerForm(MZ700 machine, Action resetMachine)
    {
        _machine = machine;
        _resetMachine = resetMachine;

        Text = "Debugger";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ClientSize = new Size(880, 500);
        MinimumSize = new Size(720, 380);
        KeyPreview = true;
        DoubleBuffered = true;
        ShowInTaskbar = false;

        _mono = new Font(FontFamily.GenericMonospace, 9f);

        // ===== root layout: 3 columns × 3 rows =====
        var root = new SmoothTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240f));   // registers
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));    // disassembly
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220f));   // breakpoints
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));          // button bar
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));          // main content
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));          // status line

        // --- button bar (row 0, spans 3) ---
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2),
        };
        ConfigButton(_btnPause, "Pause (F5)", 104, (_, _) => TogglePause());
        ConfigButton(_btnStep, "Step (F10)", 88, (_, _) => DoStep());
        ConfigButton(_btnStepFrame, "Step Frame (F11)", 122, (_, _) => DoStepFrame());
        ConfigButton(_btnReset, "Reset", 64, (_, _) => DoReset());
        buttonPanel.Controls.AddRange(new Control[] { _btnPause, _btnStep, _btnStepFrame, _btnReset });
        root.Controls.Add(buttonPanel, 0, 0);
        root.SetColumnSpan(buttonPanel, 3);

        // --- register view (row 1, col 0) ---
        _regLabel.Dock = DockStyle.Fill;
        _regLabel.AutoSize = false;
        _regLabel.Font = _mono;
        _regLabel.BackColor = Color.White;
        _regLabel.BorderStyle = BorderStyle.FixedSingle;
        _regLabel.TextAlign = ContentAlignment.TopLeft;
        _regLabel.Padding = new Padding(6);
        _regLabel.UseMnemonic = false;
        root.Controls.Add(_regLabel, 0, 1);

        // --- disassembly pane (row 1, col 1) ---
        root.Controls.Add(BuildDisasmPanel(), 1, 1);

        // --- breakpoint manager (row 1, col 2) ---
        root.Controls.Add(BuildBreakpointPanel(), 2, 1);

        // --- status line (row 2, spans 3) ---
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = false;
        _statusLabel.BorderStyle = BorderStyle.Fixed3D;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(4, 0, 0, 0);
        root.Controls.Add(_statusLabel, 0, 2);
        root.SetColumnSpan(_statusLabel, 3);

        Controls.Add(root);

        FormClosing += OnFormClosing;
        Shown += (_, _) => { _viewBase = _machine.Cpu.PC; RegenerateDisasm(); };
        UpdateAll();
    }

    private Control BuildDisasmPanel()
    {
        var panel = new SmoothTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));   // toolbar
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // listbox

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2),
        };
        var lbl = new Label { Text = "Goto $", AutoSize = true, Margin = new Padding(2, 7, 0, 0) };
        _gotoAddr.Width = 60;
        _gotoAddr.Font = _mono;
        _gotoAddr.MaxLength = 5;
        _gotoAddr.Margin = new Padding(2, 4, 4, 0);
        _gotoAddr.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DoGoto(); e.SuppressKeyPress = true; } };
        ConfigButton(_btnGoto, "Go", 40, (_, _) => DoGoto());
        _chkFollow.Margin = new Padding(8, 7, 0, 0);
        _chkFollow.CheckedChanged += (_, _) =>
        {
            if (_chkFollow.Checked) { _viewBase = _machine.Cpu.PC; RegenerateDisasm(); }
        };
        toolbar.Controls.AddRange(new Control[] { lbl, _gotoAddr, _btnGoto, _chkFollow });
        panel.Controls.Add(toolbar, 0, 0);

        _disasmList.Dock = DockStyle.Fill;
        _disasmList.Font = _mono;
        _disasmList.IntegralHeight = false;
        _disasmList.DrawMode = DrawMode.OwnerDrawFixed;
        _disasmList.ItemHeight = _mono.Height + 2;
        _disasmList.BorderStyle = BorderStyle.FixedSingle;
        _disasmList.DrawItem += OnDisasmDraw;
        _disasmList.DoubleClick += (_, _) => ToggleBreakpointAtSelected();
        _disasmList.MouseWheel += OnDisasmMouseWheel;
        _disasmList.KeyDown += OnDisasmKeyDown;
        panel.Controls.Add(_disasmList, 0, 1);
        return panel;
    }

    private Control BuildBreakpointPanel()
    {
        var bpPanel = new SmoothTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        bpPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bpPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));   // input row
        bpPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // list

        var bpInput = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2),
        };
        var bpLabel = new Label { Text = "Addr $", AutoSize = true, Margin = new Padding(2, 7, 0, 0) };
        _bpAddr.Width = 56;
        _bpAddr.Font = _mono;
        _bpAddr.MaxLength = 5;
        _bpAddr.Margin = new Padding(2, 4, 4, 0);
        _bpAddr.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddBreakpoint(); e.SuppressKeyPress = true; } };
        ConfigButton(_bpAdd, "Add", 44, (_, _) => AddBreakpoint());
        ConfigButton(_bpRemove, "Del", 44, (_, _) => RemoveSelectedBreakpoint());
        bpInput.Controls.AddRange(new Control[] { bpLabel, _bpAddr, _bpAdd, _bpRemove });
        bpPanel.Controls.Add(bpInput, 0, 0);

        _bpList.Dock = DockStyle.Fill;
        _bpList.Font = _mono;
        _bpList.IntegralHeight = false;
        _bpList.DoubleClick += (_, _) => RemoveSelectedBreakpoint();
        bpPanel.Controls.Add(_bpList, 0, 1);
        return bpPanel;
    }

    private static void ConfigButton(Button b, string text, int width, EventHandler onClick)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 28;
        b.TabStop = false;
        b.Click += onClick;
    }

    /// <summary>
    /// F5 = pause/resume, F10 = single-step, F11 = step-frame. Uses
    /// ProcessCmdKey so the function keys are caught reliably (F10 would
    /// otherwise be swallowed as a menu-activation key).
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: TogglePause(); return true;
            case Keys.F10: DoStep(); return true;
            case Keys.F11: DoStepFrame(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // --- execution control ----------------------------------------------

    private void TogglePause()
    {
        if (_machine.Paused) _machine.Resume();
        else _machine.Pause();
        UpdateAll();
    }

    private void DoStep()
    {
        _machine.StepInstruction();   // also leaves the machine paused
        EnsurePCInView();
        UpdateAll();
    }

    private void DoStepFrame()
    {
        _machine.Pause();
        _machine.StepFrame();         // honoured by the next Timer_Tick
        UpdateAll();
    }

    private void DoReset()
    {
        _resetMachine();
        _viewBase = _machine.Cpu.PC;
        RegenerateDisasm();
        UpdateAll();
    }

    // --- disassembly ----------------------------------------------------

    private int VisibleLineCount()
    {
        int h = _disasmList.ClientSize.Height;
        if (h <= 0 || _disasmList.ItemHeight <= 0) return 32;
        return Math.Max(8, h / _disasmList.ItemHeight + 1);
    }

    private void RegenerateDisasm()
    {
        _lines.Clear();
        int n = VisibleLineCount();
        ushort cursor = _viewBase;
        for (int i = 0; i < n; i++)
        {
            var res = Z80Disassembler.Disassemble(_machine.Mem, cursor, IsMzIoWindow);
            string bytes = FormatBytes(cursor, res.Length);
            _lines.Add(new DisasmLine
            {
                Addr = cursor,
                Len = res.Length,
                Text = $"{cursor:X4}  {bytes,-12}{res.Text}",
            });
            cursor = (ushort)(cursor + res.Length);
        }
        _disasmList.BeginUpdate();
        _disasmList.Items.Clear();
        foreach (var l in _lines) _disasmList.Items.Add(l.Text);
        _disasmList.EndUpdate();
        _disasmList.Invalidate();
    }

    private string FormatBytes(ushort addr, int len)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < len && i < 4; i++)
        {
            if (i > 0) sb.Append(' ');
            ushort a = (ushort)(addr + i);
            byte b = (a >= 0xE000 && a <= 0xE00F) ? (byte)0 : _machine.Mem.Read(a);
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private void OnDisasmDraw(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _lines.Count) { e.DrawBackground(); return; }
        var line = _lines[e.Index];
        var c = _machine.Cpu;

        bool isPC = line.Addr == c.PC;
        bool isBp = c.Breakpoints[line.Addr];
        bool selected = (e.State & DrawItemState.Selected) != 0;

        Color back =
            isPC && isBp ? Color.FromArgb(255, 200, 120) :
            isPC         ? Color.FromArgb(255, 240, 160) :
            isBp         ? Color.FromArgb(255, 220, 220) :
            selected     ? SystemColors.Highlight :
                           SystemColors.Window;
        Color fore =
            selected && !isPC && !isBp ? SystemColors.HighlightText :
            isBp                       ? Color.DarkRed :
                                         SystemColors.WindowText;

        using (var bg = new SolidBrush(back)) e.Graphics.FillRectangle(bg, e.Bounds);

        string marker = isBp ? "*" : (isPC ? ">" : " ");
        string txt = $" {marker} {line.Text}";

        // TextRenderer (GDI) is markedly faster and less flickery than
        // Graphics.DrawString (GDI+) for owner-drawn items refreshing at
        // 60 Hz — particularly with a monospace font.
        TextRenderer.DrawText(e.Graphics, txt, _disasmList.Font,
            new Point(e.Bounds.Left + 2, e.Bounds.Top + 1), fore,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private void OnDisasmMouseWheel(object? sender, MouseEventArgs e)
    {
        int delta = e.Delta > 0 ? -3 : 3;
        ScrollByInstructions(delta);
        if (_chkFollow.Checked) _chkFollow.Checked = false;
    }

    private void OnDisasmKeyDown(object? sender, KeyEventArgs e)
    {
        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollByInstructions(-1); break;
            case Keys.Down: ScrollByInstructions(1); break;
            case Keys.PageUp: ScrollByInstructions(-(VisibleLineCount() / 2)); break;
            case Keys.PageDown: ScrollByInstructions(VisibleLineCount() / 2); break;
            case Keys.Home: _viewBase = _machine.Cpu.PC; RegenerateDisasm(); break;
            default: handled = false; break;
        }
        if (handled)
        {
            // Manual scroll → stop tracking PC. Home (re-centre on PC) is the
            // explicit way back; don't clobber the user's positioning otherwise.
            if (_chkFollow.Checked && e.KeyCode != Keys.Home) _chkFollow.Checked = false;
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    private void ScrollByInstructions(int delta)
    {
        if (delta == 0) return;
        ushort cursor = _viewBase;
        if (delta > 0)
        {
            for (int i = 0; i < delta; i++)
            {
                var res = Z80Disassembler.Disassemble(_machine.Mem, cursor, IsMzIoWindow);
                cursor = (ushort)(cursor + res.Length);
            }
        }
        else
        {
            // Reverse-walking Z80 is fundamentally ambiguous (variable-length
            // ops). For each step backwards, try offsets 1..6 and pick the one
            // whose forward disassembly lands exactly on the current cursor.
            for (int i = 0; i < -delta; i++)
            {
                ushort best = (ushort)(cursor - 1);
                for (int back = 6; back >= 1; back--)
                {
                    ushort candidate = (ushort)(cursor - back);
                    var res = Z80Disassembler.Disassemble(_machine.Mem, candidate);
                    if (res.Length == back) { best = candidate; break; }
                }
                cursor = best;
            }
        }
        _viewBase = cursor;
        RegenerateDisasm();
    }

    private void DoGoto()
    {
        if (TryParseAddr(_gotoAddr.Text, out ushort addr))
        {
            _viewBase = addr;
            if (_chkFollow.Checked) _chkFollow.Checked = false;
            RegenerateDisasm();
            _gotoAddr.Clear();
        }
        else
        {
            _statusLabel.Text = "Invalid address — enter a hex value 0–FFFF.";
        }
    }

    private void ToggleBreakpointAtSelected()
    {
        int idx = _disasmList.SelectedIndex;
        if (idx < 0 || idx >= _lines.Count) return;
        ushort a = _lines[idx].Addr;
        _machine.Cpu.Breakpoints[a] = !_machine.Cpu.Breakpoints[a];
        _bpVersion++;
        UpdateBreakpointList();
        _disasmList.Invalidate();
    }

    private void EnsurePCInView()
    {
        if (!_chkFollow.Checked) return;
        ushort pc = _machine.Cpu.PC;
        if (_lines.Any(l => l.Addr == pc)) return;
        _viewBase = pc;
        RegenerateDisasm();
    }

    // --- breakpoints ----------------------------------------------------

    private void AddBreakpoint()
    {
        if (TryParseAddr(_bpAddr.Text, out ushort addr))
        {
            _machine.Cpu.Breakpoints[addr] = true;
            _bpVersion++;
            _bpAddr.Clear();
            UpdateBreakpointList();
            _disasmList.Invalidate();
        }
        else
        {
            _statusLabel.Text = "Invalid address — enter a hex value 0–FFFF.";
        }
    }

    private void RemoveSelectedBreakpoint()
    {
        if (_bpList.SelectedItem is string sel && TryParseAddr(sel, out ushort addr))
        {
            _machine.Cpu.Breakpoints[addr] = false;
            _bpVersion++;
            UpdateBreakpointList();
            _disasmList.Invalidate();
        }
    }

    private static bool TryParseAddr(string s, out ushort addr)
    {
        addr = 0;
        s = s.Trim();
        if (s.StartsWith("$", StringComparison.Ordinal)) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        if (v < 0 || v > 0xFFFF) return false;
        addr = (ushort)v;
        return true;
    }

    // --- refresh --------------------------------------------------------

    /// <summary>Called once per frame by MainForm's Timer_Tick.</summary>
    public void RefreshIfVisible()
    {
        if (!Visible) return;
        UpdateButtons();
        UpdateRegisters();
        UpdateStatus();
        // Follow PC only while paused: a breakpoint trip or step that lands
        // outside the visible window re-anchors the view. While running, PC
        // moves too fast for a tracking view to be readable — leave it put.
        if (_machine.Paused) EnsurePCInView();
        InvalidateDisasmIfChanged();
    }

    /// <summary>
    /// Repaint the disassembly list only when something it draws actually
    /// changed — PC, paused state, or breakpoint set. Repainting an
    /// owner-drawn ListBox every frame is the main flicker source.
    /// </summary>
    private void InvalidateDisasmIfChanged()
    {
        ushort pc = _machine.Cpu.PC;
        bool paused = _machine.Paused;
        if (pc == _lastDrawnPC && paused == _lastDrawnPaused && _bpVersion == _lastDrawnBpVersion)
            return;
        _lastDrawnPC = pc;
        _lastDrawnPaused = paused;
        _lastDrawnBpVersion = _bpVersion;
        _disasmList.Invalidate();
    }

    private void UpdateAll()
    {
        UpdateButtons();
        UpdateRegisters();
        UpdateStatus();
        UpdateBreakpointList();
        // Force a repaint after explicit user actions (open, step, reset, etc.)
        _lastDrawnPC = 0xFFFF;
        InvalidateDisasmIfChanged();
    }

    private void UpdateButtons()
    {
        SetTextIfChanged(_btnPause, _machine.Paused ? "Resume (F5)" : "Pause (F5)");
    }

    private void UpdateRegisters()
    {
        var c = _machine.Cpu;
        var sb = new StringBuilder();
        sb.AppendLine($"PC = {c.PC:X4}    SP = {c.SP:X4}");
        sb.AppendLine();
        sb.AppendLine($"AF = {c.AF:X4}    AF'= {c.AF_:X4}");
        sb.AppendLine($"BC = {c.BC:X4}    BC'= {c.BC_:X4}");
        sb.AppendLine($"DE = {c.DE:X4}    DE'= {c.DE_:X4}");
        sb.AppendLine($"HL = {c.HL:X4}    HL'= {c.HL_:X4}");
        sb.AppendLine($"IX = {c.IX:X4}    IY = {c.IY:X4}");
        sb.AppendLine();
        sb.AppendLine($"I = {c.I:X2}  R = {c.R:X2}   IM = {c.IM}");
        sb.AppendLine($"IFF1 = {(c.IFF1 ? 1 : 0)}  IFF2 = {(c.IFF2 ? 1 : 0)}  HALT = {(c.Halted ? 1 : 0)}");
        sb.AppendLine();
        sb.AppendLine($"Flags  {DecodeFlags(c.F)}");
        sb.AppendLine();
        sb.Append($"Cycles {c.TotalCycles:N0}");
        SetTextIfChanged(_regLabel, sb.ToString());
    }

    private static string DecodeFlags(byte f)
    {
        char Bit(byte mask, char letter) => (f & mask) != 0 ? letter : '·';
        return $"{Bit(Z80Cpu.FLAG_S, 'S')} {Bit(Z80Cpu.FLAG_Z, 'Z')} " +
               $"{Bit(Z80Cpu.FLAG_H, 'H')} {Bit(Z80Cpu.FLAG_PV, 'P')} " +
               $"{Bit(Z80Cpu.FLAG_N, 'N')} {Bit(Z80Cpu.FLAG_C, 'C')}";
    }

    private void UpdateStatus()
    {
        var c = _machine.Cpu;
        string text;
        if (_machine.Paused && c.BreakpointTripped) text = $"Stopped — breakpoint at ${c.PC:X4}";
        else if (_machine.Paused) text = $"Paused at ${c.PC:X4}";
        else text = "Running";
        SetTextIfChanged(_statusLabel, text);
    }

    private void UpdateBreakpointList()
    {
        _bpList.BeginUpdate();
        _bpList.Items.Clear();
        var bps = _machine.Cpu.Breakpoints;
        for (int a = 0; a < bps.Length; a++)
            if (bps[a]) _bpList.Items.Add($"${a:X4}");
        _bpList.EndUpdate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Closing the window just hides it — keep breakpoints and the
        // instance alive so reopening is instant. MainForm disposes it
        // for real when the emulator shuts down.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // --- helpers --------------------------------------------------------

    // Setting Text on a Label/Button is the simplest cause of WinForms
    // flicker — it forces a full redraw even if the new value equals the
    // old. Skip the assignment when nothing changed.
    private static void SetTextIfChanged(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }
}
