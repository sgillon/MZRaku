using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A text-mode video renderer.
///
///   40 cols × 25 rows, 8 × 8 cells → 320 × 200 logical pixels.
///   VRAM (2 KiB effective) holds 8-bit display codes at $D000 onward.
///   Character generator ROM (SA-CG.rom) is 2 KiB — single bank, 256
///   characters × 8 rows each. No MZ-700-style bit-7 bank-select and
///   no per-cell attribute plane (the MZ-80A is monochrome).
///
///   Two global toggles handled at the video-renderer level rather
///   than per-cell:
///     * <see cref="Reverse"/> — set by <c>Mz80aIoBus</c> when the CPU
///       reads $E015 (reverse) / $E014 (normal). Inverts the whole
///       screen; used by demos and the SA-5510 BASIC startup.
///     * <see cref="ScrollOffset"/> — set by <c>Mz80aIoBus</c> when the
///       CPU reads any address in $E200-$E2FF (low byte selects the
///       new offset in 8-character units, wrapping within 2 KiB VRAM).
///       This is how the MZ-80A implements hardware scroll — the
///       visible 25 rows walk through VRAM as the offset advances,
///       instead of the CPU copying rows the way MZ-700 code has to.
///
/// The MZ-700's <c>VideoRenderer</c> is a close sibling. Reused idioms
/// (font ROM layout, LSB-first pixel decoding, 32bppArgb backing
/// bitmap) are called out inline.
/// </summary>
public sealed class Mz80aVideo
{
    public const int CharCols = 40;
    public const int CharRows = 25;
    public const int CharWidth = 8;
    public const int CharHeight = 8;
    public const int PixelWidth = CharCols * CharWidth;      // 320
    public const int PixelHeight = CharRows * CharHeight;    // 200

    // 2 KiB single-bank character generator ROM (SA-CG.rom).
    public byte[] FontRom = new byte[2048];

    /// <summary>When true, the whole screen renders black-on-white.</summary>
    public bool Reverse;

    /// <summary>
    /// Hardware scroll offset in 8-character units (0-255). The
    /// visible 25 rows start at <c>Vram[ScrollOffset * 8]</c> and wrap
    /// within the 2 KiB VRAM window. Set by the CPU via reads to
    /// $E200-$E2FF (low byte = offset).
    /// </summary>
    public int ScrollOffset;

    /// <summary>
    /// Foreground pixel colour as a 32-bit ARGB int (0xAARRGGBB). Set
    /// by MainForm from Settings — pure green (0xFF00FF00) for the
    /// authentic P1-phosphor look (matches EmuZ-80A's tint,
    /// user-confirmed via MS Paint dropper 2026-07-12), white
    /// (0xFFFFFFFF) otherwise. Reverse-video swaps this with
    /// <see cref="BackgroundArgb"/> per-frame.
    /// </summary>
    public int ForegroundArgb = unchecked((int)0xFF00FF00);

    /// <summary>Background pixel colour, mate to <see cref="ForegroundArgb"/>.</summary>
    public int BackgroundArgb = unchecked((int)0xFF000000);

    public Bitmap Frame = new(PixelWidth, PixelHeight, PixelFormat.Format32bppArgb);

    // Standalone-glyph cache for the Font Sheet pane, keyed by
    // (display-code, scale-factor). MZ-80A has a single font bank (no
    // MZ-700 attribute bit-7 select), so the key omits the bank dimension
    // that Video.cs threads through its equivalent cache.
    private readonly Dictionary<(byte code, int scale), Bitmap> _glyphCache = new();

    public void LoadFont(byte[] font)
    {
        int n = Math.Min(font.Length, FontRom.Length);
        Array.Copy(font, FontRom, n);
        InvalidateGlyphCache();
    }

    /// <summary>
    /// Render one MZ-80A character-ROM glyph to a standalone Bitmap,
    /// black-on-white for readability outside the emulator's palette
    /// (Font Sheet pane). MSB-first pixel decoding matches
    /// <see cref="Render"/>. Results cached per (code, scale); call
    /// <see cref="InvalidateGlyphCache"/> if the font ROM changes.
    /// </summary>
    public Bitmap GetGlyph(byte code, int scale = 2)
    {
        if (scale < 1) scale = 1;
        var key = (code, scale);
        if (_glyphCache.TryGetValue(key, out var cached)) return cached;
        var bmp = RenderGlyph(code, scale);
        _glyphCache[key] = bmp;
        return bmp;
    }

    public void InvalidateGlyphCache()
    {
        foreach (var bmp in _glyphCache.Values) bmp.Dispose();
        _glyphCache.Clear();
    }

    private Bitmap RenderGlyph(byte code, int scale)
    {
        int size = CharWidth * scale;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        int fontOff = code * CharHeight;
        int fg = unchecked((int)0xFF000000);
        int bg = unchecked((int)0xFFFFFFFF);
        var rect = new Rectangle(0, 0, size, size);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;
                for (int r = 0; r < CharHeight; r++)
                {
                    byte fb = FontRom[fontOff + r];
                    // MSB-first per Render(): bit 7 is the leftmost pixel.
                    // (Contrast Video.cs, which is LSB-first for the
                    // MZ-700's mz700fon.int.)
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int* dst = pix + (r * scale + sy) * stride;
                        for (int c = 0; c < CharWidth; c++)
                        {
                            int color = ((fb & (0x80 >> c)) != 0) ? fg : bg;
                            for (int sx = 0; sx < scale; sx++)
                                dst[c * scale + sx] = color;
                        }
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    public void Render(byte[] vram)
    {
        // MZ-80A is monochrome. Foreground/background come from
        // configurable ARGB fields (green-phosphor by default), swapped
        // when Reverse is set. Computed once per frame — no per-cell
        // attribute lookup.
        int fg = Reverse ? BackgroundArgb : ForegroundArgb;
        int bg = Reverse ? ForegroundArgb : BackgroundArgb;

        var rect = new Rectangle(0, 0, PixelWidth, PixelHeight);
        var data = Frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;

                // Hardware-scroll starting byte offset within VRAM.
                // The visible 25 rows walk from here through VRAM,
                // wrapping at the 2 KiB boundary.
                int startByte = (ScrollOffset * 8) & 0x7FF;

                for (int row = 0; row < CharRows; row++)
                {
                    int vramRowBase = (startByte + row * CharCols) & 0x7FF;
                    int pixY = row * CharHeight;
                    for (int col = 0; col < CharCols; col++)
                    {
                        int idx = (vramRowBase + col) & 0x7FF;
                        byte ch = vram[idx];
                        int fontOff = ch * CharHeight;
                        int pixX = col * CharWidth;
                        for (int r = 0; r < CharHeight; r++)
                        {
                            byte fb = FontRom[fontOff + r];
                            // SA-CG.rom stores glyph rows MSB-first —
                            // bit 7 is the leftmost column. (The
                            // MZ-700's mz700fon.int is LSB-first and
                            // uses the opposite ordering.) Confirmed
                            // empirically: LSB-first rendered the
                            // SA-1510 boot banner horizontally
                            // mirrored, MSB-first reads normally.
                            int* dst = pix + (pixY + r) * stride + pixX;
                            dst[0] = ((fb & 0x80) != 0) ? fg : bg;
                            dst[1] = ((fb & 0x40) != 0) ? fg : bg;
                            dst[2] = ((fb & 0x20) != 0) ? fg : bg;
                            dst[3] = ((fb & 0x10) != 0) ? fg : bg;
                            dst[4] = ((fb & 0x08) != 0) ? fg : bg;
                            dst[5] = ((fb & 0x04) != 0) ? fg : bg;
                            dst[6] = ((fb & 0x02) != 0) ? fg : bg;
                            dst[7] = ((fb & 0x01) != 0) ? fg : bg;
                        }
                    }
                }
            }
        }
        finally
        {
            Frame.UnlockBits(data);
        }
    }
}
