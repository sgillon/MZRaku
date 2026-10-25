using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MZRaku.Core;

/// <summary>
/// Minimal PNG encoder for <c>--dump</c>: writes a <see cref="FrameBuffer"/>
/// as an 8-bit RGBA PNG (colour type 6, no filtering, zlib via
/// <see cref="ZLibStream"/>). Keeps the core free of System.Drawing;
/// the regression harness compares decoded pixels, so the exact bytes
/// don't need to match what GDI+ used to write.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(FrameBuffer frame, string path)
    {
        int w = frame.Width, h = frame.Height;
        using var fs = File.Create(path);
        fs.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        // ihdr[10..12] = compression, filter, interlace: all 0
        WriteChunk(fs, "IHDR", ihdr);

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + w * 4];   // row[0] = filter type 0 (None)
            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                for (int x = 0; x < w; x++)
                {
                    int p = frame.Pixels[src + x];   // 0xAARRGGBB
                    int o = 1 + x * 4;
                    row[o]     = (byte)(p >> 16);
                    row[o + 1] = (byte)(p >> 8);
                    row[o + 2] = (byte)p;
                    row[o + 3] = (byte)(p >> 24);
                }
                z.Write(row);
            }
        }
        WriteChunk(fs, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
