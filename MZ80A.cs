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

    // Cassette + BASIC autoload — Phase 4 wires these properly. Until
    // then, throw a clear message rather than crash the app; MainForm
    // catches this in the --basic path and shows a friendlier prompt.
    public void AutoLoadBasic(string basicPath)
    {
        throw new NotSupportedException(
            "MZ-80A BASIC autoload arrives in Phase 4. For now the machine boots to the SA-1510 monitor.");
    }

    public void AutoLoadCassette(string path, bool autoRun)
    {
        throw new NotSupportedException(
            "MZ-80A cassette autoload arrives in Phase 4.");
    }

    public void DirectInjectCassette(string path)
    {
        throw new NotSupportedException(
            "MZ-80A cassette autoload arrives in Phase 4.");
    }
}
