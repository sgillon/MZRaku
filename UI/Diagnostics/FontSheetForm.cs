using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Diagnostic view of the loaded character ROM — all glyphs rendered
/// in display-code order, one bank per section.
///
/// MZ-700: two 16 × 16 sections, one per font bank (bank 0 = ALPHA,
/// bank 1 = GRAPH). Cells that the keyboard can produce in that bank
/// per <see cref="RomKeyTables"/> are outlined green, and clicking one
/// enqueues the (row, col, shift) into the auto-typer — the point being
/// to reach MZ-only glyphs (graphics blocks, kana) that have no PC-key
/// equivalent. Bank 1 clicks are parked pending the attribute-byte fix
/// tracked in [[graph-clicktotype-parked]].
///
/// MZ-80A: two 16 × 8 sections, Text (codes $00–$7F) and Graphics
/// ($80–$FF). Same 40 × 25 renderer feeds both, single-bank font ROM
/// (no MZ-700 attribute bit-7 select). View-only for this phase — the
/// display-code → key-slot table needed for click-to-type doesn't
/// exist for MZ-80A yet, and lives with the same graphic-glyph work
/// that will eventually unpark MZ-700 bank 1.
/// </summary>
public sealed class FontSheetForm : Form
{
    private const int CellsPerRow = 16;
    private const int GlyphScale = 3;
    private const int CellPx = 8 * GlyphScale;
    private const int MarginPx = 24;
    private const int BankGapPx = 16;

    private readonly MZ700? _mz700;
    private readonly MZ80A? _mz80a;

    // For MZ-700 the section index doubles as the font-bank index
    // (0 = ALPHA, 1 = GRAPH). For MZ-80A the font is single-bank; the
    // section merely partitions the display-code range for display.
    private sealed record Section(string Label, byte FirstCode, int RowCount);
    private readonly List<Section> _sections;

    private readonly PictureBox _pic = new() { SizeMode = PictureBoxSizeMode.AutoSize };
    private readonly Label _headerLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Top,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 4, 8, 4),
        Height = 26,
        ForeColor = SystemColors.GrayText,
    };
    private readonly Label _statusLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Bottom,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
        Height = 22,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
        ForeColor = SystemColors.GrayText,
    };

    public FontSheetForm(IMachine machine)
    {
        _mz700 = machine as MZ700;
        _mz80a = machine as MZ80A;

        if (_mz700 != null)
        {
            _sections = new List<Section>
            {
                new("Bank 0", 0x00, 16),
                new("Bank 1", 0x00, 16),
            };
            _headerLabel.Text = "Click a glyph to type it into the emulator. Green-outlined cells are reachable from the keyboard.";
        }
        else
        {
            _sections = new List<Section>
            {
                new("Text", 0x00, 8),
                new("Graphics", 0x80, 8),
            };
            _headerLabel.Text = "View-only for MZ-80A this phase — click-to-type coming with the graphics work.";
        }

        Text = "Font Sheet";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;
        ShowInTaskbar = false;

        _pic.MouseClick += OnCellClick;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(_pic);

        // Docking order matters: Fill panel added first, then Top/Bottom
        // bars — otherwise the Fill claims the whole client area.
        Controls.Add(scroll);
        Controls.Add(_statusLabel);
        Controls.Add(_headerLabel);

        int totalRows = 0;
        foreach (var s in _sections) totalRows += s.RowCount;
        int gapCount = _sections.Count;
        ClientSize = new Size(
            MarginPx + CellsPerRow * CellPx + 24,
            MarginPx * gapCount + totalRows * CellPx + BankGapPx * (gapCount - 1) + _headerLabel.Height + _statusLabel.Height + 16);

        RedrawSheet();
    }

    private int SectionY(int index)
    {
        int y = MarginPx;
        for (int i = 0; i < index; i++)
            y += _sections[i].RowCount * CellPx + BankGapPx + MarginPx;
        return y;
    }

    private void RedrawSheet()
    {
        int width = MarginPx + CellsPerRow * CellPx;
        int lastIdx = _sections.Count - 1;
        int height = SectionY(lastIdx) + _sections[lastIdx].RowCount * CellPx;
        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        using (var hdrFont = new Font(FontFamily.GenericMonospace, 7f))
        using (var hdrBrush = new SolidBrush(Color.DimGray))
        {
            g.Clear(Color.White);
            for (int i = 0; i < _sections.Count; i++)
                DrawSection(g, i, hdrFont, hdrBrush);
        }
        var old = _pic.Image;
        _pic.Image = bmp;
        old?.Dispose();
    }

    private void DrawSection(Graphics g, int sectionIndex, Font hdrFont, Brush hdrBrush)
    {
        var section = _sections[sectionIndex];
        int yOffset = SectionY(sectionIndex);

        for (int c = 0; c < CellsPerRow; c++)
            g.DrawString($"{c:X}", hdrFont, hdrBrush, MarginPx + c * CellPx + 2, yOffset - 14);
        g.DrawString(section.Label, hdrFont, hdrBrush, 2, yOffset - 14);

        using var reachablePen = new Pen(Color.MediumSeaGreen, 1f);
        for (int r = 0; r < section.RowCount; r++)
        {
            int cellY = yOffset + r * CellPx;
            byte rowHigh = (byte)(section.FirstCode + r * CellsPerRow);
            g.DrawString($"{rowHigh >> 4:X}{r & 0xF:X}_", hdrFont, hdrBrush, 2, cellY + CellPx / 2 - 6);
            for (int c = 0; c < CellsPerRow; c++)
            {
                byte code = (byte)(section.FirstCode + r * CellsPerRow + c);
                int cellX = MarginPx + c * CellPx;
                var glyph = _mz700 != null
                    ? _mz700.Video.GetGlyph(code, sectionIndex, GlyphScale)
                    : _mz80a!.Video.GetGlyph(code, GlyphScale);
                g.DrawImageUnscaled(glyph, cellX, cellY);

                // MZ-700: outline cells the keyboard can produce in this
                // bank so the user can pick something typeable without
                // guesswork. MZ-80A has no equivalent reverse map yet, so
                // no outlines — clicks are view-only for this phase.
                if (_mz700 != null &&
                    _mz700.KeyTables.FindByDisplayCode(code, sectionIndex) != null)
                    g.DrawRectangle(reachablePen, cellX, cellY, CellPx - 1, CellPx - 1);
            }
        }
    }

    private void OnCellClick(object? sender, MouseEventArgs e)
    {
        if (!TryHitTest(e.X, e.Y, out byte code, out int sectionIndex))
        {
            _statusLabel.Text = "Click a glyph cell to type it.";
            return;
        }

        if (_mz80a != null)
        {
            string half = _sections[sectionIndex].Label;
            _statusLabel.Text = $"{half} code ${code:X2}";
            return;
        }

        int bank = sectionIndex;
        var slot = _mz700!.KeyTables.FindByDisplayCode(code, bank);
        if (slot is null)
        {
            _statusLabel.Text = $"Bank {bank} code ${code:X2} isn't reachable from the keyboard.";
            return;
        }

        // Mode check: clicking an ALPHA cell while the machine is in
        // GRAPH mode (or vice versa) would press a slot whose lookup is
        // mode-dependent and produce the wrong glyph. Refuse rather than
        // silently mistype.
        bool inGraphMode = (_mz700.Mem.Read(0x0060) & 0x10) != 0;
        bool wantGraphMode = bank == RomKeyTables.GraphBank;
        if (inGraphMode != wantGraphMode)
        {
            string need = wantGraphMode ? "GRAPH" : "ALPHA";
            _statusLabel.Text =
                $"Switch the machine to {need} mode first (press the {need} key) to type this glyph.";
            return;
        }

        // GRAPH-bank click-to-type is parked — see docs/usage/keyboard.md.
        // The matrix press lands the right byte in VRAM but BASIC's input
        // handler doesn't set the attribute byte to bank 1 via this path,
        // so the byte renders as the bank-0 glyph at that code instead of
        // the intended graphic. Investigation 2026-06-07 narrowed it to
        // the auto-typer-vs-PC-keystroke difference without finding the
        // missing state. Outline stays so the reachable set is visible,
        // but clicking is suppressed until the attribute write is solved.
        if (bank == RomKeyTables.GraphBank)
        {
            _statusLabel.Text =
                "Bank 1 click-to-type is a known limitation — the glyph lands "
                + "but with the wrong attribute. See docs/usage/keyboard.md.";
            return;
        }

        _mz700.Keyboard.TypePress(new CharMap.Press(slot.Value.Row, slot.Value.Col, slot.Value.MzShift));
        string shiftTxt = slot.Value.MzShift ? "shift+" : "";
        _statusLabel.Text =
            $"Typed bank {bank} code ${code:X2} via {shiftTxt}({slot.Value.Row},{slot.Value.Col}).";
    }

    private bool TryHitTest(int x, int y, out byte code, out int sectionIndex)
    {
        code = 0;
        sectionIndex = 0;
        int cx = x - MarginPx;
        if (cx < 0 || cx >= CellsPerRow * CellPx) return false;
        int col = cx / CellPx;

        for (int i = 0; i < _sections.Count; i++)
        {
            int top = SectionY(i);
            int bottom = top + _sections[i].RowCount * CellPx;
            if (y >= top && y < bottom)
            {
                sectionIndex = i;
                int row = (y - top) / CellPx;
                code = (byte)(_sections[i].FirstCode + row * CellsPerRow + col);
                return true;
            }
        }
        return false;
    }
}
