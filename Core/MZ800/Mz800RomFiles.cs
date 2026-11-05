using System;
using System.IO;

namespace MZRaku.Core;

/// <summary>
/// The MZ-800's 16 KiB ROM comes as one file or three:
/// <list type="bullet">
/// <item><c>MZ800.ROM</c>, 16 KiB, the whole ROM;</item>
/// <item>EmuZ-800's layout: <c>MZ700IPL.ROM</c> (4 KiB, the MZ-700
/// monitor), <c>FONT.ROM</c> (4 KiB, the character generator) and
/// <c>MZ800IPL.ROM</c> (8 KiB, the MZ-800 IPL and monitor).</item>
/// </list>
/// The three parts joined in that order are byte-identical to
/// <c>MZ800.ROM</c>. The form is told by the size of the first file,
/// whatever it is called.
/// </summary>
public static class Mz800RomFiles
{
    /// <summary>The one-file form.</summary>
    public const string Combined = "MZ800.ROM";
    /// <summary>The first of the three parts: the MZ-700 monitor.</summary>
    public const string Ipl700 = "MZ700IPL.ROM";
    /// <summary>The second part: the character generator.</summary>
    public const string Font = "FONT.ROM";
    /// <summary>The third part: the MZ-800 IPL and monitor.</summary>
    public const string Ipl800 = "MZ800IPL.ROM";

    /// <summary>Byte sizes of the one file and the three parts.</summary>
    public const int CombinedSize = 0x4000;
    public const int Ipl700Size = 0x1000;
    public const int FontSize = 0x1000;
    public const int Ipl800Size = 0x2000;

    /// <summary>
    /// True when <paramref name="path"/> is an existing 4 KiB file,
    /// the first of the three parts. False for <c>MZ800.ROM</c>, a
    /// missing file or any other size.
    /// </summary>
    public static bool IsSplit(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        return new FileInfo(path).Length == Ipl700Size;
    }

    /// <summary>
    /// Why <paramref name="path"/> can't be the part or file it was
    /// given as, or null if it can. <paramref name="size"/> is the
    /// byte count that part must have.
    /// </summary>
    public static string? SizeProblem(string path, int size)
    {
        long length = new FileInfo(path).Length;
        return length == size ? null : $"{Path.GetFileName(path)} is {length} bytes, not {size}.";
    }

    /// <summary>
    /// Read the ROM in either form. <paramref name="monitorPath"/> is
    /// <c>MZ800.ROM</c> or <c>MZ700IPL.ROM</c>; the other two paths are
    /// used only with the second. Throws, with a message for the user,
    /// if a part is missing or the wrong size. <paramref name="iniPath"/>
    /// is named in that message.
    /// </summary>
    public static byte[] Read(string monitorPath, string? fontPath, string? ipl800Path, string iniPath)
    {
        var first = File.ReadAllBytes(monitorPath);
        if (first.Length == CombinedSize) return first;
        if (first.Length != Ipl700Size)
            throw new InvalidDataException(
                $"{Path.GetFileName(monitorPath)} is {first.Length} bytes, so it is neither form of the MZ-800 ROM.\n\n" +
                $"Monitor= must name {Combined} ({CombinedSize} bytes) or {Ipl700} ({Ipl700Size} bytes, " +
                $"used with {Font} and {Ipl800}).\n\nFile: {monitorPath}");

        var rom = new byte[CombinedSize];
        first.CopyTo(rom, 0);
        Part(fontPath, Font, "Font", FontSize, iniPath).CopyTo(rom, Ipl700Size);
        Part(ipl800Path, Ipl800, "Ipl800", Ipl800Size, iniPath).CopyTo(rom, Ipl700Size + FontSize);
        return rom;
    }

    private static byte[] Part(string? path, string name, string key, int size, string iniPath)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var configured = string.IsNullOrEmpty(path) ? "(none configured)" : path;
            throw new FileNotFoundException(
                $"{name} not found.\n\n" +
                $"Monitor= names {Ipl700}, the first of the MZ-800 ROM's three files; " +
                $"{Font} and {Ipl800} are needed with it.\n\n" +
                $"Configured path: {configured}\n\n" +
                $"Place {name} next to {Ipl700}, or set [Roms.MZ800] {key}= in {iniPath}. " +
                $"Alternatively use the one-file form, {Combined}.");
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != size)
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} is {bytes.Length} bytes, but the MZ-800's {name} is {size} bytes.\n\n" +
                $"File: {path}");
        return bytes;
    }
}
