using System.Drawing;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// The surface a MainForm-style host needs to drive a Sharp MZ
/// machine — CPU, memory, the debugger controls, the frame loop,
/// plus the small subset of hardware that BOTH machines have AND
/// MainForm accesses uniformly (sound output, the rendered video
/// frame, cassette-trap state). Machine-specific hardware
/// (PPI, PIT, joystick multiplex, ROM key tables) stays off this
/// interface — panes that need those cast to the concrete class
/// and gate their menu items on <see cref="Kind"/>.
///
/// v1.2 audit F-061 widened this from CPU/Mem/debugger-only to
/// include the three converged surfaces above, so MainForm's
/// _machine/_mz80a-null branching (F-056) can collapse against
/// one interface.
/// </summary>
public interface IMachine
{
    /// <summary>Which machine is behind this interface.</summary>
    MachineType Kind { get; }

    /// <summary>The Z80 CPU — always Z80Core.Z80Cpu regardless of machine.</summary>
    Z80Cpu Cpu { get; }

    /// <summary>
    /// The machine's memory as seen by the CPU. Same interface Z80Core
    /// itself uses, so the debugger's disassembly and the memory viewer
    /// work identically across machines.
    /// </summary>
    IMemory Mem { get; }

    /// <summary>
    /// The machine's speaker output. Both machines drive the same
    /// <see cref="Hardware.Sound"/> class — MZ-700 uses its
    /// two-gate NAND (PC3 + $E008 D0), MZ-80A pins Enabled=true
    /// and uses only the hard gate. MainForm's mute-on-pause /
    /// Dispose-on-close paths run through this getter.
    /// </summary>
    Sound Sound { get; }

    /// <summary>
    /// The most recently rendered video frame. Null if the
    /// machine hasn't rendered yet (first frame not drawn). Both
    /// machines render into a <see cref="Bitmap"/> at their native
    /// 320×200 (MZ-700) or 320×200 (MZ-80A) resolution — MainForm
    /// paints whichever is non-null.
    /// </summary>
    Bitmap? VideoFrame { get; }

    /// <summary>
    /// The machine's cassette-trap state. Exposed at the shared
    /// <see cref="CassetteTrapBase"/> level so MainForm's TAPE
    /// activity chip + auto-load orchestrator can poll Pending +
    /// HeaderTrapHits + DataTrapHits + DataDelivered uniformly.
    /// MZ-700-specific fields (WriteTapeTrapHits, BreakWaitTrapHits,
    /// SAVE-tape machinery) live on the concrete
    /// <see cref="Hardware.Cassette"/> class and callers that need
    /// them cast.
    /// </summary>
    CassetteTrapBase Cassette { get; }

    /// <summary>
    /// When true, <see cref="RunFrame"/> renders the display but does
    /// not step the CPU. The debugger toggles this to pause / resume.
    /// </summary>
    bool Paused { get; set; }

    /// <summary>Advance one video frame's worth of CPU + peripheral time.</summary>
    void RunFrame();

    /// <summary>Reset the CPU and hardware to power-on state.</summary>
    void Reset();

    /// <summary>Set <see cref="Paused"/> = true.</summary>
    void Pause();

    /// <summary>Clear <see cref="Paused"/> and arm a one-shot breakpoint bypass.</summary>
    void Resume();

    /// <summary>Execute exactly one Z80 instruction; leave the machine paused.</summary>
    void StepInstruction();

    /// <summary>Run one frame's worth of cycles, then re-pause.</summary>
    void StepFrame();

    /// <summary>Load the monitor ROM (required) and character-generator ROM (optional path).</summary>
    void LoadRoms(string monitorRomPath, string? fontPath);

    /// <summary>Direct-inject a BASIC .mzf cassette image and jump to its exec entry.</summary>
    void AutoLoadBasic(string basicPath);

    /// <summary>Queue a cassette image for LOAD dispatch; optionally auto-type L+Enter.</summary>
    void AutoLoadCassette(string path, bool autoRun);

    /// <summary>Parse a cassette image and direct-inject it (bypassing the monitor's LOAD).</summary>
    void DirectInjectCassette(string path);
}
