using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// The WinForms edge for core video: copies a <see cref="FrameBuffer"/>
/// into a 32bpp ARGB <see cref="Bitmap"/>. Both use the same 0xAARRGGBB
/// pixel layout, so it is a straight row copy.
/// </summary>
public static class FrameBufferBitmap
{
    /// <summary>Copy <paramref name="src"/> into <paramref name="dst"/>, which must be the same size and 32bpp ARGB.</summary>
    public static void CopyTo(FrameBuffer src, Bitmap dst)
    {
        var data = dst.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == src.Width * 4)
                Marshal.Copy(src.Pixels, 0, data.Scan0, src.Pixels.Length);
            else
                for (int y = 0; y < src.Height; y++)
                    Marshal.Copy(src.Pixels, y * src.Width, data.Scan0 + y * data.Stride, src.Width);
        }
        finally
        {
            dst.UnlockBits(data);
        }
    }

    /// <summary>A new 32bpp ARGB bitmap holding a copy of <paramref name="src"/>; the caller disposes it.</summary>
    public static Bitmap ToBitmap(FrameBuffer src)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        CopyTo(src, bmp);
        return bmp;
    }
}
