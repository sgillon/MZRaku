using System;
using System.IO;
using System.IO.Compression;

namespace MZ700Emul.Hardware;

/// <summary>
/// File-level loader for cassette images. Reads the bytes that
/// <see cref="Cassette.Parse"/> expects, transparently extracting the
/// first <c>.mzf</c>/<c>.m12</c>/<c>.mzt</c> entry when handed a zip
/// archive. Multi-cassette archives pick the first match (alphabetical
/// by entry name) — covers the common case of an .mzf bundled with a
/// readme without forcing the user to unzip.
/// </summary>
public static class CassetteFile
{
    private static readonly string[] Extensions = { ".mzf", ".m12", ".mzt" };

    public static byte[] ReadBytes(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return ExtractFromZip(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] ExtractFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? best = null;
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name);
            if (Array.Exists(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                if (best == null || string.CompareOrdinal(entry.FullName, best.FullName) < 0)
                    best = entry;
            }
        }
        if (best == null)
            throw new InvalidDataException(
                $"No .mzf/.m12/.mzt entry found in {Path.GetFileName(zipPath)}.");
        using var s = best.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
