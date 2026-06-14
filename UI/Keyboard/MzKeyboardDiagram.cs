using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Owner-drawn diagram of the MZ-700 physical keyboard, rendered from
/// <see cref="MzKeyboardLayout"/>. Scales to fit the client area while
/// preserving aspect; each key is a rounded rect coloured by
/// <see cref="MzKeyboardLayout.KeyKind"/>, with its MZ labels rendered
/// from <see cref="MzGlyphCatalog"/> on the left and an optional PC-key
/// binding label on the right (the latter populated by the host once
/// the reverse-lookup index lands in P2-3).
///
/// Cursor changes to a hand when hovering a key — but only when a
/// <see cref="KeyClicked"/> subscriber is attached, so read-only hosts
/// (like the temporary diagnostic form) stay visually inert.
/// </summary>
public sealed class MzKeyboardDiagram : UserControl
{
    /// <summary>
    /// Optional per-key PC-binding label, keyed by <see cref="MzKeyboardLayout.MzKey.Id"/>.
    /// Set by the host after building the reverse-lookup index; null /
    /// missing keys render no PC binding label.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PcKeyLabels { get; set; }

    /// <summary>
    /// Set of <see cref="MzKeyboardLayout.MzKey.Id"/> values that should
    /// render with a red warning outline — used by the P2-9 safety gate
    /// to surface keys that have no PC binding (so the user can't press
    /// them from the host keyboard). null or empty = no highlighting.
    /// </summary>
    public ISet<string>? UnreachableKeyIds { get; set; }

    /// <summary>
    /// Raised when the user clicks a key. The diagram is otherwise
    /// stateless — the host owns the edit lifecycle.
    /// </summary>
    public event EventHandler<KeyDiagramClickedEventArgs>? KeyClicked;

    private string? _hoveredId;

    public MzKeyboardDiagram()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(245, 245, 247);
        // Default size sized to roughly 1 unit ≈ 50 px (14 × 6 → 700 × 300).
        // Plus 24 px padding all round.
        Size = new Size(724, 324);
    }

    /// <summary>
    /// Trigger a redraw — call after mutating <see cref="PcKeyLabels"/>.
    /// </summary>
    public void RefreshLabels() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var (scale, ox, oy) = ComputeTransform();
        if (scale <= 0f) return;

        foreach (var k in MzKeyboardLayout.Keys)
            DrawKey(g, k, scale, ox, oy);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.X, e.Y);
        var id = hit?.Id;
        if (id != _hoveredId)
        {
            _hoveredId = id;
            Invalidate();
        }
        Cursor = (hit.HasValue && KeyClicked != null) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredId != null)
        {
            _hoveredId = null;
            Invalidate();
        }
        Cursor = Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (KeyClicked == null) return;
        var hit = HitTest(e.X, e.Y);
        if (hit.HasValue)
            KeyClicked.Invoke(this, new KeyDiagramClickedEventArgs(hit.Value));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private (float scale, float ox, float oy) ComputeTransform()
    {
        const float pad = 12f;
        float availW = ClientSize.Width - pad * 2;
        float availH = ClientSize.Height - pad * 2;
        if (availW <= 0 || availH <= 0) return (0f, pad, pad);
        float sx = availW / MzKeyboardLayout.Width;
        float sy = availH / MzKeyboardLayout.Height;
        float scale = Math.Min(sx, sy);
        float ox = pad + (availW - MzKeyboardLayout.Width * scale) / 2f;
        float oy = pad + (availH - MzKeyboardLayout.Height * scale) / 2f;
        return (scale, ox, oy);
    }

    private MzKeyboardLayout.MzKey? HitTest(int x, int y)
    {
        var (scale, ox, oy) = ComputeTransform();
        if (scale <= 0f) return null;
        float lx = (x - ox) / scale;
        float ly = (y - oy) / scale;
        foreach (var k in MzKeyboardLayout.Keys)
        {
            if (lx >= k.X && lx < k.X + k.W && ly >= k.Y && ly < k.Y + k.H)
                return k;
        }
        return null;
    }

    private void DrawKey(Graphics g, MzKeyboardLayout.MzKey k, float scale, float ox, float oy)
    {
        // Pixel rect for the keycap, inset slightly so adjacent keys
        // don't share a border.
        const float gap = 1.5f;
        var rect = new RectangleF(
            ox + k.X * scale + gap,
            oy + k.Y * scale + gap,
            k.W * scale - gap * 2,
            k.H * scale - gap * 2);

        bool hovered = _hoveredId == k.Id;
        bool unreachable = UnreachableKeyIds != null && UnreachableKeyIds.Contains(k.Id);
        var (fill, border, text) = ColorsForKind(k.Kind, hovered);
        float radius = Math.Min(rect.Width, rect.Height) * 0.12f;

        // Unreachable caps get a thick red outline so they stand out at
        // a glance against both the white character caps and the blue/
        // amber special caps — distinct from the hover state's thin
        // recolored border.
        Color borderColor = unreachable ? Color.Crimson : border;
        float borderWidth = unreachable ? 3f : (hovered ? 2f : 1f);

        using (var fillBrush = new SolidBrush(fill))
        using (var borderPen = new Pen(borderColor, borderWidth))
            DrawRoundedRect(g, rect, radius, fillBrush, borderPen);

        DrawKeyLabels(g, k, rect, text);
        DrawPcBindingLabel(g, k, rect);
    }

    private static void DrawRoundedRect(Graphics g, RectangleF r, float radius, Brush fill, Pen border)
    {
        // Clamp radius so it can't exceed half the smallest dimension.
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        if (radius < 0.5f)
        {
            g.FillRectangle(fill, r);
            g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
            return;
        }
        float d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(r.X,             r.Y,             d, d, 180, 90);
        path.AddArc(r.Right - d,     r.Y,             d, d, 270, 90);
        path.AddArc(r.Right - d,     r.Bottom - d,    d, d,   0, 90);
        path.AddArc(r.X,             r.Bottom - d,    d, d,  90, 90);
        path.CloseFigure();
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    private void DrawKeyLabels(Graphics g, MzKeyboardLayout.MzKey k, RectangleF rect, Color textColor)
    {
        // Function keys (F1-F5) — half-height, centred label, smaller
        // font, no shifted/unshifted split. Bypass the standard
        // two-band layout.
        if (k.Kind == MzKeyboardLayout.KeyKind.Function && !string.IsNullOrEmpty(k.FixedLabel))
        {
            using var fnFont = new Font(FontFamily.GenericSansSerif,
                                        FontSizeForLabel(rect.Height), FontStyle.Bold);
            TextRenderer.DrawText(g, k.FixedLabel, fnFont, Rectangle.Round(rect), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        // Reserve the right ~40 % of the keycap for the optional PC
        // binding label. Glyphs / labels render in the left ~60 %.
        float labelArea = rect.Width * 0.6f;
        var glyphRect = new RectangleF(rect.X, rect.Y, labelArea, rect.Height);

        // Uniform two-band layout for ALL keys (character and fixed-label):
        // - Upper band (~45 % of height): the shifted glyph if any, else empty.
        // - Lower band (~55 % of height): the main label / unshifted glyph.
        // This keeps the main label the same size across keys regardless of
        // whether there's a shifted-glyph partner.
        var shiftRect = new RectangleF(glyphRect.X, glyphRect.Y + 2,
                                       glyphRect.Width, glyphRect.Height * 0.45f);
        var mainRect  = new RectangleF(glyphRect.X, glyphRect.Y + glyphRect.Height * 0.45f,
                                       glyphRect.Width, glyphRect.Height * 0.55f - 2);

        if (!string.IsNullOrEmpty(k.FixedLabel))
        {
            // Fixed-label keys (CR, GRAPH, ALPHA, BREAK, CTRL, SHIFT,
            // INST/DEL, cursors) have no shifted/unshifted split. Centre
            // on the full key — both axes — and shrink the font if the
            // label is too wide for the cap (GRAPH on the 1-unit cap is
            // the tightest case). Font size capped to the unshifted-
            // glyph sizing on the dual-glyph keys so single-char labels
            // stay visually consistent with the 1-9 row.
            float maxFontSize = FontSizeForLabel(rect.Height * 0.55f - 2f);
            DrawFittingText(g, k.FixedLabel, rect, maxFontSize,
                            textColor, FontStyle.Bold);
            return;
        }

        // Resolve unshifted / shifted labels: explicit per-side overrides
        // first, then the CharMap-derived glyph catalog, then the
        // SpecialKeyMap fallback.
        string? mainText  = k.UnshiftedLabel;
        string? shiftText = k.ShiftedLabel;

        if (mainText == null && shiftText == null && k.Row.HasValue && k.Col.HasValue)
        {
            int row = k.Row.Value;
            int col = k.Col.Value;
            char? un = MzGlyphCatalog.FindByPrintableSlot(row, col, false);
            char? sh = MzGlyphCatalog.FindByPrintableSlot(row, col, true);
            if (un.HasValue)  mainText  = un.Value.ToString();
            if (sh.HasValue)  shiftText = sh.Value.ToString();

            if (mainText == null && shiftText == null)
            {
                // Fall back to a SpecialKeyMap label if the slot has one.
                var special = MzGlyphCatalog.FindSpecialLabel(row, col);
                if (special != null)
                    DrawCentredText(g, special, mainRect, FontSizeForLabel(mainRect.Height),
                                    textColor, FontStyle.Regular);
                return;
            }
        }

        // Shifted glyph rendered slightly lighter than the main label so
        // it reads as the secondary printing on the cap.
        var dimText = DimColor(textColor);

        if (!string.IsNullOrEmpty(shiftText))
            DrawCentredText(g, shiftText!, shiftRect, FontSizeForLabel(shiftRect.Height),
                            dimText, FontStyle.Regular);

        if (!string.IsNullOrEmpty(mainText))
            DrawCentredText(g, mainText!, mainRect, FontSizeForLabel(mainRect.Height),
                            textColor, FontStyle.Bold);
        else if (!string.IsNullOrEmpty(shiftText))
        {
            // Shift-only slot — main label is the shifted glyph (dim).
            DrawCentredText(g, shiftText!, mainRect, FontSizeForLabel(mainRect.Height),
                            dimText, FontStyle.Bold);
        }
    }

    /// <summary>
    /// Returns a slightly muted variant of the given colour, used to
    /// render the shifted glyph on dual-glyph keys so it reads as a
    /// secondary marking. Works correctly for both black-on-white
    /// (returns mid-grey) and white-on-amber/blue (returns near-white)
    /// cases.
    /// </summary>
    private static Color DimColor(Color c)
    {
        // Pull towards mid-grey by 40 %.
        int r = (int)(c.R * 0.6f + 128 * 0.4f);
        int gC = (int)(c.G * 0.6f + 128 * 0.4f);
        int b = (int)(c.B * 0.6f + 128 * 0.4f);
        return Color.FromArgb(c.A, r, gC, b);
    }

    private void DrawPcBindingLabel(Graphics g, MzKeyboardLayout.MzKey k, RectangleF rect)
    {
        if (PcKeyLabels == null) return;
        if (!PcKeyLabels.TryGetValue(k.Id, out var label) || string.IsNullOrEmpty(label)) return;

        // Skip when the PC label would simply duplicate the cap's fixed
        // label — e.g. PC "F1" badge on MZ "F1" key, redundant.
        if (!string.IsNullOrEmpty(k.FixedLabel) && label == k.FixedLabel) return;

        // White-on-blue pill at top-right of the cap. Short-term styling
        // per user request — makes the PC binding stand out clearly
        // against the cap glyph. Width adapts to the label; clamps to
        // the cap so it never spills off the side.
        float fontSize = Math.Max(6f, rect.Height * 0.18f);
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold);
        var textSize = TextRenderer.MeasureText(g, label, font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        const float padX = 4f;
        const float padY = 1f;
        const float inset = 3f;
        float badgeW = Math.Min(textSize.Width + padX * 2, rect.Width - inset * 2);
        float badgeH = textSize.Height + padY * 2;

        var badgeRect = new RectangleF(
            rect.Right - inset - badgeW,
            rect.Top + inset,
            badgeW,
            badgeH);

        using var badgeBrush = new SolidBrush(CapBlue);
        using var badgePen = new Pen(CapBlue, 1f);
        DrawRoundedRect(g, badgeRect, badgeH * 0.3f, badgeBrush, badgePen);

        TextRenderer.DrawText(g, label, font, Rectangle.Round(badgeRect), GlyphWhite,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private static void DrawCentredText(Graphics g, string text, RectangleF rect, float fontSize,
                                        Color color, FontStyle style)
    {
        if (fontSize < 5f) return; // too small to be legible — skip
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, style);
        var pixelRect = Rectangle.Round(rect);
        TextRenderer.DrawText(g, text, font, pixelRect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Like <see cref="DrawCentredText"/>, but measures the text first and
    /// scales the font down if it would exceed the cap width. Used for
    /// multi-character fixed labels (GRAPH, BREAK, ALPHA, …) on narrow
    /// caps where height-based sizing alone overflows horizontally.
    /// </summary>
    private static void DrawFittingText(Graphics g, string text, RectangleF rect, float maxFontSize,
                                        Color color, FontStyle style)
    {
        if (string.IsNullOrEmpty(text) || maxFontSize < 5f || rect.Width < 6f) return;

        const float horizontalPad = 6f; // breathing room each side
        var pixelRect = Rectangle.Round(rect);
        float availWidth = pixelRect.Width - horizontalPad;

        // Probe at the requested max size; if it overflows, scale linearly
        // by the width ratio (sans-serif widths are close enough to linear
        // in font size that this lands within a pixel or two — no iteration
        // needed).
        float fontSize = maxFontSize;
        using (var probeFont = new Font(FontFamily.GenericSansSerif, maxFontSize, style))
        {
            var probeSize = TextRenderer.MeasureText(g, text, probeFont,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (probeSize.Width > availWidth)
                fontSize = maxFontSize * availWidth / probeSize.Width;
        }
        if (fontSize < 5f) return;

        using var font = new Font(FontFamily.GenericSansSerif, fontSize, style);
        TextRenderer.DrawText(g, text, font, pixelRect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private static float FontSizeForLabel(float keyHeightPx)
    {
        // Roughly fill ~45 % of the available height with the cap height
        // of the font (an empirical fudge — Sans-serif cap height is
        // ≈ 70 % of em-size, so em-size ≈ height * 0.45 / 0.70).
        return Math.Max(6f, keyHeightPx * 0.45f);
    }

    // Real MZ-700 cap colours:
    //   Blue  cap + white glyph: function keys.
    //   Amber cap + white glyph: mode (GRAPH / ALPHA), modifier
    //                            (CTRL / SHIFT), edit (BREAK / INST /
    //                            DEL), enter (CR), cursor arrows.
    //   White cap + black glyph: character keys + space + the BLANK
    //                            filler.
    private static readonly Color CapBlue   = Color.FromArgb(38, 68, 144);
    private static readonly Color CapAmber  = Color.FromArgb(205, 120, 40);
    private static readonly Color CapWhite  = Color.FromArgb(252, 252, 250);
    private static readonly Color GlyphWhite = Color.FromArgb(252, 252, 250);
    private static readonly Color GlyphBlack = Color.FromArgb(20, 20, 20);

    private static (Color fill, Color border, Color text) ColorsForKind(MzKeyboardLayout.KeyKind kind, bool hovered)
    {
        Color fill;
        Color text;
        switch (kind)
        {
            case MzKeyboardLayout.KeyKind.Function:
                fill = CapBlue;  text = GlyphWhite; break;
            case MzKeyboardLayout.KeyKind.Mode:
            case MzKeyboardLayout.KeyKind.Modifier:
            case MzKeyboardLayout.KeyKind.Edit:
            case MzKeyboardLayout.KeyKind.Enter:
            case MzKeyboardLayout.KeyKind.Cursor:
            case MzKeyboardLayout.KeyKind.Blank:
                fill = CapAmber; text = GlyphWhite; break;
            case MzKeyboardLayout.KeyKind.Character:
            case MzKeyboardLayout.KeyKind.Space:
            default:
                fill = CapWhite; text = GlyphBlack; break;
        }
        if (hovered)
            fill = LightenOrDarken(fill, -18);
        Color border = hovered ? Color.FromArgb(30, 30, 40) : Color.FromArgb(110, 110, 115);
        return (fill, border, text);
    }

    private static Color LightenOrDarken(Color c, int delta)
    {
        int r = Math.Clamp(c.R + delta, 0, 255);
        int gC = Math.Clamp(c.G + delta, 0, 255);
        int b = Math.Clamp(c.B + delta, 0, 255);
        return Color.FromArgb(c.A, r, gC, b);
    }
}

public sealed class KeyDiagramClickedEventArgs : EventArgs
{
    public MzKeyboardLayout.MzKey Key { get; }
    public KeyDiagramClickedEventArgs(MzKeyboardLayout.MzKey key) { Key = key; }
}
