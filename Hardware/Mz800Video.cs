using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 video output — two render paths, picked per frame by
/// <see cref="MZRaku.MZ800"/> from the DMD register:
///
///   • MZ-700 mode (<see cref="Render"/>): 40×25 character cells, 8×8
///     glyphs, display codes at $D000-$D7FF and attributes at
///     $D800-$DFFF (bit 7 = character set, bits 6-4 = foreground, bits
///     2-0 = background), 8 fixed colours. Glyphs come straight from
///     the CG-ROM inside MZ800.ROM ($1000-$1FFF). Real hardware draws
///     from a PCG copy in VRAM that the IPL fills at boot (tech-ref
///     p. 13); software that later redefines characters through the
///     PCG window isn't reflected — not modelled yet.
///   • MZ-800 bitmap modes (<see cref="RenderPlanes320"/> /
///     <see cref="RenderPlanes640"/>): planes I-IV decoded through a
///     per-row colour lookup (palette, 16-colour groups — tech-ref
///     pp. 17-23) and the hardware-scroll address map (pp. 10-12).
///
/// Output bitmaps: <see cref="Frame"/> (320×200, MZ-700 mode and
/// 320-wide bitmap modes) and <see cref="FrameHi"/> (640×200). The
/// border is not drawn. See _mz800info/MZ800_VideoRendering_Research/
/// for the decode notes and citations.
/// </summary>
public sealed class Mz800Video
{
    public const int CharCols = 40;
    public const int CharRows = 25;
    public const int CharWidth = 8;
    public const int CharHeight = 8;
    public const int PixelWidth = CharCols * CharWidth;      // 320
    public const int PixelHeight = CharRows * CharHeight;    // 200

    // 4 KB CG-ROM (2 character sets × 256 glyphs × 8 rows), copied
    // from MZ800.ROM $1000-$1FFF by LoadFontFromRom.
    public byte[] FontRom = new byte[4096];

    // MZ-700-mode attribute colours (B R G bit order, as MZ-700).
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

    // 640×200 output. A second bitmap rather than a resized Frame;
    // MZ800.VideoFrame returns whichever matches the DMD resolution.
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
    /// MZ-800 4-bit colour → 32-bit ARGB. Bit order I G R B (D3 =
    /// intensity, D2 = green, D1 = red, D0 = blue — tech-ref p. 22), so
    /// the low three bits run 0 black · 1 blue · 2 red · 3 magenta ·
    /// 4 green · 5 cyan · 6 yellow · 7 white, the same order as the
    /// MZ-700 attribute colours.
    ///
    /// Channel levels: 0 off, $AA on, $FF on with intensity; colour 8
    /// (intensity alone) is dark grey ($55) so it stays distinct from
    /// black. The ramp is an approximation — the tech-ref gives no
    /// analogue levels.
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

    /// <summary>
    /// MZ-700-mode character display: 40×25 cells from the display codes
    /// in <paramref name="vram"/> and attributes in <paramref name="aram"/>,
    /// glyphs from <see cref="FontRom"/> (LSB = leftmost pixel).
    /// </summary>
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

    /// <summary>The same colour lookup for every display row (no raster changes).</summary>
    public static int[][] UniformRows(int[] lut)
    {
        var rows = new int[PixelHeight][];
        Array.Fill(rows, lut);
        return rows;
    }

    /// <summary>
    /// Hardware-scroll registers (tech-ref pp. 10-12), in register units:
    /// SSA / SEA / SW count 64-byte blocks (5 = one 8-raster character
    /// row), SOF counts 8-byte blocks (5 = one raster line).
    /// </summary>
    public readonly record struct ScrollRegs(int Ssa, int Sea, int Sw, int Sof)
    {
        /// <summary>Power-on "no scroll" state (p. 10 §2).</summary>
        public static readonly ScrollRegs None = new(0x00, 0x7D, 0x7D, 0);
    }

    private const int DisplayBytes = 8000;              // 40 display addresses × 200 rasters
    private readonly int[] _addressMap = new int[DisplayBytes];
    private ScrollRegs _mappedScroll = new(-1, -1, -1, -1);

    /// <summary>
    /// Display address → VRAM address after scrolling (tech-ref p. 11
    /// bit table, p. 12 "execution of scrolling by address
    /// conversion"). The CRTC walks display addresses DA 0..7999 (40 per
    /// raster). Inside the scroll window [SSA·64, SEA·64) it fetches
    /// <c>SSA·64 + ((DA − SSA·64 + SOF·8) mod SW·64)</c>; outside, DA
    /// itself — which is how a split screen keeps fixed bands above /
    /// below the scrolling one (p. 10 §5). SOF that isn't a multiple of
    /// 5 shifts by part of a raster line.
    /// </summary>
    private int[] AddressMap(ScrollRegs sc)
    {
        if (sc == _mappedScroll) return _addressMap;
        _mappedScroll = sc;
        int start = sc.Ssa * 64, end = Math.Min(sc.Sea * 64, DisplayBytes), width = sc.Sw * 64;
        int offset = sc.Sof * 8;
        for (int da = 0; da < DisplayBytes; da++)
            _addressMap[da] = da >= start && da < end && width > 0
                ? start + (da - start + offset) % width
                : da;
        return _addressMap;
    }

    /// <summary>
    /// MZ-800 320×200 bitmap renderer for every plane combination
    /// (Frame A, Frame B, 16-colour). Each pixel's code is
    /// <c>p0 | p1&lt;&lt;1 | p2&lt;&lt;2 | p3&lt;&lt;3</c> and indexes its
    /// display row's lookup in <paramref name="rowLuts"/> (see
    /// <see cref="PaletteLut"/> / <see cref="SixteenColourLut"/>) — one
    /// per row so mid-frame palette changes (raster effects) show. Pass
    /// <c>null</c> for planes the mode doesn't use.
    ///
    /// Layout: plane offset = VRAM address, 40 bytes per raster,
    /// LSB-first (bit 0 = leftmost pixel), research/02-plane-layout.md;
    /// each display address goes through the scroll map
    /// (<see cref="AddressMap"/>). Border not painted — the 320×200
    /// active area fills <see cref="Frame"/>.
    /// </summary>
    public void RenderPlanes320(byte[]? p0, byte[]? p1, byte[]? p2, byte[]? p3, int[][] rowLuts, ScrollRegs scroll)
    {
        p0 ??= NoPlane; p1 ??= NoPlane; p2 ??= NoPlane; p3 ??= NoPlane;
        int[] map = AddressMap(scroll);

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
                    int rowBase = y * bytesPerRow;
                    int* rowPix = pix + y * stride;
                    int[] lut = rowLuts[y];
                    for (int col = 0; col < bytesPerRow; col++)
                    {
                        int offset = map[rowBase + col];
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
    /// MZ-800 640×200 bitmap renderer. Pixel code = <c>a | b&lt;&lt;1</c>,
    /// indexing the row's lookup in <paramref name="rowLuts"/>: 1-colour
    /// passes plane I (Frame A) or III (Frame B) as
    /// <paramref name="planeA"/>; 4-colour passes I and III (tech-ref
    /// p. 22).
    ///
    /// Each plane packs 80 bytes per raster across its two 8 KB halves
    /// (tech-ref p. 15): the CRTC fetches two bytes per display address
    /// DA — the even screen byte from offset DA, the odd one from
    /// $2000 + DA. DA goes through the same scroll map as 320 mode
    /// (<see cref="AddressMap"/>). LSB-first pixels.
    /// </summary>
    public void RenderPlanes640(byte[]? planeA, byte[]? planeB, int[][] rowLuts, ScrollRegs scroll)
    {
        planeA ??= NoPlane;
        planeB ??= NoPlane;
        int[] map = AddressMap(scroll);

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
                    int rowBase = y * (bytesPerRow / 2);
                    int* rowPix = pix + y * stride;
                    int[] lut = rowLuts[y];
                    for (int c = 0; c < bytesPerRow; c++)
                    {
                        int da = map[rowBase + (c >> 1)];
                        int planeAddr = (c & 1) == 0 ? da : oddBankBase + da;
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
