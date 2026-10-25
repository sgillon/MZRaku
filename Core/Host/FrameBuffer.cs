namespace MZRaku.Core;

/// <summary>
/// A rendered image as plain 32-bit ARGB pixels (0xAARRGGBB, the same
/// layout as a WinForms <c>Format32bppArgb</c> bitmap), row-major with
/// no row padding: pixel (x, y) is <c>Pixels[y * Width + x]</c>.
///
/// The video renderers draw into one of these instead of a
/// <c>System.Drawing.Bitmap</c>, so the core needs no graphics
/// library. Each front end converts at its edge: WinForms copies it
/// into a <c>Bitmap</c> (<c>FrameBufferBitmap</c>), and
/// <c>PngWriter</c> writes it straight to a PNG for <c>--dump</c>.
/// </summary>
public sealed class FrameBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public FrameBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new int[width * height];
    }
}
