namespace MZRaku.Hardware;

/// <summary>
/// Which Sharp MZ machine MZRaku is currently emulating. The default is
/// MZ700 (the original target); the MZ80A variant was added in a later
/// release. Selected at startup via <c>--mz80a</c> / <c>--mz700</c> CLI
/// flags or persisted via the <c>[Machine] Type=</c> setting; a change
/// via <c>File → Machine → …</c> writes the INI and restarts.
/// </summary>
public enum MachineType
{
    MZ700,
    MZ80A,
}
