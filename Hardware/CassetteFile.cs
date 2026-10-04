using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MZRaku.Hardware;

/// <summary>
/// File-level loader for cassette images. Reads the bytes that
/// <see cref="MzfImage.Parse"/> expects, transparently extracting the
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

    /// <summary>
    /// Every file on a cassette, in tape order — for machines that model
    /// a whole tape (MZ-800), where a program can LOAD the next file by
    /// name. A zip contributes all its image entries (alphabetical by
    /// entry name); a <c>.mzt</c> holds back-to-back header+data blocks;
    /// a <c>.mzf</c> / <c>.m12</c> is one file.
    /// </summary>
    public static List<MzfImage> ReadTape(string path)
    {
        var files = new List<MzfImage>();
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = new List<ZipArchiveEntry>();
            foreach (var entry in zip.Entries)
                if (HasImageExtension(entry.Name)) entries.Add(entry);
            entries.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (var entry in entries)
            {
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                AddBlocks(files, ms.ToArray(), entry.Name);
            }
            if (files.Count == 0)
                throw new InvalidDataException(
                    $"No .mzf/.m12/.mzt entry found in {Path.GetFileName(path)}.");
            return files;
        }
        AddBlocks(files, File.ReadAllBytes(path), path);
        return files;
    }

    private static void AddBlocks(List<MzfImage> files, byte[] bytes, string name)
    {
        if (!string.Equals(Path.GetExtension(name), ".mzt", StringComparison.OrdinalIgnoreCase))
        {
            files.Add(MzfImage.Parse(bytes));
            return;
        }
        int pos = 0;
        while (bytes.Length - pos >= 128)
        {
            var block = new byte[bytes.Length - pos];
            Array.Copy(bytes, pos, block, 0, block.Length);
            var img = MzfImage.Parse(block);
            files.Add(img);
            pos += 128 + img.Data.Length;
        }
    }

    private static bool HasImageExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return Array.Exists(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] ExtractFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? best = null;
        foreach (var entry in zip.Entries)
        {
            if (HasImageExtension(entry.Name)
                && (best == null || string.CompareOrdinal(entry.FullName, best.FullName) < 0))
                best = entry;
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
