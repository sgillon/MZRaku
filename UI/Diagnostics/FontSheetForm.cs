using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Diagnostic view of the loaded character ROM — all glyphs rendered
/// in display-code order, one bank per section.
///
/// Click-to-type works in BASIC only, on all three machines (v1.5
/// 4.8a): graphics glyphs are typed at BASIC's prompt; a program run
/// from the monitor would set them through memory instead. While BASIC
/// runs, the cells its keyboard can type are outlined, green for normal
/// (ALPHA) mode and blue for GRAPH-only, from BASIC's own key tables.
/// Clicking one queues its key into the auto-typer. If only the other
/// mode types it, the click switches mode, types it and switches back
/// to the mode BASIC was in, so each click stands alone. Outside BASIC
/// a click only names the code.
///
/// MZ-700: two 16 × 16 sections, one per font bank (attribute bit 7).
/// S-BASIC's keyboard only produces bank-0 codes, in every mode
/// (<see cref="Mz700KeyTables"/>), so only bank 0 has outlines. Bank 1
/// is reached only by a program setting attribute bit 7, so its clicks
/// just explain that (v1.5 Phase 2.3, issue #3).
///
/// MZ-80A: two 16 × 8 sections, Text (codes $00–$7F) and Graphics
/// ($80–$FF), from a single-bank font ROM (no attribute bit-7 select).
/// SA-5510 reads keys through SA-1510's GETKY, whose tables
/// (<see cref="Mz80aKeyTables"/>) cover both sections (v1.5 4.8).
///
/// MZ-800: the CG-ROM, in the MZ-700's two-bank layout. 1Z-016 BASIC
/// draws its text with the bank-0 glyphs, typed from its own key tables
/// (<see cref="Mz800KeyTables"/>), as on the MZ-700 (v1.5 4.8).
///
/// S-BASIC and 1Z-016 share their mode keys: GRAPH selects GRAPH, ALPHA
/// selects ALPHA (from GRAPH), SHIFT+ALPHA selects shift lock (from
/// GRAPH). The MZ-80A's GRPH toggles.
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
    private readonly MZ800? _mz800;

    // For MZ-700 and MZ-800 the section index doubles as the font-bank
    // index (attribute bit 7). For MZ-80A the font is single-bank; the
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
    // One line only: the label is fixed-height and word-wraps, so a
    // message wider than the sheet (~58 characters) vanishes into a
    // clipped second line.
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
        _mz800 = machine as MZ800;

        if (_mz800 != null)
        {
            _sections = new List<Section>
            {
                new("Bank 0", 0x00, 16),
                new("Bank 1", 0x00, 16),
            };
            _headerLabel.Text = "In BASIC, click an outlined glyph to type it (green: ALPHA, blue: GRAPH).";
        }
        else if (_mz700 != null)
        {
            _sections = new List<Section>
            {
                new("Bank 0", 0x00, 16),
                new("Bank 1", 0x00, 16),
            };
            _headerLabel.Text = "In BASIC, click an outlined glyph to type it (green: ALPHA, blue: GRAPH).";
        }
        else
        {
            _sections = new List<Section>
            {
                new("Text", 0x00, 8),
                new("Graphics", 0x80, 8),
            };
            _headerLabel.Text = "In BASIC, click an outlined glyph to type it (green: normal, blue: GRAPH).";
        }

        Text = "Font Sheet";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;
        ShowInTaskbar = false;

        _pic.MouseClick += OnCellClick;
        // Outlines show only while BASIC runs, so refresh on each visit.
        Activated += (_, _) => RedrawSheet();

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

        int? basicMode = BasicMode();
        using var alphaPen = new Pen(Color.MediumSeaGreen, 1f);
        using var graphPen = new Pen(Color.DodgerBlue, 1f);
        for (int r = 0; r < section.RowCount; r++)
        {
            int cellY = yOffset + r * CellPx;
            byte rowHigh = (byte)(section.FirstCode + r * CellsPerRow);
            g.DrawString($"{rowHigh >> 4:X}{r & 0xF:X}_", hdrFont, hdrBrush, 2, cellY + CellPx / 2 - 6);
            for (int c = 0; c < CellsPerRow; c++)
            {
                byte code = (byte)(section.FirstCode + r * CellsPerRow + c);
                int cellX = MarginPx + c * CellPx;
                var glyph = _mz700 != null ? _mz700.Video.GetGlyph(code, sectionIndex, GlyphScale)
                          : _mz800 != null ? _mz800.Video.GetGlyph(code, sectionIndex, GlyphScale)
                          : _mz80a!.Video.GetGlyph(code, GlyphScale);
                using (var glyphBmp = FrameBufferBitmap.ToBitmap(glyph))
                    g.DrawImageUnscaled(glyphBmp, cellX, cellY);

                // Outline the cells BASIC's keyboard can type: green for
                // normal (ALPHA) mode, blue for GRAPH-only. Shift lock
                // types the same codes as ALPHA (SHIFT only swaps).
                if (basicMode != null && (sectionIndex == 0 || _mz80a != null))
                {
                    var pen = FindKey(code, 0) != null ? alphaPen
                            : FindKey(code, 2) != null ? graphPen
                            : null;
                    if (pen != null)
                        g.DrawRectangle(pen, cellX, cellY, CellPx - 1, CellPx - 1);
                }
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

        int? liveMode = BasicMode();
        if (liveMode == null)
        {
            _statusLabel.Text = $"{_sections[sectionIndex].Label} ${code:X2}: types only in {BasicName}.";
            return;
        }

        // The keyboard only types bank-0 codes; bank 1 needs a program
        // to set attribute bit 7. (The MZ-80A's sections aren't banks.)
        if (sectionIndex != 0 && _mz80a == null)
        {
            _statusLabel.Text =
                $"Bank 1 ${code:X2} can't be typed: programs set attribute bit 7.";
            return;
        }

        // Each click ends by restoring the mode it started from, so while
        // earlier clicks are still being typed, BASIC will end up in the
        // mode the first of them saw.
        if (TyperIdle) _queuedFromMode = liveMode;
        int mode = _queuedFromMode ?? liveMode.Value;

        if (FindKey(code, mode) is { } key)
        {
            Press(key);
            _statusLabel.Text = $"Typed code ${code:X2} via {ModeName(mode)} {Describe(key)}.";
            return;
        }

        // Only the other mode types it: switch, type, switch back.
        int other = mode == 2 ? 0 : 2;
        if (FindKey(code, other) is not { } otherKey)
        {
            _statusLabel.Text = $"Code ${code:X2} isn't reachable from the keyboard.";
            return;
        }
        Press(ModeKey(other));
        Press(otherKey);
        Press(ModeKey(mode));
        _statusLabel.Text =
            $"Typed ${code:X2} via {ModeName(other)} {Describe(otherKey)}, back to {ModeName(mode)}.";
    }

    // The mode the auto-typer's queue will leave BASIC in (see OnCellClick).
    private int? _queuedFromMode;

    private readonly record struct Key(int A, int B, bool Shift);

    // Mode keys. S-BASIC and 1Z-016: GRAPH (0,6) selects GRAPH from any
    // mode; from GRAPH, ALPHA (0,4) selects ALPHA and SHIFT+ALPHA shift
    // lock. The MZ-80A's GRPH (strobe 0, bit 1) toggles.
    private static readonly Key Mz80aGrph = new(0, 1, false);
    private static readonly Key GraphKey = new(0, 6, false);
    private static readonly Key AlphaKey = new(0, 4, false);
    private static readonly Key ShiftLockKey = new(0, 4, true);

    // BASIC's keyboard mode: 0 normal (ALPHA), 1 shift lock, 2 GRAPH;
    // null while BASIC isn't running. SA-1510's own shift inversion
    // ($118F) is handled inside Mz80aKeyTables, so the MZ-80A has no 1.
    private int? BasicMode()
    {
        if (_mz700 != null) return _mz700.KeyTables.BasicKeyboardMode;
        if (_mz800 != null) return _mz800.BasicKeyboardMode;
        var tables = _mz80a!.KeyTables;
        return tables.BasicRunning ? (tables.InGraphMode ? 2 : 0) : null;
    }

    private Key? FindKey(byte code, int mode)
    {
        if (_mz700 != null) return ToKey(_mz700.KeyTables.FindByDisplayCode(code, mode));
        if (_mz800 != null) return ToKey(_mz800.KeyTables.FindByDisplayCode(code, mode));
        return _mz80a!.KeyTables.FindByDisplayCode(code, mode == 2) is { } k
            ? new Key(k.Strobe, k.Bit, k.MzShift) : null;
    }

    private static Key? ToKey((int Row, int Col, bool MzShift)? slot) =>
        slot is { } s ? new Key(s.Row, s.Col, s.MzShift) : null;

    private bool TyperIdle =>
        _mz700 != null ? _mz700.Keyboard.AutoType.Idle
        : _mz800 != null ? _mz800.Keyboard.AutoType.Idle
        : _mz80a!.Keyboard.AutoTypeIdle;

    private void Press(Key k)
    {
        if (_mz80a != null)
            _mz80a.Keyboard.TypePress(new Mz80aCharMap.Press(k.A, k.B, k.Shift));
        else
            (_mz700?.Keyboard.AutoType ?? _mz800!.Keyboard.AutoType).TypePress(new Mz700CharMap.Press(k.A, k.B, k.Shift));
    }

    // The key that puts BASIC in `mode`: pressed from GRAPH for normal or
    // shift lock, and from either of those for GRAPH.
    private Key ModeKey(int mode) =>
        _mz80a != null ? Mz80aGrph : mode switch { 2 => GraphKey, 1 => ShiftLockKey, _ => AlphaKey };

    private string ModeName(int mode) =>
        mode switch { 2 => "GRAPH", 1 => "LOCK", _ => _mz80a != null ? "normal" : "ALPHA" };

    private string BasicName =>
        _mz700 != null ? "S-BASIC" : _mz800 != null ? "MZ-800 BASIC" : "SA-5510";

    private static string Describe(Key k) => $"{(k.Shift ? "shift+" : "")}({k.A},{k.B})";

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
