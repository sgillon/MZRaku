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
/// the 1Z-013B monitor blind — CPU starts at $0000 (JP $E800), the
/// MZ-800 IPL (1Z-016B) runs from ROM at CPU $E800 (file offset
/// $2800), flips to MZ-700 mode via OUT ($CE),A, banks the CG-ROM
/// in/out to copy PCG data, then MZ-700 monitor at CPU $0000 takes
/// over and writes the '*' prompt to VRAM at $D000. All that is verifiable via the
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
        // Keyboard needs the DRAM handle for the $1170 shift mirror
        // that the 1Z-013B monitor's GETKY reads to pick between
        // unshifted / shifted key tables (Phase 3, 2026-08-28).
        Keyboard.Memory = Mem;

        // Cassette needs Memory + CPU for trap injection. PreStep
        // watches the 1Z-013B tape implementation addresses at $04D8
        // (READ HEADER) and $04F8 (READ DATA), which 1Z-016B's own L
        // subroutine at $EB54 calls into. See Mz800Cassette's class
        // comment for the flow.
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
        // MZ800.ROM is a single 16 KB file combining MZ-700 monitor
        // (1Z-013B) + CG-ROM + MZ-800 IPL/monitor (1Z-016B) +
        // BASIC-IOCS. Font parameter is ignored — the CG lives inside
        // the combined ROM at offset $1000-$1FFF, extracted for the
        // renderer by Mz800Video.
        Mem.LoadRom(File.ReadAllBytes(monitorRomPath));
        Video.LoadFontFromRom(Mem.Rom);
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
        // Drop any matrix bits a host KeyDown asserted but hasn't yet
        // released. Canonical case: Ctrl+R — PC Ctrl down asserts MZ
        // CTRL via Mz800SpecialKeyMap's ControlKey entry, then the
        // menu shortcut fires Reset. Without this the IPL boots with
        // CTRL still held, which routes it into a diagnostic branch
        // that never draws anything ("black screen after Ctrl+R"
        // regression caught during Phase 3 verification 2026-08-28).
        // Same pattern as MZ700.Reset / MZ80A.Reset.
        Keyboard.ReleaseAll();
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
            // Still rebuild the framebuffer so the debugger's memory
            // viewer and the main display stay live even while the
            // CPU is paused. Same pattern as MZ700 / MZ80A.
            Video.Render(Mem.Vram, Mem.Aram);
            return;
        }
        bool stepFrame = _stepFrameRequested;
        _stepFrameRequested = false;

        // Live-typing staged key bits: shifted presses land their key
        // bit a couple of frames after SHIFT/$1170 was set, so the ROM
        // scan sees a consistent (shift, key) pair rather than the key
        // with stale cached shift. Same reason MZ-700 / MZ-80A tick
        // this once per frame.
        Keyboard.TickStagedKeyBits();

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

        Video.Render(Mem.Vram, Mem.Aram);
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

        // 1Z-016.mzf loads to $0000 with exec=$0000 in its SA-1510
        // header. That's not literally "jump to zero and reset" — the
        // 42 KB payload's byte-0 is a standard Sharp jump table whose
        // first entry (`C3 F9 0E` at offset 0) is JP $0EF9, the BASIC
        // cold-boot handler. To land there the CPU has to see DRAM at
        // $0000, not the MZ-700 monitor ROM that config B keeps mapped
        // in there. Bank config D_AllRam (all 64 KB DRAM) does exactly
        // that — it's the config the tech-ref specifies for BASIC use
        // and is what IN ($E1) in MZ-800 mode selects (p. 5).
        //
        // Trap-driven LOAD via M/L or the IPL's C option also loads
        // the binary but then 1Z-016B does JP <header exec = $0000>,
        // which in config B reads ROM at $0000 (`JP $E800`) and
        // restarts the IPL. That's the "load then boot menu again"
        // behaviour we saw during Phase 4c bring-up. AutoLoadBasic
        // skips that dance entirely: write to Ram[] directly, flip
        // the banks, hand off to BASIC's own cold-boot at PC=$0000.
        //
        // The payload spans $0000-$A3F9 which stays safely below the
        // VRAM window at $D000 — no chance of VRAM/ARAM corruption
        // from these writes. Going via Ram[] rather than Mem.Write
        // is still the right shape because current config B routes
        // $E000-$E00F writes to the I/O bus and $E010+ to ROM shadow
        // (both fine here since 1Z-016 doesn't cross those thresholds
        // but preserves the "the loaded binary owns exactly what its
        // header says it owns" contract).
        for (int i = 0; i < CassetteTrapBase.HeaderSize; i++)
            Mem.Ram[CassetteTrapBase.HeaderBufferAddr + i] = img.Header[i];
        for (int i = 0; i < img.Data.Length; i++)
            Mem.Ram[img.LoadAddr + i] = img.Data[i];

        Mem.Mz700Mode = false;
        Mem.Config = MZ800Memory.BankConfig.D_AllRam;
        Cpu.PC = img.ExecAddr; // = $0000 for 1Z-016
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
