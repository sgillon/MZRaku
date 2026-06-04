using System.Drawing;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Diagnostic view of the loaded MZ-700 character ROM — all 512 glyphs
/// (two banks × 256 codes) rendered via <see cref="VideoRenderer.GetGlyph"/>
/// in display-code order. Useful for confirming the font ROM loaded
/// correctly and for picking the display code of a specific glyph
/// (position implies code: row*16 + col, with bank 0 in the top half
/// and bank 1 in the bottom half).
/// </summary>
public sealed class FontSheetForm : Form
{
    private const int CellsPerRow = 16;
    private const int RowsPerBank = 16;
    private const int GlyphScale = 3;
    private const int CellPx = 8 * GlyphScale;
    private const int MarginPx = 24;
    private const int BankGapPx = 16;

    private readonly VideoRenderer _video;
    private readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.AutoSize };

    public FontSheetForm(VideoRenderer video)
    {
        _video = video;
        Text = "Font Sheet";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;
        ShowInTaskbar = false;

        var reload = new Button { Text = "Reload", AutoSize = true, Dock = DockStyle.Top };
        reload.Click += (_, _) => { _video.InvalidateGlyphCache(); RedrawSheet(); };

        Controls.Add(_pic);
        Controls.Add(reload);
        ClientSize = new Size(
            MarginPx + CellsPerRow * CellPx + 16,
            MarginPx * 2 + RowsPerBank * 2 * CellPx + BankGapPx + reload.PreferredSize.Height + 16);

        RedrawSheet();
    }

    private void RedrawSheet()
    {
        int width = MarginPx + CellsPerRow * CellPx;
        int height = MarginPx + RowsPerBank * CellPx + BankGapPx + MarginPx + RowsPerBank * CellPx;
        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        using (var hdrFont = new Font(FontFamily.GenericMonospace, 7f))
        using (var hdrBrush = new SolidBrush(Color.DimGray))
        {
            g.Clear(Color.White);

            DrawBank(g, bank: 0, yOffset: MarginPx, hdrFont, hdrBrush);
            DrawBank(g, bank: 1, yOffset: MarginPx + RowsPerBank * CellPx + BankGapPx + MarginPx, hdrFont, hdrBrush);
        }
        var old = _pic.Image;
        _pic.Image = bmp;
        old?.Dispose();
    }

    private void DrawBank(Graphics g, int bank, int yOffset, Font hdrFont, Brush hdrBrush)
    {
        // Column header strip directly above the bank.
        for (int c = 0; c < CellsPerRow; c++)
        {
            g.DrawString($"{c:X}", hdrFont, hdrBrush, MarginPx + c * CellPx + 2, yOffset - 14);
        }
        // Bank label in the top-left corner of the margin.
        g.DrawString($"Bank {bank}", hdrFont, hdrBrush, 2, yOffset - 14);

        for (int r = 0; r < RowsPerBank; r++)
        {
            int cellY = yOffset + r * CellPx;
            // Row header: high nibble of the display code.
            g.DrawString($"{r:X}_", hdrFont, hdrBrush, 2, cellY + CellPx / 2 - 6);
            for (int c = 0; c < CellsPerRow; c++)
            {
                byte code = (byte)(r * CellsPerRow + c);
                var glyph = _video.GetGlyph(code, bank, GlyphScale);
                g.DrawImageUnscaled(glyph, MarginPx + c * CellPx, cellY);
            }
        }
    }
}
