using System;
using System.IO;
using MZRaku.Hardware;
using Z80Core;

namespace MZRaku;

/// <summary>
/// Assembled Sharp MZ-800 machine: Z80 (3.547 MHz) + 8255 PPI + 8253
/// PIT + 64 KiB DRAM + 16 KiB combined ROM + memory-banking (six
/// configs per mode, keyed off IN $E0-$E5) + dual-mode I/O layout.
///
/// Phase 1 status (v1.3.0, 2026-08-29): boot spike. This class boots
/// the 1Z-013B monitor blind — CPU starts at $0000 (JP $E800), IPL
/// runs from ROM at CPU $E800 (file offset $2800), IPL flips to
/// MZ-700 mode via OUT ($CE),A, banks the CG-ROM in/out to copy PCG
/// data, then MZ-700 monitor at CPU $0000 takes over and writes the
/// '*' prompt to VRAM at $D000. All that is verifiable via the
/// debugger + memory viewer; no video renderer yet (Phase 2).
///
/// The peripheral classes are placeholders sized to Phase 1:
/// <see cref="Mz800Video"/> returns null so MainForm paints nothing,
/// <see cref="Mz800Keyboard"/> returns $FF from every row (no keys),
/// <see cref="Mz800Cassette"/> traps but nothing queues an image,
/// <see cref="Sound"/> is present but disabled (Enabled=false; no
/// PSG path yet — that arrives in Phase 6).
///
/// Roadmap: Phase 2 lights up the MZ-700-mode video renderer, Phase 3
/// wires the keyboard (matrix reference + PC-key mapping), Phase 4
/// completes the cassette + BASIC LOAD flow, Phase 5 adds MZ-800-mode
/// bitmap graphics + palette, Phase 6 replaces the PIT beeper with
/// the SN76489 PSG, Phase 7 wires the Z80 PIO + joystick.
/// </summary>
public sealed class MZ800 : MzMachineBase, IMachine
{
    // Cpu is inherited from MzMachineBase (v1.2 audit F-060).
    public MZ800Memory Mem { get; } = new();
    public Ppi8255 Ppi = new();
    public Pit8253 Pit = new();
    public Mz800IoBus Io = new();
    public Mz800Video Video { get; } = new();
    public Mz800Keyboard Keyboard = new();
    public Mz800Cassette Cassette { get; } = new();
    // MZ-800 sound arrives in Phase 6 (SN76489 PSG). Phase 1 wires
    // the MZ-700-style Sound class as a placeholder — the $E008 D0
    // gate write in MZ-700 mode flows through it — but Enabled=false
    // so nothing ever actually plays.
    public Sound Sound { get; } = new();

    public MachineType Kind => MachineType.MZ800;
    Z80Core.IMemory IMachine.Mem => Mem;
    CassetteTrapBase IMachine.Cassette => Cassette;
    System.Drawing.Bitmap? IMachine.VideoFrame => Video.Frame;

    // MZ-800 CPU runs at 3.547 MHz (17.734 MHz crystal ÷ 5, per
    // tech-ref p. 9). Matches MZ-700 exactly; MZ-80A is the slower
    // one at 2 MHz.
    public const double CpuClockHz = 3_546_900.0;
    public const int FramesPerSecond = 60;
    public const int CyclesPerFrame = (int)(CpuClockHz / FramesPerSecond);

    // PIT input clocks — same values as MZ-700 for Phase 1. Refine
    // when Phase 6 PSG wiring lands and we start measuring timing
    // against sample MC games.
    public const double PitC0InputHz = 895_000.0;
    public const double PitC2InputHz = 15_700.0;

    private int _pitC0Accum;
    private int _pitC1Accum;
    private int _tempoAccum;
    // Same CyclesPerTempoToggle as MZ-700 (fits 50 Hz TEMP signal at
    // 3.5469 MHz CPU clock). The MZ-700 monitor at $02DB polls $E008
    // bit 0 for this signal to advance out of the boot beep-wait loop;
    // without the toggle, the CPU spins there forever. Confirmed by
    // Phase 1 boot spike (2026-08-29): stuck at PC=$02DB until this
    // toggle was added.
    private const int CyclesPerTempoToggle = 35469;

    public MZ800()
    {
        Cpu.Mem = Mem;
        Cpu.Io = Io;
        Io.Ppi = Ppi;
        Io.Pit = Pit;
        Io.Memory = Mem;
        Io.Sound = Sound;
        Io.Cpu = Cpu;
        Mem.IoBus = Io;
        Mem.Cpu = Cpu;
        Ppi.Keyboard = Keyboard;

        // Cassette needs Memory + CPU for trap injection. PreStep
        // watches the 1Z-013B tape entry-point vectors at $0027 /
        // $002A (same addresses as MZ-80A per research; MZ-700 traps
        // at implementation addresses instead but that's a historical
        // quirk, not a hardware requirement).
        Cassette.Memory = Mem;
        Cassette.Cpu = Cpu;
        Cpu.PreStep = Cassette.OnPreStep;

        // Sound present but disabled — the $E008 D0 writes in MZ-700
        // mode flow to Sound.HardGate through Mz800IoBus (mirroring
        // MZ-700's behaviour) but Enabled=false silences the output
        // pipeline. Phase 6 replaces this whole path with the PSG.
        Sound.Enabled = false;

        // Timer interrupt from PIT counter 2 — mirrors MZ-700. INTMSK
        // bit is PortC bit 2 (== INTMSK meaning D2=1 means interrupts
        // ENABLED on MZ-700; MZ-800 keeps the MZ-700 convention when
        // in MZ-700 mode).
        Pit.Counter2Out += _ =>
        {
            if (Ppi.InterruptMask) Cpu.RequestInterrupt();
        };
    }

    public void LoadRoms(string monitorRomPath, string? fontPath)
    {
        // MZ800.ROM is a single 16 KB file combining MZ-700 monitor +
        // CG-ROM + MZ-800 IPL + monitor + BASIC-IOCS. Font parameter is
        // ignored — the CG lives inside the combined ROM at offset
        // $1000-$1FFF, and Phase 2's renderer will slice it out from
        // Mem.Rom directly rather than reading a separate file.
        Mem.LoadRom(File.ReadAllBytes(monitorRomPath));
    }

    public void Reset()
    {
        Cpu.Reset();
        Cpu.IM = 1;
        // Restore power-on bank state — MZ-800 mode, config (a). ROM
        // is at $0000 (MZ-700 monitor) and $E000 (MZ-800 IPL); the
        // reset vector at $0000 is `JP $E800`, which jumps into the
        // IPL and starts the mode-selection dance.
        Mem.ResetBankState();
        // Clear cassette state so a stale Pending image doesn't get
        // served to the freshly-booting monitor's tape traps.
        Cassette.ResetTrapState();
        // Clear VRAM buffers so the display starts blank (once the
        // renderer arrives). Sound gates default off.
        Array.Clear(Mem.Vram, 0, Mem.Vram.Length);
        Array.Clear(Mem.Aram, 0, Mem.Aram.Length);
        Sound.HardGate = false;
    }

    /// <summary>
    /// Execute one video frame's worth of CPU + peripheral time.
    /// Mirrors <see cref="MZ700.RunFrame"/> and
    /// <see cref="MZ80A.RunFrame"/> in shape — see those methods'
    /// doc comments for the rationale.
    /// </summary>
    public void RunFrame()
    {
        if (Paused && !_stepFrameRequested)
        {
            // No renderer yet — nothing to redraw when paused. Phase 2
            // adds the render call here so the display stays live
            // during debug.
            return;
        }
        bool stepFrame = _stepFrameRequested;
        _stepFrameRequested = false;

        Ppi.SetVBlank(false);
        int cyclesThisFrame = 0;
        int cyclesToVBlank = (int)(CyclesPerFrame * 0.85);

        Cpu.BreakpointTripped = false;
        bool tripped = false;

        while (cyclesThisFrame < cyclesToVBlank)
        {
            int cyc = Cpu.Step();
            if (Cpu.BreakpointTripped) { tripped = true; break; }
            cyclesThisFrame += cyc;
            AccumulatePit(cyc);
        }

        if (!tripped)
        {
            Ppi.SetVBlank(true);
            while (cyclesThisFrame < CyclesPerFrame)
            {
                int cyc = Cpu.Step();
                if (Cpu.BreakpointTripped) { tripped = true; break; }
                cyclesThisFrame += cyc;
                AccumulatePit(cyc);
            }
        }

        if (tripped || stepFrame) Paused = true;

        // Video.Render call arrives in Phase 2.
    }

    protected override void AccumulatePit(int cpuCycles)
    {
        // Same rates as MZ-700 — the MZ-800 uses the same 8253 layout
        // in MZ-700 mode. Phase 6 revisits when the PSG needs its
        // own timer path.
        _pitC0Accum += cpuCycles * 895;   // 895/3547 ≈ 0.2523 → 895 kHz
        int c0 = _pitC0Accum / 3547;
        _pitC0Accum -= c0 * 3547;

        _pitC1Accum += cpuCycles * 157;   // 157/35469 ≈ 0.00443 → 15.7 kHz
        int c1 = _pitC1Accum / 35469;
        _pitC1Accum -= c1 * 35469;

        Pit.Tick(c0, c1);

        // TEMP toggle — same shape as MZ-700's tempo bit. The MZ-800's
        // MZ-700-mode monitor polls $E008 bit 0 to time boot beep and
        // MUSIC-note duration; without this the monitor's beep-wait
        // loop at $02DB hangs.
        _tempoAccum += cpuCycles;
        while (_tempoAccum >= CyclesPerTempoToggle)
        {
            _tempoAccum -= CyclesPerTempoToggle;
            Ppi.TempoBit = !Ppi.TempoBit;
        }
    }

    public void AutoLoadBasic(string basicPath)
    {
        if (!File.Exists(basicPath))
            throw new FileNotFoundException("BASIC cassette image not found", basicPath);
        var img = MzfImage.Parse(File.ReadAllBytes(basicPath));
        // DirectInject shortcut, same as MZ-80A. Phase 4 refines the
        // BASIC boot dance if 1Z-016's IPL turns out to need
        // pointer-fixup like S-BASIC does on MZ-700.
        Cassette.DirectInject(img, jumpExec: true);
    }

    public void AutoLoadCassette(string path, bool autoRun)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Cassette image not found", path);
        var img = MzfImage.Parse(CassetteFile.ReadBytes(path));
        // Same direct-inject shortcut as MZ-80A — the trap path lands
        // in Phase 4 when the keyboard auto-typer for MZ-800 arrives.
        Cassette.DirectInject(img, jumpExec: ShouldJumpExecForType(img.Type));
    }

    public void DirectInjectCassette(string path)
    {
        var img = MzfImage.Parse(CassetteFile.ReadBytes(path));
        Cassette.DirectInject(img, jumpExec: ShouldJumpExecForType(img.Type));
    }

    /// <summary>
    /// Only machine-code (type 01) images have a monitor-callable exec
    /// address in their .mzf header. BASIC text (02), BASIC data (03),
    /// and relocatable (05) images typically carry exec=$0000, which
    /// is the reset vector — jumping there would wipe the loaded
    /// program. Same rationale as MZ-80A / MZ-700.
    /// </summary>
    private static bool ShouldJumpExecForType(byte type) => type == 0x01;
}
