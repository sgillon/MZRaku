using System;
using System.IO;

namespace MZRaku.Hardware;

/// <summary>
/// One parsed Sharp .mzf cassette image — the format Sharp used
/// across its 8-bit line (MZ-80K/A/B/700/800). Header is a fixed
/// 128 bytes: type byte, 16-char ASCII filename, 16-bit size / load
/// / execute addresses. Payload is <see cref="Size"/> bytes of raw
/// program data following the header.
///
/// The <see cref="Cassette"/> / <see cref="Mz80aCassette"/> classes
/// both consume this shape — MzfImage lives at the Hardware namespace
/// root rather than nested inside one of them so per-machine code
/// paths don't have to reach through a sibling machine's class to
/// name it (v1.2 audit F-001 pulled it out of Cassette for exactly
/// that reason).
/// </summary>
public sealed class MzfImage
{
    public byte[] Header = new byte[128];
    public byte[] Data = Array.Empty<byte>();
    public string Filename = "";
    public ushort Size;
    public ushort LoadAddr;
    public ushort ExecAddr;
    public byte Type;

    /// <summary>
    /// Parse a raw .mzf byte stream into an <see cref="MzfImage"/>.
    /// The 128-byte header carries type + 16-char ASCII name + 16-bit
    /// size / load / exec addresses. Payload follows immediately;
    /// truncated payloads (bytes.Length &lt; 128 + Size) are
    /// tolerated — the data array is sized to whatever's actually
    /// there. Files shorter than 128 bytes throw.
    ///
    /// MZF filenames are stored as plain ASCII (verified by
    /// inspection of multiple commercial images). Non-ASCII bytes —
    /// typically Japanese katakana on Sharp's original Japanese-
    /// language software — show as '?' from the ASCII encoding's
    /// default fallback.
    /// </summary>
    public static MzfImage Parse(byte[] bytes)
    {
        if (bytes.Length < 128) throw new InvalidDataException("MZF too short (<128 bytes)");
        var img = new MzfImage();
        Array.Copy(bytes, img.Header, 128);
        img.Type = img.Header[0];
        img.Size = (ushort)(img.Header[0x12] | (img.Header[0x13] << 8));
        img.LoadAddr = (ushort)(img.Header[0x14] | (img.Header[0x15] << 8));
        img.ExecAddr = (ushort)(img.Header[0x16] | (img.Header[0x17] << 8));
        int nameLen = 0;
        while (nameLen < 16 && img.Header[1 + nameLen] != 0x0D && img.Header[1 + nameLen] != 0x00) nameLen++;
        img.Filename = System.Text.Encoding.ASCII.GetString(img.Header, 1, nameLen);
        int dataLen = Math.Min(img.Size, Math.Max(0, bytes.Length - 128));
        img.Data = new byte[dataLen];
        if (dataLen > 0) Array.Copy(bytes, 128, img.Data, 0, dataLen);
        return img;
    }
}
