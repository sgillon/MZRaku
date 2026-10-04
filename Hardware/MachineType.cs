namespace MZRaku.Hardware;

/// <summary>
/// Which Sharp MZ machine MZRaku is currently emulating. Default is
/// MZ700 (the original target); MZ80A was added in v1.1.0, MZ800 in
/// v1.3.0. Selected at startup via <c>--mz700</c> / <c>--mz80a</c> /
/// <c>--mz800</c> CLI flags or persisted via the
/// <c>[Machine] DefaultMachine=</c> setting; a change via
/// <c>System → Machine → …</c> launches a fresh process with the
/// target's flag.
/// </summary>
public enum MachineType
{
    MZ700,
    MZ80A,
    MZ800,
}
