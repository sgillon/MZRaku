using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// The minimum surface a MainForm-style host needs to drive a Sharp MZ
/// machine — CPU, memory (via Z80Core's <see cref="IMemory"/> so the
/// debugger/memory viewer see machine-agnostic bytes), the debugger
/// controls, and the frame loop. Machine-specific hardware surfaces
/// (PPI, PIT, sound generator, joystick multiplex, video renderer,
/// keyboard, cassette, ROM key tables) stay off this interface — panes
/// that need them cast to the concrete machine class and gate their
/// menu items on <see cref="Kind"/>.
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
