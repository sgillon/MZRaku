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

    /// <summary>
    /// Convert an MZ-800 4-bit IRGB colour code to 32-bit ARGB, for the
    /// Phase 5.5 bitmap renderer to consume when it paints plane pixels
    /// through the palette.
    ///
    /// Bit layout (BRG wiring, matching MZ-700's <c>Video.Palette</c>
    /// table — Sharp uses this ordering consistently across the 700/800
    /// family): D3=I intensity, D2=G green, D1=R red, D0=B blue.
    /// So the 3-bit RGB portion decodes as:
    ///   0 black · 1 blue · 2 red · 3 magenta · 4 green · 5 cyan · 6 yellow · 7 white
    /// Verified against Phase 5.0's BASIC-cold-boot palette writes
    /// (`$00 $11 $22 $3F` for slots 0-3 = black / blue / red /
    /// bright-white) — matches BASIC's intended "text on black,
    /// alternate colours available" layout.
    ///
    /// Intensity formula (provisional): channels are 0 when off, 0xAA
    /// when on without I, 0xFF when on with I — classic CGA-family
    /// ramp. IRGB=$8 (intensity alone with no primary) renders as
    /// dark grey (0x55 across channels) so a "bright black" palette
    /// slot is visually distinct from natural black IRGB=$0. Phase 5.5
    /// revisits both the wiring and the intensity ramp if visible
    /// output doesn't match reference-emulator screenshots — see
    /// research/05-palette.md.
    /// </summary>
    public static int IrgbToArgb(byte irgb)
    {
        bool i = (irgb & 0x08) != 0;
        bool g = (irgb & 0x04) != 0;
        bool r = (irgb & 0x02) != 0;
        bool b = (irgb & 0x01) != 0;
        byte on = i ? (byte)0xFF : (byte)0xAA;
        byte cr = r ? on : (byte)0;
        byte cg = g ? on : (byte)0;
        byte cb = b ? on : (byte)0;
        if (irgb == 0x08) { cr = cg = cb = 0x55; }
        return unchecked((int)0xFF000000) | (cr << 16) | (cg << 8) | cb;
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

    /// <summary>
    /// Phase 5.5 MZ-800-mode 320×200 4-colour Frame A renderer.
    /// Reads planes I and II, combines each pair of bits into a 2-bit
    /// pixel colour code, resolves through the palette + IrgbToArgb,
    /// and paints into <see cref="Frame"/> — the same bitmap the
    /// MZ-700-mode <see cref="Render"/> path uses, so
    /// <see cref="MainForm"/>'s Display_Paint doesn't need to know
    /// which mode is active.
    ///
    /// Plane offset per CPU-visible address is
    /// <c>plane_offset = addr - $8000</c> (see research/02-plane-layout.md).
    /// 40 bytes cover one scanline × 200 scanlines = 8000 bytes total.
    /// Bit ordering per byte: LSB-first (bit 0 = leftmost pixel),
    /// matching the MZ-700 CG-ROM convention above. If Phase 5.9
    /// verification shows mirrored pixels, flip the shift index.
    ///
    /// Colour code: <c>(planeII_bit &lt;&lt; 1) | planeI_bit</c>. If
    /// verification shows colours consistently swapped across all
    /// non-black pixels, swap the two plane arguments at the call
    /// site (simpler than adjusting the shift here).
    ///
    /// Border is not painted here — the 320×200 pixel area fills the
    /// full <see cref="Frame"/>. Real hardware surrounds the active
    /// image with a coloured border on an overscanned CRT area; if we
    /// later want to model that, grow Frame and paint BorderColour
    /// around a centred 320×200 active rect.
    /// </summary>
    public void RenderBitmap(byte[] planeI, byte[] planeII, byte[] palette, byte borderIrgb)
    {
        // Resolve the 4-entry palette to ARGB once per frame.
        int c0 = IrgbToArgb(palette[0]);
        int c1 = IrgbToArgb(palette[1]);
        int c2 = IrgbToArgb(palette[2]);
        int c3 = IrgbToArgb(palette[3]);
        int _ = IrgbToArgb(borderIrgb); // reserved: border painting arrives when we grow Frame past 320×200

        var rect = new Rectangle(0, 0, PixelWidth, PixelHeight);
        var data = Frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;
                const int bytesPerRow = PixelWidth / 8; // 40 bytes per scanline for 320 pixels
                for (int y = 0; y < PixelHeight; y++)
                {
                    int rowBase = y * bytesPerRow;
                    int* rowPix = pix + y * stride;
                    for (int col = 0; col < bytesPerRow; col++)
                    {
                        int offset = rowBase + col;
                        byte b1 = planeI[offset];
                        byte b2 = planeII[offset];
                        int pixX = col * 8;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            int mask = 1 << bit;
                            int code = ((b2 & mask) != 0 ? 2 : 0) | ((b1 & mask) != 0 ? 1 : 0);
                            int argb = code switch
                            {
                                0 => c0,
                                1 => c1,
                                2 => c2,
                                _ => c3,
                            };
                            rowPix[pixX + bit] = argb;
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
