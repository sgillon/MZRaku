namespace MZRaku.Hardware;

/// <summary>
/// Which Sharp MZ machine MZRaku is currently emulating. Default is
/// MZ700 (the original target); MZ80A was added in v1.1.0; MZ800 is
/// v1.3.0 in-progress — the enum slot + settings surface exist as
/// Phase 0 scaffolding, but the boot spike lands in Phase 1 (a
/// selection of MZ800 in Phase 0 falls back with a friendly message).
/// Selected at startup via <c>--mz700</c> / <c>--mz80a</c> /
/// <c>--mz800</c> CLI flags or persisted via the
/// <c>[Machine] DefaultMachine=</c> setting; a change via
/// <c>File → Machine → …</c> launches a fresh process with the
/// target's flag.
/// </summary>
public enum MachineType
{
    MZ700,
    MZ80A,
    MZ800,
}
