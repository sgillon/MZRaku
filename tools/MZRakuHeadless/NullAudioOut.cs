namespace MZRakuHeadless;

/// <summary>
/// An audio output that plays nothing. It always reports a full buffer,
/// so the sound feed thread just sleeps in its wait loop rather than
/// spinning. Dump audio doesn't come through here: the MZ-800 .wav and
/// the MZ-700 / MZ-80A .snd.txt are recorded by the core.
/// </summary>
internal sealed class NullAudioOut : IAudioOut
{
    public TimeSpan BufferedDuration => TimeSpan.MaxValue;

    public void AddSamples(byte[] data, int offset, int count) { }

    public void Reset() { }

    public void Dispose() { }
}
