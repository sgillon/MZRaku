using System;

namespace MZRaku.Core;

/// <summary>
/// The PCM format and buffering <see cref="Sound"/> plays at, so a front
/// end can open a matching <see cref="IAudioOut"/>. Every chunk
/// <see cref="Sound"/> submits is at most <see cref="ChunkBytes"/> long,
/// and it keeps at most <see cref="ChunkCount"/> chunks queued.
/// </summary>
public readonly record struct AudioOutFormat(
    int SampleRate, int BitsPerSample, int Channels, int ChunkBytes, int ChunkCount);

/// <summary>
/// Where <see cref="Sound"/> sends its PCM (v1.4 design decision D4).
/// The front end supplies one to <see cref="Sound.Start"/>; on Windows
/// that is the app's <c>WinmmWaveOut</c>. All members are called from the
/// sound feed thread except <see cref="Reset"/>, which the emulation
/// side calls on mute, and <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IAudioOut : IDisposable
{
    /// <summary>
    /// Audio submitted but not yet played. The feed thread paces itself
    /// on this, so it should track the device's real playback position.
    /// </summary>
    TimeSpan BufferedDuration { get; }

    /// <summary>
    /// Submit a chunk of PCM. The bytes are copied, so the caller can
    /// reuse its buffer as soon as this returns. A chunk that can't be
    /// queued is dropped.
    /// </summary>
    void AddSamples(byte[] data, int offset, int count);

    /// <summary>
    /// Stop playback at once and discard anything queued. Output stays
    /// open, so the next <see cref="AddSamples"/> plays straight away.
    /// </summary>
    void Reset();
}
