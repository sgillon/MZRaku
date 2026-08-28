using System;
using System.Drawing;
using MZRaku.Hardware;
using Z80Core;

namespace MZRaku;

/// <summary>
/// Sharp MZ-800 machine class — v1.3.0 Phase 0 scaffolding stub.
/// The type exists so <see cref="MachineType.MZ800"/>, the
/// <c>--mz800</c> CLI flag, <c>[Roms.MZ800]</c> auto-population,
/// and the File → Machine menu all read as a complete three-machine
/// set right now. Construction throws with a friendly message —
/// MainForm intercepts an MZ-800 selection ahead of ever calling
/// <c>new MZ800()</c>, so a reached exception here means the
/// intercept was bypassed (bug).
///
/// See <c>_mz800info/MZ800-FEASIBILITY.md</c> for the plan and
/// <c>_mz800info/MZ800-RESEARCH-2026-08-29.md</c> for the
/// pre-Phase-1 research (tape traps identical to MZ-700's, 10×8
/// keyboard matrix, standard SN76489 PSG, standard .mzf format,
/// CRTC register map — all resolved).
///
/// Phase 1 replaces the throwing ctor with real wiring: Z80 CPU +
/// MZ800Memory (five-config bank switcher keyed off IN $E0-$E4) +
/// Mz800IoBus, loads MZ800.ROM at CPU $0000-$0FFF (MZ-700 monitor)
/// + $E000-$FFFF (MZ-800 IPL), and boots to the '*' prompt blind
/// (no video yet). Phase 2 lights up the MZ-700-mode renderer;
/// the remaining phases follow the feasibility doc.
/// </summary>
public sealed class MZ800 : MzMachineBase, IMachine
{
    public MZ800()
    {
        throw new NotImplementedException(
            "MZ-800 support is Phase 0 scaffolding as of v1.3.0. The " +
            "machine slot is reserved but the boot spike (Phase 1) " +
            "hasn't landed yet. MainForm intercepts this path with a " +
            "friendly fallback message ahead of construction; if you're " +
            "seeing this exception the intercept was bypassed (bug).");
    }

    public MachineType Kind => MachineType.MZ800;

    // IMachine surface — all throw. The ctor throws first so these
    // are unreachable in normal flow; the throws are defensive in
    // case a later refactor moves the ctor throw elsewhere.
    Z80Core.IMemory IMachine.Mem => throw NotReady();
    public Sound Sound => throw NotReady();
    public Bitmap? VideoFrame => throw NotReady();
    CassetteTrapBase IMachine.Cassette => throw NotReady();

    public void RunFrame() => throw NotReady();
    public void Reset() => throw NotReady();
    public void LoadRoms(string monitorRomPath, string? fontPath) => throw NotReady();
    public void AutoLoadBasic(string basicPath) => throw NotReady();
    public void AutoLoadCassette(string path, bool autoRun) => throw NotReady();
    public void DirectInjectCassette(string path) => throw NotReady();

    protected override void AccumulatePit(int cpuCycles) => throw NotReady();

    private static NotImplementedException NotReady() =>
        new("MZ-800 Phase 1 boot spike hasn't landed. This member should " +
            "never be reached — MZ800 construction throws first, and " +
            "MainForm intercepts the selection ahead of that.");
}
