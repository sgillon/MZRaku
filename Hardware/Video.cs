using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace MZ700Emul.Hardware;

/// <summary>
/// MZ-700 text-mode video renderer.
///   40 cols x 25 rows, 8x8 cells -> 320x200 logical pixels.
///   VRAM (2KB) holds 8-bit display codes at 0xD000..0xD7FF.
///   Attribute RAM (2KB) at 0xD800..0xDFFF: bit 7 = char-set bank (0/1),
///     bits 6..4 = background color (3 bits), bits 2..0 = foreground color.
///   Font ROM is 4KB (2 banks x 256 chars x 8 rows/char).
/// </summary>
public sealed class VideoRenderer
{
    public const int CharCols = 40;
    public const int CharRows = 25;
    public const int CharWidth = 8;
    public const int CharHeight = 8;
    public const int PixelWidth = CharCols * CharWidth;      // 320
    public const int PixelHeight = CharRows * CharHeight;    // 200

    public byte[] FontRom = new byte[4096];

    // 8 MZ-700 colors (B,R,G wiring): 0=blk,1=blu,2=red,3=mag,4=grn,5=cyan,6=yel,7=wht
    private static readonly int[] Palette = new int[]
    {
        unchecked((int)0xFF000000), // black
        unchecked((int)0xFF0000FF), // blue
        unchecked((int)0xFFFF0000), // red
        unchecked((int)0xFFFF00FF), // magenta
        unchecked((int)0xFF00FF00), // green
        unchecked((int)0xFF00FFFF), // cyan
        unchecked((int)0xFFFFFF00), // yellow
        unchecked((int)0xFFFFFFFF), // white
    };

    public Bitmap Frame = new Bitmap(PixelWidth, PixelHeight, PixelFormat.Format32bppArgb);

    // Standalone-glyph cache for picker / matrix-grid UIs. Keyed by
    // (display-code, font-bank, scale-factor) so multiple scales coexist.
    // Invalidated when the font ROM is reloaded.
    private readonly Dictionary<(byte code, int bank, int scale), Bitmap> _glyphCache = new();

    public void LoadFont(byte[] font)
    {
        int n = Math.Min(font.Length, FontRom.Length);
        Array.Copy(font, FontRom, n);
        InvalidateGlyphCache();
    }

    public void LoadFontHex(string hexText)
    {
        // font_hex.txt: each line contains 8 hex bytes representing one character row
        // (or some similar layout). We parse tokens and fill sequentially.
        int pos = 0;
        int i = 0;
        while (i < hexText.Length && pos < FontRom.Length)
        {
            // Skip whitespace
            while (i < hexText.Length && !IsHex(hexText[i])) i++;
            if (i >= hexText.Length) break;
            int start = i;
            while (i < hexText.Length && IsHex(hexText[i])) i++;
            int len = i - start;
            if (len > 0)
            {
                string tok = hexText.Substring(start, len);
                // Tokens may be 2 hex chars (a byte) or longer (treat as hex stream)
                if (tok.Length % 2 != 0) tok = "0" + tok;
                for (int j = 0; j + 1 < tok.Length && pos < FontRom.Length; j += 2)
                {
                    FontRom[pos++] = byte.Parse(tok.Substring(j, 2), System.Globalization.NumberStyles.HexNumber);
                }
            }
        }
        InvalidateGlyphCache();
    }

    /// <summary>
    /// Render one MZ-700 character-ROM glyph to a standalone Bitmap, scaled
    /// by an integer factor. Black-on-white so the result is readable out
    /// of the MZ palette context (used by picker / matrix-grid UIs).
    /// Results are cached per (code, bank, scale); call
    /// <see cref="InvalidateGlyphCache"/> if the font ROM changes.
    /// </summary>
    public Bitmap GetGlyph(byte code, int bank, int scale = 2)
    {
        if (scale < 1) scale = 1;
        var key = (code, bank, scale);
        if (_glyphCache.TryGetValue(key, out var cached)) return cached;
        var bmp = RenderGlyph(code, bank, scale);
        _glyphCache[key] = bmp;
        return bmp;
    }

    public void InvalidateGlyphCache()
    {
        foreach (var bmp in _glyphCache.Values) bmp.Dispose();
        _glyphCache.Clear();
    }

    private Bitmap RenderGlyph(byte code, int bank, int scale)
    {
        int size = CharWidth * scale;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        int fontOff = (bank & 1) * 2048 + code * CharHeight;
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
                    // Same LSB-first decoding as Render; bit 0 is leftmost.
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int* dst = pix + (r * scale + sy) * stride;
                        for (int c = 0; c < CharWidth; c++)
                        {
                            int color = ((fb & (1 << c)) != 0) ? fg : bg;
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

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    public void Render(byte[] vram, byte[] aram)
    {
        var rect = new Rectangle(0, 0, PixelWidth, PixelHeight);
        var data = Frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;

                for (int row = 0; row < CharRows; row++)
                {
                    int vramRowBase = row * CharCols;
                    int pixY = row * CharHeight;
                    for (int col = 0; col < CharCols; col++)
                    {
                        int idx = vramRowBase + col;
                        byte ch = vram[idx];
                        byte attr = aram[idx];
                        int bank = (attr >> 7) & 1;
                        // MZ-700 attribute: bits 6..4 = FG, bits 2..0 = BG
                        int fg = Palette[(attr >> 4) & 7];
                        int bg = Palette[attr & 7];
                        int fontOff = bank * 2048 + ch * 8;

                        int pixX = col * CharWidth;
                        for (int r = 0; r < CharHeight; r++)
                        {
                            byte fb = FontRom[fontOff + r];
                            // Font ROM stores pixels LSB-first (bit 0 = leftmost column)
                            int* dst = pix + (pixY + r) * stride + pixX;
                            dst[0] = ((fb & 0x01) != 0) ? fg : bg;
                            dst[1] = ((fb & 0x02) != 0) ? fg : bg;
                            dst[2] = ((fb & 0x04) != 0) ? fg : bg;
                            dst[3] = ((fb & 0x08) != 0) ? fg : bg;
                            dst[4] = ((fb & 0x10) != 0) ? fg : bg;
                            dst[5] = ((fb & 0x20) != 0) ? fg : bg;
                            dst[6] = ((fb & 0x40) != 0) ? fg : bg;
                            dst[7] = ((fb & 0x80) != 0) ? fg : bg;
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
