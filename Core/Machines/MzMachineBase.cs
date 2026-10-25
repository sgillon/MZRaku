using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Shared base for the two Sharp machine classes (<see cref="MZ700"/>
/// and <see cref="MZ80A"/>): the Z80 CPU (same type on both
/// machines), the debugger-control surface (Paused +
/// Pause/Resume/StepInstruction/StepFrame), and the shared
/// pause/step state machine both use identically.
///
/// The subclasses still implement <see cref="IMachine"/> directly —
/// this base is inheritance for the parts they genuinely share, not
/// a substitute for the interface. Members that legitimately differ
/// per machine (Mem type, Kind, RunFrame, Reset, LoadRoms, the
/// autoload/direct-inject cassette entry points) stay on the
/// subclass; the shared debugger controls no longer need to be
/// duplicated verbatim.
///
/// v1.2 audit F-060 extracted this. The two Pause/Resume/Step
/// methods drifted before (see F-026 for a related pattern) —
/// having one implementation closes that vector.
/// </summary>
public abstract class MzMachineBase
{
    /// <summary>
    /// The Z80 CPU. Same type both machines use, so it lives on
    /// the base. Subclasses wire its Mem/Io/PreStep in their
    /// constructors.
    /// </summary>
    public Z80Cpu Cpu { get; } = new();

    /// <summary>
    /// When true, <see cref="IMachine.RunFrame"/> renders the
    /// display but does not step the CPU. The debugger toggles
    /// this to pause / resume.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// One-shot the "step frame" debugger action sets, honoured by
    /// the next <see cref="IMachine.RunFrame"/> call even though
    /// the machine is paused. Subclasses read this at the top of
    /// their frame loop and clear it once consumed.
    /// </summary>
    protected bool _stepFrameRequested;

    /// <summary>Freeze the CPU; RunFrame keeps rendering but won't step.</summary>
    public void Pause() => Paused = true;

    /// <summary>
    /// Un-freeze the CPU. Arms a one-shot breakpoint bypass so
    /// execution can move off an instruction the debugger is
    /// parked on.
    /// </summary>
    public void Resume()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        Paused = false;
    }

    /// <summary>
    /// Execute exactly one Z80 instruction, with the PIT/tempo
    /// bookkeeping <see cref="IMachine.RunFrame"/>'s loop normally
    /// does so timing devices stay coherent. Leaves the machine
    /// paused.
    /// </summary>
    public void StepInstruction()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        int cyc = Cpu.Step();
        AccumulatePit(cyc);
        Paused = true;
    }

    /// <summary>
    /// Run one full frame's worth of cycles, then re-pause.
    /// Honoured by the next RunFrame call even though the machine
    /// is paused.
    /// </summary>
    public void StepFrame()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        _stepFrameRequested = true;
    }

    /// <summary>
    /// Called from <see cref="StepInstruction"/> (and each
    /// subclass's RunFrame loop) with the cycle count from the
    /// last <see cref="Z80Cpu.Step"/>. Per-machine PIT clock
    /// ratios are baked in on the subclass side.
    /// </summary>
    protected abstract void AccumulatePit(int cpuCycles);
}
