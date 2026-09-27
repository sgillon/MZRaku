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

    // Phase 5.6 640×200 mono output. Kept as a second bitmap rather
    // than resizing Frame so the existing 320-mode + MZ-700-mode paths
    // stay byte-identical. MZ800.VideoFrame picks whichever matches
    // the current DMD-selected resolution; MainForm scales from the
    // returned bitmap's own Width/Height (mode-agnostic).
    public const int HiPixelWidth = 640;
    public const int HiPixelHeight = 200;
    public Bitmap FrameHi = new Bitmap(HiPixelWidth, HiPixelHeight, PixelFormat.Format32bppArgb);

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

    // Stand-in for a plane that isn't part of the current mode (or,
    // without the MZ-1R25, doesn't exist): always reads as zero.
    private static readonly byte[] NoPlane = new byte[0x4000];

    /// <summary>
    /// Colour lookup for the palette modes: entries 0-3 = PLT0-3.
    /// Pixel code = planeA_bit + 2 × planeB_bit — tech-ref p. 22
    /// "output select" A/B (320 4-colour: A = plane I / III, B = plane
    /// II / IV; 640 4-colour: A = I, B = III; 640 1-colour: A only).
    /// </summary>
    public static int[] PaletteLut(byte[] palette)
    {
        var lut = new int[16];
        for (int c = 0; c < 16; c++) lut[c] = IrgbToArgb(palette[c & 3]);
        return lut;
    }

    /// <summary>
    /// Colour lookup for 320×200 16-colour mode (tech-ref pp. 22-23).
    /// Pixel code = I | II&lt;&lt;1 | III&lt;&lt;2 | IV&lt;&lt;3, which is
    /// directly an IGRB colour (plane I = B, II = R, III = G, IV = I).
    /// Codes whose group bits (III, IV) equal the palette switch
    /// SW0/SW1 (<paramref name="paletteGroup"/>, set by OUT $F0 with
    /// register select 4) go through PLT0-3 instead.
    /// </summary>
    public static int[] SixteenColourLut(byte[] palette, int paletteGroup)
    {
        var lut = new int[16];
        for (int c = 0; c < 16; c++)
            lut[c] = (c >> 2) == paletteGroup ? IrgbToArgb(palette[c & 3]) : IrgbToArgb((byte)c);
        return lut;
    }

    /// <summary>
    /// Phase 5.5 entry point, kept for the dump recorder's test
    /// patterns: 320×200 4-colour from planes I + II.
    /// </summary>
    public void RenderBitmap(byte[] planeI, byte[] planeII, byte[] palette, byte borderIrgb, int scrollLines = 0)
        => RenderPlanes320(planeI, planeII, NoPlane, NoPlane, PaletteLut(palette), scrollLines);

    /// <summary>
    /// Phase 5.6 entry point, kept for the dump recorder's test
    /// patterns: 640×200 1-colour from plane I.
    /// </summary>
    public void RenderBitmap640Mono(byte[] planeI, byte[] palette, byte borderIrgb, int scrollLines = 0)
        => RenderPlanes640(planeI, NoPlane, PaletteLut(palette), scrollLines);

    /// <summary>
    /// MZ-800-mode 320×200 renderer for every plane combination
    /// (Phase 7.1 generalisation of the Phase 5.5 Frame A renderer).
    /// Each pixel's code is <c>p0 | p1&lt;&lt;1 | p2&lt;&lt;2 | p3&lt;&lt;3</c>
    /// and indexes <paramref name="lut"/> (see <see cref="PaletteLut"/>
    /// / <see cref="SixteenColourLut"/>). Pass <c>null</c> for planes
    /// the mode doesn't use.
    ///
    /// Layout: plane offset = addr - $8000, 40 bytes per scanline,
    /// LSB-first (bit 0 = leftmost pixel), research/02-plane-layout.md.
    /// <paramref name="scrollLines"/> (Phase 5.7) = <c>Sof / 5</c>;
    /// plane row for display row Y is <c>(Y + scrollLines) mod 200</c>
    /// (SSA/SEA windowing deferred). Border not painted — the 320×200
    /// active area fills <see cref="Frame"/>.
    /// </summary>
    public void RenderPlanes320(byte[]? p0, byte[]? p1, byte[]? p2, byte[]? p3, int[] lut, int scrollLines = 0)
    {
        p0 ??= NoPlane; p1 ??= NoPlane; p2 ??= NoPlane; p3 ??= NoPlane;
        int scroll = ((scrollLines % PixelHeight) + PixelHeight) % PixelHeight;

        var rect = new Rectangle(0, 0, PixelWidth, PixelHeight);
        var data = Frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;
                const int bytesPerRow = PixelWidth / 8; // 40
                for (int y = 0; y < PixelHeight; y++)
                {
                    int planeRow = y + scroll;
                    if (planeRow >= PixelHeight) planeRow -= PixelHeight;
                    int rowBase = planeRow * bytesPerRow;
                    int* rowPix = pix + y * stride;
                    for (int col = 0; col < bytesPerRow; col++)
                    {
                        int offset = rowBase + col;
                        int b0 = p0[offset], b1 = p1[offset], b2 = p2[offset], b3 = p3[offset];
                        int pixX = col * 8;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            int code = ((b0 >> bit) & 1) | (((b1 >> bit) & 1) << 1)
                                     | (((b2 >> bit) & 1) << 2) | (((b3 >> bit) & 1) << 3);
                            rowPix[pixX + bit] = lut[code];
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
    /// MZ-800-mode 640×200 renderer (Phase 7.1 generalisation of the
    /// Phase 5.6 mono renderer). Pixel code = <c>a | b&lt;&lt;1</c>
    /// indexing <paramref name="lut"/>: 1-colour passes plane I (Frame
    /// A) or III (Frame B) as <paramref name="planeA"/>; 4-colour
    /// passes I and III (tech-ref p. 22).
    ///
    /// Each plane packs 80 bytes per scanline interleaved across its
    /// two 8 KB halves (tech-ref p. 15): display byte n = row×80 + c,
    /// even c → offset row×40 + c/2, odd c → $2000 + row×40 + c/2.
    /// LSB-first pixels. Scroll as <see cref="RenderPlanes320"/>.
    /// </summary>
    public void RenderPlanes640(byte[]? planeA, byte[]? planeB, int[] lut, int scrollLines = 0)
    {
        planeA ??= NoPlane;
        planeB ??= NoPlane;
        int scroll = ((scrollLines % HiPixelHeight) + HiPixelHeight) % HiPixelHeight;

        var rect = new Rectangle(0, 0, HiPixelWidth, HiPixelHeight);
        var data = FrameHi.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride / 4;
                int* pix = (int*)data.Scan0;
                const int bytesPerRow = HiPixelWidth / 8; // 80
                const int oddBankBase = 0x2000;
                for (int y = 0; y < HiPixelHeight; y++)
                {
                    int planeRow = y + scroll;
                    if (planeRow >= HiPixelHeight) planeRow -= HiPixelHeight;
                    int evenRowBase = planeRow * (bytesPerRow / 2);
                    int oddRowBase  = oddBankBase + planeRow * (bytesPerRow / 2);
                    int* rowPix = pix + y * stride;
                    for (int c = 0; c < bytesPerRow; c++)
                    {
                        int planeAddr = ((c & 1) == 0) ? evenRowBase + (c >> 1) : oddRowBase + (c >> 1);
                        int a = planeA[planeAddr], b = planeB[planeAddr];
                        int pixX = c * 8;
                        for (int bit = 0; bit < 8; bit++)
                            rowPix[pixX + bit] = lut[((a >> bit) & 1) | (((b >> bit) & 1) << 1)];
                    }
                }
            }
        }
        finally
        {
            FrameHi.UnlockBits(data);
        }
    }
}
