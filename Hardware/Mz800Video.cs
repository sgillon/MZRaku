using System.Drawing;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 video renderer — Phase 1 stub. <see cref="Frame"/> is null,
/// so <see cref="MZ800.IMachine.VideoFrame"/> also returns null and
/// MainForm's paint skips drawing until a real frame is available.
///
/// Phase 2 replaces this with a MZ-700-mode renderer (fork of
/// <see cref="Video"/> / <see cref="Mz80aVideo"/>) that draws
/// 40×25 char cells from Vram + Aram.
///
/// Phase 5 adds the MZ-800-mode bitmap renderer (320×200 or 640×200,
/// 4/16-color palette, planes I-IV, hardware scroll).
/// </summary>
public sealed class Mz800Video
{
    public Bitmap? Frame;
}
