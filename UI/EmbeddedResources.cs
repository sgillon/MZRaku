using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MZRaku;

/// <summary>
/// Shared loaders for the two embedded resources the WinForms UI
/// consumes — the app icon and the logo. MainForm and AboutForm each
/// used to carry a byte-identical copy of these; keeping the pattern
/// in one place stops the icon-and-a-copy from drifting.
///
/// Errors swallow to null on purpose: neither caller has anywhere
/// useful to surface a "resource missing" message, and the fallback
/// (system-default icon, no logo pane) is acceptable.
/// </summary>
internal static class EmbeddedResources
{
    /// <summary>
    /// Returns the app icon from embedded resources, or null if it
    /// isn't packaged (unit-test builds, unusual publish shapes) or a
    /// stream error prevents the read.
    /// </summary>
    public static Icon? LoadIcon(string filenameSuffix = "MZRaku.ico")
    {
        try
        {
            var asm = typeof(EmbeddedResources).Assembly;
            var name = FindResource(asm, filenameSuffix);
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns an embedded image (e.g. the About-dialog logo), or null
    /// if the resource is absent or unreadable. The stream is copied
    /// into a MemoryStream before Image.FromStream reads it —
    /// Image.FromStream keeps the underlying stream alive lazily and
    /// disposing it under the Image's feet corrupts the render.
    /// </summary>
    public static Image? LoadImage(string filenameSuffix)
    {
        try
        {
            var asm = typeof(EmbeddedResources).Assembly;
            var name = FindResource(asm, filenameSuffix);
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            var mem = new MemoryStream();
            s.CopyTo(mem);
            mem.Position = 0;
            return Image.FromStream(mem);
        }
        catch { return null; }
    }

    private static string? FindResource(Assembly asm, string filenameSuffix) =>
        asm.GetManifestResourceNames()
           .FirstOrDefault(n => n.EndsWith(filenameSuffix, StringComparison.OrdinalIgnoreCase));
}
