using System;
using System.IO;
using MZRaku.Hardware;
using Z80Core;

namespace MZRaku;

/// <summary>
/// Assembled Sharp MZ-80A machine: Z80 (2 MHz) + 8255 PPI + 8253 PIT
/// + 48 KiB RAM + memory-swap-capable ROM layout + monochrome text VRAM.
///
/// Phase 1 status: this class boots the SA-1510 monitor blind — CPU
/// steps against ROM at $0000, MMIO dispatches to a functional Ppi/Pit
/// pair, memory swap works, VRAM writes land in the buffer (but aren't
/// rendered yet). Video, keyboard, sound, and cassette are stubbed and
/// come online in Phases 2/3/4/5. Debugger and memory viewer already
/// work via the shared IMachine interface — set a breakpoint at $0100
/// in the SA-1510 disassembly and it will hit.
/// </summary>
public sealed class MZ80A : IMachine
{
    public Z80Cpu Cpu { get; } = new();
    public MZ80AMemory Mem { get; } = new();
    public Ppi8255 Ppi = new();
    public Pit8253 Pit = new();
    public Mz80aIoBus Io = new();
    public Mz80aVideo Video = new();
    public Mz80aKeyboard Keyboard = new();
    public Mz80aCassette Cassette = new();
    // MZ-80A sound path is simpler than MZ-700's dual-gate NAND — a
    // single hard gate at $E008 D0 in front of the audio amplifier,
    // fed by PIT counter 1's OUT (per Owner's Manual p.163 text). We
    // reuse the MZ-700 Sound class with Enabled pinned true (no soft
    // gate exists on MZ-80A) and a different input-clock rate.
    public Sound Sound = new();

    public MachineType Kind => MachineType.MZ80A;
    Z80Core.IMemory IMachine.Mem => Mem;

    // MZ-80A crystal is 8 MHz divided down; CPU runs at 2 MHz per the
    // Owner's Manual §3.1 text. PIT input clock rates are best-guess
    // for Phase 1 (exact rates from the schematic section pinned down
    // in Phase 5 when sound comes online). For the blind boot only
    // the CPU rate matters — the ROM's inner loops depend on how many
    // Z80 cycles per frame we allow.
    public const double CpuClockHz = 2_000_000.0;
    public const int FramesPerSecond = 60;
    public const int CyclesPerFrame = (int)(CpuClockHz / FramesPerSecond);

    // Best-guess PIT input clock for counter 0 (audio path per the
    // block diagram on p.162). 895 kHz is the MZ-700 value; tune for
    // MZ-80A once we can stopwatch a MUSIC note against a known
    // reference in Phase 5.
    public const double PitC0InputHz = 895_000.0;

    public bool Paused { get; set; }
    private bool _stepFrameRequested;

    private int _pitC0Accum;
    private int _pitC1Accum;

    public MZ80A()
    {
        Cpu.Mem = Mem;
        Cpu.Io = Io;
        Io.Ppi = Ppi;
        Io.Pit = Pit;
        Io.Memory = Mem;
        Mem.IoBus = Io;
        Mem.Cpu = Cpu;
        // PPI Port B reads pull the strobed row bits out of the
        // MZ-80A keyboard shim. Assigned via the shared
        // IKeyboardMatrix interface so Ppi8255 stays machine-agnostic.
        Ppi.Keyboard = Keyboard;

        // Cassette needs memory + CPU for trap injection; PreStep
        // watches SA-1510's RDINF ($0027) and RDDAT ($002A) entry
        // points and delivers the queued .mzf image directly.
        Cassette.Memory = Mem;
        Cassette.Cpu = Cpu;
        Cpu.PreStep = Cassette.OnPreStep;

        // Sound: MZ-80A has no soft gate (no MZ-700-style PC3 stage),
        // so Enabled is pinned. The hard gate at $E008 D0 is wired via
        // Mz80aIoBus. Best-guess input clock 31.5 kHz for C1 (matches
        // Owner's Manual p.162 block diagram's "31.5 kHz" annotation
        // on the 8253 input path); tune when a MUSIC-tempo reference
        // is available.
        Sound.Enabled = true;
        Sound.InputClockHz = 31_500.0;
        Io.Sound = Sound;

        // Timer interrupt from PIT counter 2. On MZ-80A, $E002 D2 is
        // documented in Owner's Manual Table 3.1 as "Masking of timer
        // interrupt" — natural reading is D2=1 masks (disables). This
        // is the OPPOSITE convention to MZ-700 where the same bit
        // (PC2) is "INTMSK" with D2=1 meaning enabled. So we read the
        // raw PortCOut bit and treat 0=not-masked=fire.
        Pit.Counter2Out += _ =>
        {
            bool notMasked = (Ppi.PortCOut & 0x04) == 0;
            if (notMasked) Cpu.RequestInterrupt();
        };
    }

    public void LoadRoms(string monitorRomPath, string? fontPath)
    {
        Mem.LoadRom(File.ReadAllBytes(monitorRomPath));
        if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
        {
            Video.LoadFont(File.ReadAllBytes(fontPath));
        }
    }

    public void Reset()
    {
        Cpu.Reset();
        Cpu.IM = 1;
        Mem.RomSwapped = false;
        // Clear VRAM so the screen starts blank rather than showing
        // whatever noise came out of the .NET Array allocator.
        Array.Clear(Mem.Vram, 0, Mem.Vram.Length);
    }

    /// <summary>
    /// Execute one video frame's worth of CPU + peripheral time.
    /// Mirrors MZ700.RunFrame in shape (visible/VBLANK split, PIT
    /// tick per Cpu.Step, breakpoint short-circuit) — see that
    /// method's doc comment for the rationale.
    /// </summary>
    public void RunFrame()
    {
        if (Paused && !_stepFrameRequested)
        {
            // Still rebuild the framebuffer so the debugger's memory
            // viewer and any external tools see a live-ish display
            // even while the CPU is paused. Same pattern as MZ700.
            RenderFrame();
            return;
        }
        bool stepFrame = _stepFrameRequested;
        _stepFrameRequested = false;

        // Advance any staged key-release countdowns so a PC press
        // that KeyDown'd and KeyUp'd within one 60 Hz host frame
        // stays asserted long enough for the ROM's scan to observe.
        Keyboard.TickFrame();

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

        RenderFrame();

        // Feed the sound generator with PIT counter 1's current reload.
        // When the counter isn't actively running (e.g. between control
        // words and LSB/MSB loads), zero it so the speaker stays silent.
        Sound.SetReload(Pit.Counters[1].Running ? Pit.Counters[1].Reload : 0);
    }

    private void RenderFrame()
    {
        // Copy the two side-effect-latched settings from the IoBus over
        // to the renderer just before drawing. Keeping them out of the
        // hot MMIO path (they change infrequently) keeps the bus code
        // small — Mz80aIoBus doesn't need to know Mz80aVideo exists.
        Video.Reverse = Io.ReverseVideo;
        Video.ScrollOffset = Io.ScrollOffset;
        Video.Render(Mem.Vram);
    }

    public void Pause() => Paused = true;

    public void Resume()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        Paused = false;
    }

    public void StepInstruction()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        int cyc = Cpu.Step();
        AccumulatePit(cyc);
        Paused = true;
    }

    public void StepFrame()
    {
        Cpu.IgnoreBreakpointOnce = true;
        Cpu.BreakpointTripped = false;
        _stepFrameRequested = true;
    }

    private void AccumulatePit(int cpuCycles)
    {
        // C0 (audio input) coarse rate 895 kHz — refined in Phase 5.
        _pitC0Accum += cpuCycles * 895;
        int c0 = _pitC0Accum / 2000;
        _pitC0Accum -= c0 * 2000;

        // C1 clocks the display timing tree — per Fig 3.1 on Owner's
        // Manual p.162 C1's output cascades into C2 to derive the
        // 1-second interrupt. Without a live C1 tick, C2 never fires
        // and the SA-1510 monitor's main-loop wait never completes
        // (the `*` prompt would never print). Best-guess rate for
        // Phase 3: the horizontal-sync 15.72 kHz frequency, matching
        // what the MZ-700 does for HBLNK-derived cursor timing.
        _pitC1Accum += cpuCycles * 157;    // 157/20000 ≈ 15.72 kHz @ 2 MHz
        int c1 = _pitC1Accum / 20000;
        _pitC1Accum -= c1 * 20000;

        Pit.Tick(c0, c1);
    }

    public void AutoLoadBasic(string basicPath)
    {
        if (!File.Exists(basicPath))
            throw new FileNotFoundException("BASIC cassette image not found", basicPath);
        var img = Hardware.Cassette.Parse(File.ReadAllBytes(basicPath));
        // Direct-inject rather than dispatch through SA-1510's LOAD —
        // the L command needs a keyboard input path that isn't fully
        // wired yet (auto-typer is MZ-700 only). Once the auto-typer
        // lands, this can switch to Queue + "L\r" to exercise the
        // real trap path.
        Cassette.DirectInject(img, jumpExec: true);
    }

    public void AutoLoadCassette(string path, bool autoRun)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Cassette image not found", path);
        var img = Hardware.Cassette.Parse(CassetteFile.ReadBytes(path));
        // Same direct-inject shortcut as AutoLoadBasic — bypasses the
        // monitor's L command until a keyboard auto-typer exists.
        Cassette.DirectInject(img, jumpExec: ShouldJumpExecForType(img.Type));
    }

    public void DirectInjectCassette(string path)
    {
        var img = Hardware.Cassette.Parse(CassetteFile.ReadBytes(path));
        Cassette.DirectInject(img, jumpExec: ShouldJumpExecForType(img.Type));
    }

    /// <summary>
    /// Only machine-code (type 01) images have a monitor-callable exec
    /// address in their .mzf header. BASIC text (02), BASIC data (03),
    /// and relocatable (05) images typically carry exec=$0000, which
    /// is SA-1510's reset entry — jumping there would wipe the loaded
    /// BASIC and reset the whole machine. Load them into RAM but leave
    /// PC alone so BASIC can run them via its own RUN command (which
    /// still needs to be typed manually until the auto-typer lands).
    /// </summary>
    private static bool ShouldJumpExecForType(byte type) => type == 0x01;
}
