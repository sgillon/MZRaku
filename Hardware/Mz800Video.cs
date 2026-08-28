using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 video renderer — Phase 2 MZ-700-mode fork of <see cref="Video"/>.
///
/// The MZ-800 in MZ-700 mode uses the same 40×25 char-cell layout as
/// the MZ-700: 8×8 cells → 320×200 logical pixels, 8-bit display codes
/// in VRAM at $D000-$D7FF, attribute bytes in ARAM at $D800-$DFFF
/// (bit 7 = char-set bank, bits 6-4 = FG, bits 2-0 = BG). Palette is
/// the same 8-color BRG mapping. So the pixel path forks 1:1 from
/// <see cref="Video"/>; only the font-load path differs — MZ-800 has
/// no separate CG-ROM file, the character generator lives inside the
/// combined 16 KB MZ800.ROM at offset $1000-$1FFF.
///
/// Real hardware actually uses a PCG (Programmable Character
/// Generator) in VRAM that the IPL populates from CG-ROM at cold
/// boot. Phase 2 simplifies by reading straight from the CG-ROM data
/// in <see cref="MZ800Memory.Rom"/> — this matches what a
/// freshly-booted machine displays. A program that later rewrites
/// PCG bytes (writes to $D000-$DFFF while bank config (c) is active,
/// which our memory model routes to Vram/Aram) wouldn't be reflected
/// in the font here; Phase 5 refactors when the bitmap renderer
/// arrives and PCG modelling becomes load-bearing.
///
/// Phase 5 will add the MZ-800-mode bitmap renderer (320×200 or
/// 640×200 from bit-planes I-IV via the CRTC, with a 4-register
/// palette and hardware scroll). Until then, this class covers the
/// display when the machine is in MZ-700 mode.
/// </summary>
public sealed class Mz800Video
{
    public const int CharCols = 40;
    public const int CharRows = 25;
    public const int CharWidth = 8;
    public const int CharHeight = 8;
    public const int PixelWidth = CharCols * CharWidth;      // 320
    public const int PixelHeight = CharRows * CharHeight;    // 200

    // 4 KB font ROM (2 banks × 256 chars × 8 rows) — mirrors MZ-700's
    // shape. Loaded from Mem.Rom[$1000-$1FFF] via
    // <see cref="LoadFontFromRom"/> during MZ800.LoadRoms.
    public byte[] FontRom = new byte[4096];

    // Same 8-color palette as MZ-700 (BRG wiring).
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

    /// <summary>
    /// Copy the 4 KB CG-ROM out of the combined MZ800.ROM into
    /// <see cref="FontRom"/>. Called from
    /// <see cref="MZRaku.MZ800.LoadRoms"/> after the ROM file lands
    /// in memory. Offset $1000-$1FFF per tech-ref p. 24 ROM
    /// configuration diagram.
    /// </summary>
    public void LoadFontFromRom(byte[] rom)
    {
        const int cgOffset = 0x1000;
        int n = Math.Min(rom.Length - cgOffset, FontRom.Length);
        if (n > 0) Array.Copy(rom, cgOffset, FontRom, 0, n);
    }

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
