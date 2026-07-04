using System;
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

    public Bitmap Frame = new(PixelWidth, PixelHeight, PixelFormat.Format32bppArgb);

    public void LoadFont(byte[] font)
    {
        int n = Math.Min(font.Length, FontRom.Length);
        Array.Copy(font, FontRom, n);
    }

    public void Render(byte[] vram)
    {
        // MZ-80A is monochrome: white on black, or inverted globally
        // when Reverse is set. Foreground/background computed once
        // per frame — no per-cell attribute lookup.
        int white = unchecked((int)0xFFFFFFFF);
        int black = unchecked((int)0xFF000000);
        int fg = Reverse ? black : white;
        int bg = Reverse ? white : black;

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
