using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Owner-drawn read-only view of the MZ-700 10×8 keyboard matrix. Each
/// cell renders the matrix coordinate, the MZ glyph(s) the slot
/// produces (or a friendly label for non-printable slots like Enter /
/// GRAPH / cursors), and the PC keystroke(s) currently mapped to it via
/// <see cref="CharMap"/> + <see cref="CharMapOverrides"/> +
/// <see cref="SpecialKeyMap"/> + <see cref="KeyOverride"/>. The slot
/// last asserted by the keyboard layer is highlighted yellow; slots
/// carrying an active override are framed in orange.
///
/// Phase A surface — read-only. The cell-click → editor wiring lands
/// in Phase A8.
/// </summary>
public sealed class KeyboardMatrixGrid : UserControl
{
    private const int Rows = 10;
    private const int Cols = 8;
    private const int CellW = 80;
    private const int CellH = 64;
    private const int LeftMargin = 28;
    private const int TopMargin = 22;
    private const int Pad = 8;

    private readonly MZ700 _machine;
    private readonly Font _coordFont;
    private readonly Font _bindFont;
    private readonly Font _glyphFont;
    private readonly Font _specialFont;

    // Per-slot derived strings, rebuilt by RefreshBindings(). _bindText
    // is the joined list of PC chars / VK labels that currently route to
    // the slot; _slotHasOverride flags slots touched by either override
    // layer so the cell border can highlight them.
    private readonly string[,] _bindText = new string[Rows, Cols];
    private readonly bool[,] _slotHasOverride = new bool[Rows, Cols];

    // Coverage tracking — rising-edge sampled on each paint. _wasPressed
    // is the prior frame's state; _everPressed accumulates any slot that
    // has been observed transitioning unpressed → pressed since this
    // form instance was created (or ResetCoverage was clicked). The
    // checkbox in the host form gates only the visual indicator, so the
    // user can toggle the chyrons on/off without losing accumulated
    // state.
    private readonly bool[,] _wasPressed = new bool[Rows, Cols];
    private readonly bool[,] _everPressed = new bool[Rows, Cols];

    /// <summary>Show the green corner chyron on slots that have been
    /// pressed since this grid was created (or ResetCoverage was
    /// called). Off by default; tracking happens regardless.</summary>
    public bool ShowCoverage { get; set; }

    /// <summary>
    /// Raised when the user clicks a matrix cell. Subscribed by editing
    /// hosts (Settings → Keyboard tab) to open the binding editor;
    /// read-only hosts (Debug → Keyboard Matrix…) simply don't subscribe.
    /// The cursor changes to a hand only while a subscriber is attached.
    /// </summary>
    public event EventHandler<CellClickedEventArgs>? CellClicked;

    public KeyboardMatrixGrid(MZ700 machine)
    {
        _machine = machine;
        DoubleBuffered = true;
        BackColor = Color.White;
        _coordFont   = new Font(FontFamily.GenericMonospace, 7f);
        _bindFont    = new Font(FontFamily.GenericSansSerif, 8f);
        _glyphFont   = new Font(FontFamily.GenericMonospace, 14f, FontStyle.Bold);
        _specialFont = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);

        Size = new Size(
            Pad * 2 + LeftMargin + Cols * CellW,
            Pad * 2 + TopMargin  + Rows * CellH);

        RefreshBindings();
    }

    /// <summary>
    /// Recompute per-slot binding text from the four sources (CharMap
    /// defaults + overrides, SpecialKeyMap + KeyOverride). Call after
    /// settings are reloaded.
    /// </summary>
    public void RefreshBindings()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                _bindText[r, c] = "";
                _slotHasOverride[r, c] = false;
            }

        foreach (var kv in CharMap.Defaults)
            AppendBind(kv.Value.Row, kv.Value.Col, kv.Key.ToString());

        var co = CharMap.Overrides;
        if (co != null)
            foreach (var kv in co.All)
            {
                _slotHasOverride[kv.Value.Row, kv.Value.Col] = true;
                AppendBind(kv.Value.Row, kv.Value.Col, kv.Key.ToString());
            }

        foreach (var kv in SpecialKeyMap.Map)
        {
            var rc = kv.Value;
            var label = SpecialKeyMap.Labels.TryGetValue(kv.Key, out var lbl)
                ? lbl : kv.Key.ToString();
            AppendBind(rc.row, rc.col, label);
        }

        var ko = _machine.Keyboard.Overrides;
        if (ko != null)
            foreach (var kv in ko.All)
            {
                _slotHasOverride[kv.Value.Row, kv.Value.Col] = true;
                var label = SpecialKeyMap.Labels.TryGetValue(kv.Key, out var lbl)
                    ? lbl : kv.Key.ToString();
                AppendBind(kv.Value.Row, kv.Value.Col, label);
            }

        Invalidate();
    }

    private void AppendBind(int r, int c, string s)
    {
        if (r < 0 || r >= Rows || c < 0 || c >= Cols) return;
        var cur = _bindText[r, c];
        if (string.IsNullOrEmpty(cur)) { _bindText[r, c] = s; return; }
        // Avoid duplicates ('A' and 'A' from defaults+override).
        var parts = cur.Split(' ');
        if (!parts.Contains(s)) _bindText[r, c] = cur + " " + s;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        // Column headers
        for (int c = 0; c < Cols; c++)
        {
            int x = Pad + LeftMargin + c * CellW;
            var rect = new Rectangle(x, Pad, CellW, TopMargin);
            TextRenderer.DrawText(g, c.ToString(), _coordFont, rect, Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        // Row headers
        for (int r = 0; r < Rows; r++)
        {
            int y = Pad + TopMargin + r * CellH;
            var rect = new Rectangle(Pad, y, LeftMargin, CellH);
            TextRenderer.DrawText(g, r.ToString(), _coordFont, rect, Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Cells. Highlight reads live matrix bits (active-low: a 0 bit
        // means the slot is currently held) so the yellow tint tracks
        // key-down / key-up rather than persisting until the next press.
        // Rising-edge detection feeds the coverage chyron.
        for (int r = 0; r < Rows; r++)
        {
            byte rowBits = _machine.Keyboard.PeekMatrixRow(r);
            for (int c = 0; c < Cols; c++)
            {
                bool pressed = (rowBits & (1 << c)) == 0;
                if (pressed && !_wasPressed[r, c]) _everPressed[r, c] = true;
                _wasPressed[r, c] = pressed;
                DrawCell(g, r, c, pressed);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Hand cursor only over real cells, and only when somebody's
        // listening (so the standalone diagnostic window stays read-only).
        if (CellClicked != null && HitTest(e.X, e.Y).row >= 0)
            Cursor = Cursors.Hand;
        else
            Cursor = Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (CellClicked == null) return;
        if (e.Button != MouseButtons.Left) return;
        var (r, c) = HitTest(e.X, e.Y);
        if (r < 0) return;
        bool mzShift = DetermineShiftedHalf(e.X, r, c);
        CellClicked.Invoke(this, new CellClickedEventArgs(r, c, mzShift));
    }

    private static (int row, int col) HitTest(int x, int y)
    {
        int gx = x - Pad - LeftMargin;
        int gy = y - Pad - TopMargin;
        if (gx < 0 || gy < 0) return (-1, -1);
        int c = gx / CellW;
        int r = gy / CellH;
        if (r < 0 || r >= Rows || c < 0 || c >= Cols) return (-1, -1);
        return (r, c);
    }

    // Slots with both unshifted and shifted printable glyphs render with
    // the unshifted glyph in the left half and the shifted one in the
    // right half. A click on the right half is interpreted as "the user
    // wants the shifted side". For shift-only slots, always shifted; for
    // unshifted-only slots (and slots with no printable glyph at all),
    // always unshifted.
    private static bool DetermineShiftedHalf(int x, int r, int c)
    {
        var un = MzGlyphCatalog.FindByPrintableSlot(r, c, false);
        var sh = MzGlyphCatalog.FindByPrintableSlot(r, c, true);
        if (un.HasValue && sh.HasValue)
        {
            int cellX = Pad + LeftMargin + c * CellW;
            return x >= cellX + CellW / 2;
        }
        return sh.HasValue;
    }

    /// <summary>Clear accumulated coverage state (the green chyrons).
    /// Does not touch <see cref="_wasPressed"/>, so keys currently held
    /// won't re-trigger until they're released and pressed again.</summary>
    public void ResetCoverage()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _everPressed[r, c] = false;
        Invalidate();
    }

    private void DrawCell(Graphics g, int r, int c, bool isPressed)
    {
        int x = Pad + LeftMargin + c * CellW;
        int y = Pad + TopMargin + r * CellH;
        var rect = new Rectangle(x, y, CellW, CellH);

        // Background — held slot gets a yellow tint so the user can
        // watch their PC keystrokes land on the matrix.
        Color bg = isPressed ? Color.LemonChiffon : Color.White;
        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, rect);

        // Border — orange + thicker if any override layer touches this slot.
        bool overridden = _slotHasOverride[r, c];
        var borderColor = overridden ? Color.DarkOrange : Color.LightGray;
        int borderW = overridden ? 2 : 1;
        using (var pen = new Pen(borderColor, borderW))
            g.DrawRectangle(pen, x, y, CellW - 1, CellH - 1);

        // Coord, top-left.
        TextRenderer.DrawText(g, $"{r},{c}", _coordFont,
            new Rectangle(x + 4, y + 2, CellW - 8, 12), Color.Gray,
            TextFormatFlags.Left | TextFormatFlags.Top);

        // Glyph or special label, centre band.
        var specialLabel = MzGlyphCatalog.FindSpecialLabel(r, c);
        if (specialLabel != null)
        {
            TextRenderer.DrawText(g, specialLabel, _specialFont,
                new Rectangle(x + 4, y + 14, CellW - 8, 30), Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            DrawSlotGlyphs(g, r, c, x, y);
        }

        // PC binding text, bottom band — EndEllipsis if it doesn't fit
        // rather than wrap, so cell heights stay uniform.
        var binds = _bindText[r, c];
        if (!string.IsNullOrEmpty(binds))
        {
            TextRenderer.DrawText(g, binds, _bindFont,
                new Rectangle(x + 2, y + CellH - 18, CellW - 4, 16),
                Color.MidnightBlue,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // Coverage chyron — small green triangle in the top-right corner
        // of any slot that's been pressed at least once this session.
        // Stacks cleanly inside the override border without fighting it.
        if (ShowCoverage && _everPressed[r, c])
        {
            const int chyronSize = 12;
            int tx = x + CellW - 2;
            int ty = y + 2;
            Point[] tri =
            {
                new Point(tx - chyronSize, ty),
                new Point(tx, ty),
                new Point(tx, ty + chyronSize),
            };
            using var chyronBrush = new SolidBrush(Color.MediumSeaGreen);
            g.FillPolygon(chyronBrush, tri);
        }
    }

    private void DrawSlotGlyphs(Graphics g, int r, int c, int x, int y)
    {
        // Unicode rendering via MzGlyphCatalog — keeps the grid
        // independent of the font ROM. Authentic-MZ rendering via
        // Video.GetGlyph + a slot→display-code lookup could replace this
        // later if visual fidelity matters more than simplicity.
        char? un = MzGlyphCatalog.FindByPrintableSlot(r, c, false);
        char? sh = MzGlyphCatalog.FindByPrintableSlot(r, c, true);
        var unRect = new Rectangle(x + 4, y + 12, CellW / 2 - 4, 30);
        var shRect = new Rectangle(x + CellW / 2, y + 12, CellW / 2 - 4, 30);
        var fullRect = new Rectangle(x + 4, y + 12, CellW - 8, 30);
        const TextFormatFlags flags =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        if (un.HasValue && sh.HasValue)
        {
            TextRenderer.DrawText(g, un.Value.ToString(), _glyphFont, unRect, Color.Black, flags);
            TextRenderer.DrawText(g, sh.Value.ToString(), _glyphFont, shRect, Color.DimGray, flags);
        }
        else if (un.HasValue)
        {
            TextRenderer.DrawText(g, un.Value.ToString(), _glyphFont, fullRect, Color.Black, flags);
        }
        else if (sh.HasValue)
        {
            TextRenderer.DrawText(g, sh.Value.ToString(), _glyphFont, fullRect, Color.DimGray, flags);
        }
        else
        {
            // No printable glyph and no special label = unmapped slot.
            TextRenderer.DrawText(g, "·", _coordFont, fullRect, Color.LightGray, flags);
        }
    }
}

public sealed class CellClickedEventArgs : EventArgs
{
    public int Row { get; }
    public int Col { get; }
    /// <summary>True if the user clicked the shifted half of a cell that
    /// has both unshifted and shifted glyphs, or a shift-only slot. The
    /// binding editor uses this as the default for its MzShift checkbox.</summary>
    public bool MzShift { get; }

    public CellClickedEventArgs(int row, int col, bool mzShift)
    {
        Row = row;
        Col = col;
        MzShift = mzShift;
    }
}
