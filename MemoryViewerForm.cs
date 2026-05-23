using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MZ700Emul;

/// <summary>
/// Hex / ASCII memory viewer — a companion to the debugger window.
/// Shows the full 64K address space at 16 bytes per row, refreshes once
/// per frame so values update live while the emulator runs, and lets
/// you Goto $XXXX to jump anywhere.
///
/// PC and SP are highlighted in their respective rows so you can see at
/// a glance where the CPU is and what it's pointing to. The
/// $E000-$E00F PPI/PIT I/O window is shown as <c>--</c> rather than
/// read through — reading those bytes has hardware side effects (PIT
/// counter latch, keyboard scan).
///
/// Like the debugger, the window runs on the WinForms UI thread and
/// "hides on close" so reopening is instant and any goto position is
/// preserved across closes.
/// </summary>
public sealed class MemoryViewerForm : Form
{
    private readonly MZ700 _machine;

    private readonly SmoothListBox _list = new();
    private readonly TextBox _gotoAddr = new();
    private readonly Button _btnGoto = new();
    private readonly TextBox _dumpStart = new();
    private readonly TextBox _dumpEnd = new();
    private readonly Button _btnDump = new();
    private readonly SmoothLabel _statusLabel = new();

    private readonly Font _mono;

    private const int BytesPerRow = 16;
    private const int RowCount = 0x10000 / BytesPerRow;     // 4096 rows

    // Cached per-row scratch buffer used by OnDrawRow — avoids allocating a
    // fresh StringBuilder for every visible row on every refresh.
    private readonly StringBuilder _rowBuf = new(80);
    private readonly byte[] _rowBytes = new byte[BytesPerRow];

    public MemoryViewerForm(MZ700 machine)
    {
        _machine = machine;

        Text = "Memory Viewer";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ClientSize = new Size(600, 520);
        MinimumSize = new Size(560, 240);
        KeyPreview = true;
        DoubleBuffered = true;
        ShowInTaskbar = false;

        _mono = new Font(FontFamily.GenericMonospace, 9f);

        var root = new SmoothTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));

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
        _btnGoto.Text = "Go";
        _btnGoto.Width = 40;
        _btnGoto.Height = 28;
        _btnGoto.TabStop = false;
        _btnGoto.Click += (_, _) => DoGoto();
        var btnPC = new Button { Text = "PC", Width = 40, Height = 28, TabStop = false };
        btnPC.Click += (_, _) => ScrollTo(_machine.Cpu.PC);
        var btnSP = new Button { Text = "SP", Width = 40, Height = 28, TabStop = false };
        btnSP.Click += (_, _) => ScrollTo(_machine.Cpu.SP);

        // Range-dump controls. Defaults to BASIC's working area
        // ($6A00–$7800) which is the focus of Phase 3a snapshot work.
        var sep = new Label { Text = "  Dump $", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        _dumpStart.Width = 50;
        _dumpStart.Font = _mono;
        _dumpStart.MaxLength = 5;
        _dumpStart.Margin = new Padding(2, 4, 2, 0);
        _dumpStart.Text = "6A00";
        var dash = new Label { Text = "–$", AutoSize = true, Margin = new Padding(0, 7, 0, 0) };
        _dumpEnd.Width = 50;
        _dumpEnd.Font = _mono;
        _dumpEnd.MaxLength = 5;
        _dumpEnd.Margin = new Padding(2, 4, 4, 0);
        _dumpEnd.Text = "7800";
        _btnDump.Text = "Dump…";
        _btnDump.Width = 60;
        _btnDump.Height = 28;
        _btnDump.TabStop = false;
        _btnDump.Click += (_, _) => DoDump();

        toolbar.Controls.AddRange(new Control[]
        {
            lbl, _gotoAddr, _btnGoto, btnPC, btnSP,
            sep, _dumpStart, dash, _dumpEnd, _btnDump,
        });
        root.Controls.Add(toolbar, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.Font = _mono;
        _list.IntegralHeight = false;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = _mono.Height + 2;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.DrawItem += OnDrawRow;
        _list.SelectedIndexChanged += (_, _) => UpdateStatus();
        for (int i = 0; i < RowCount; i++) _list.Items.Add(i);
        root.Controls.Add(_list, 0, 1);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = false;
        _statusLabel.BorderStyle = BorderStyle.Fixed3D;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(4, 0, 0, 0);
        root.Controls.Add(_statusLabel, 0, 2);

        Controls.Add(root);

        FormClosing += OnFormClosing;
        Shown += (_, _) => ScrollTo(_machine.Cpu.PC);
        UpdateStatus();
    }

    /// <summary>Called once per frame by MainForm's Timer_Tick.</summary>
    public void RefreshIfVisible()
    {
        if (!Visible) return;
        // Memory values change continuously while the CPU runs; the cheap
        // thing to do is just repaint the visible rows. OwnerDraw reads
        // current memory in the callback so nothing else needs updating.
        _list.Invalidate();
        UpdateStatus();
    }

    private void ScrollTo(ushort addr)
    {
        int row = addr / BytesPerRow;
        _list.TopIndex = Math.Max(0, row - 2);
        _list.SelectedIndex = row;
    }

    private void DoGoto()
    {
        if (TryParseAddr(_gotoAddr.Text, out ushort addr))
        {
            ScrollTo(addr);
            _gotoAddr.Clear();
        }
        else
        {
            _statusLabel.Text = "Invalid address — enter a hex value 0–FFFF.";
        }
    }

    private void DoDump()
    {
        if (!TryParseAddr(_dumpStart.Text, out ushort start))
        {
            _statusLabel.Text = "Invalid dump start address.";
            return;
        }
        if (!TryParseAddr(_dumpEnd.Text, out ushort end))
        {
            _statusLabel.Text = "Invalid dump end address.";
            return;
        }
        if (end < start)
        {
            _statusLabel.Text = "Dump end must be ≥ start.";
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Save RAM snapshot",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"ram-{start:X4}-{end:X4}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            WriteSnapshot(dlg.FileName, start, end);
            _statusLabel.Text = $"Dumped ${start:X4}–${end:X4} to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Dump failed: {ex.Message}";
        }
    }

    private void WriteSnapshot(string path, ushort start, ushort end)
    {
        using var w = new StreamWriter(path, append: false, encoding: Encoding.UTF8);
        w.WriteLine($"; MZ700Emul RAM snapshot");
        w.WriteLine($"; range: ${start:X4}–${end:X4}  ({end - start + 1} bytes)");
        w.WriteLine($"; PC=${_machine.Cpu.PC:X4}  SP=${_machine.Cpu.SP:X4}  AF=${_machine.Cpu.AF:X4}  BC=${_machine.Cpu.BC:X4}  DE=${_machine.Cpu.DE:X4}  HL=${_machine.Cpu.HL:X4}");
        w.WriteLine($"; captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine();

        // Align the first row to a 16-byte boundary so the layout matches
        // the on-screen viewer; pad missing leading bytes with spaces.
        ushort rowStart = (ushort)(start & ~(BytesPerRow - 1));
        for (int row = rowStart; row <= end; row += BytesPerRow)
        {
            _rowBuf.Clear();
            _rowBuf.Append(((ushort)row).ToString("X4")).Append(": ");
            for (int i = 0; i < BytesPerRow; i++)
            {
                int a = row + i;
                if (a < start || a > end) _rowBuf.Append("   ");
                else if (IsIoWindow((ushort)a)) _rowBuf.Append("-- ");
                else _rowBuf.Append(_machine.Mem.Read((ushort)a).ToString("X2")).Append(' ');
                if (i == 7) _rowBuf.Append(' ');
            }
            _rowBuf.Append(' ');
            for (int i = 0; i < BytesPerRow; i++)
            {
                int a = row + i;
                if (a < start || a > end) { _rowBuf.Append(' '); continue; }
                if (IsIoWindow((ushort)a)) { _rowBuf.Append('.'); continue; }
                byte b = _machine.Mem.Read((ushort)a);
                _rowBuf.Append((b >= 0x20 && b <= 0x7E) ? (char)b : '.');
            }
            w.WriteLine(_rowBuf.ToString());
            if (row + BytesPerRow > 0xFFFF) break;
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

    private void OnDrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= RowCount) { e.DrawBackground(); return; }

        ushort rowAddr = (ushort)(e.Index * BytesPerRow);
        for (int i = 0; i < BytesPerRow; i++)
        {
            ushort a = (ushort)(rowAddr + i);
            _rowBytes[i] = ReadByteSafe(a);
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool isPCRow = (_machine.Cpu.PC >= rowAddr) && (_machine.Cpu.PC < rowAddr + BytesPerRow);
        bool isSPRow = (_machine.Cpu.SP >= rowAddr) && (_machine.Cpu.SP < rowAddr + BytesPerRow);

        Color back =
            selected ? SystemColors.Highlight :
            isPCRow  ? Color.FromArgb(255, 248, 220) :
            isSPRow  ? Color.FromArgb(225, 240, 255) :
                       SystemColors.Window;
        Color fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;

        using (var bg = new SolidBrush(back)) e.Graphics.FillRectangle(bg, e.Bounds);

        _rowBuf.Clear();
        _rowBuf.Append(rowAddr.ToString("X4")).Append(": ");
        for (int i = 0; i < BytesPerRow; i++)
        {
            ushort a = (ushort)(rowAddr + i);
            if (IsIoWindow(a)) _rowBuf.Append("-- ");
            else _rowBuf.Append(_rowBytes[i].ToString("X2")).Append(' ');
            if (i == 7) _rowBuf.Append(' ');   // gap between two 8-byte groups
        }
        _rowBuf.Append(' ');
        for (int i = 0; i < BytesPerRow; i++)
        {
            ushort a = (ushort)(rowAddr + i);
            if (IsIoWindow(a)) { _rowBuf.Append('.'); continue; }
            byte b = _rowBytes[i];
            _rowBuf.Append((b >= 0x20 && b <= 0x7E) ? (char)b : '.');
        }

        // TextRenderer (GDI) is markedly faster and less flickery than
        // Graphics.DrawString (GDI+) for owner-drawn items refreshing at
        // 60 Hz — particularly important on the dense hex/ASCII rows.
        TextRenderer.DrawText(e.Graphics, _rowBuf.ToString(), _list.Font,
            new Point(e.Bounds.Left + 4, e.Bounds.Top + 1), fore,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        // PC / SP byte markers — a small dot under the byte's first hex digit.
        if (!selected)
        {
            if (isPCRow) MarkByte(e.Graphics, e.Bounds, _machine.Cpu.PC - rowAddr, Color.OrangeRed);
            if (isSPRow) MarkByte(e.Graphics, e.Bounds, _machine.Cpu.SP - rowAddr, Color.RoyalBlue);
        }
    }

    private void MarkByte(Graphics g, Rectangle bounds, int byteIndex, Color color)
    {
        // Approximate column position of the start of byte `byteIndex` in the
        // formatted row. Char widths in monospace are fixed but the System.
        // Drawing measurement is approximate — pick a reasonable factor.
        float charW = g.MeasureString("00000000", _list.Font).Width / 8f;
        // "XXXX: " = 6 chars, then 3 chars per byte (HH + space), plus 1
        // extra char of gap after byte 7.
        int hexCol = 6 + byteIndex * 3 + (byteIndex >= 8 ? 1 : 0);
        float x = bounds.Left + 4 + hexCol * charW;
        float y = bounds.Bottom - 3;
        using var p = new Pen(color, 2f);
        g.DrawLine(p, x, y, x + 2 * charW - 2, y);
    }

    private byte ReadByteSafe(ushort addr)
    {
        if (IsIoWindow(addr)) return 0;
        return _machine.Mem.Read(addr);
    }

    private static bool IsIoWindow(ushort addr) => addr >= 0xE000 && addr <= 0xE00F;

    private void UpdateStatus()
    {
        int row = _list.SelectedIndex;
        if (row < 0)
        {
            SetTextIfChanged(_statusLabel, "");
            return;
        }
        ushort addr = (ushort)(row * BytesPerRow);
        SetTextIfChanged(_statusLabel,
            $"Row ${addr:X4}–${addr + BytesPerRow - 1:X4}    PC=${_machine.Cpu.PC:X4}  SP=${_machine.Cpu.SP:X4}");
    }

    private static void SetTextIfChanged(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Same lifecycle as DebuggerForm — hide on user close, real dispose
        // only at app shutdown.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

}
