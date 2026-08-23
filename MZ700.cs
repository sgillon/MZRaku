using System;
using System.IO;
using MZRaku.Hardware;
using Z80Core;

namespace MZRaku;

/// <summary>
/// Assembled Sharp MZ-700 machine: Z80 + memory + I/O devices + cassette.
/// </summary>
public sealed class MZ700 : MzMachineBase, IMachine
{
    // Mem is an auto-property (not a field) so this class can satisfy
    // IMachine's read-only property requirement. Declared type stays
    // MZ700Memory so MZ-700-typed callers can still reach VRAM/ARAM/Ram
    // directly; IMachine.Mem is exposed as Z80Core.IMemory via explicit
    // interface impl below. Cpu is inherited from MzMachineBase (F-060).
    public MZ700Memory Mem { get; } = new();
    public Ppi8255 Ppi = new();
    public Pit8253 Pit = new();
    public IoBus Io = new();
    public Keyboard Keyboard = new();
    public Video Video { get; } = new();
    public Cassette Cassette { get; } = new();
    public Sound Sound { get; } = new();
    public Joystick Joystick = new();
    public RomKeyTables KeyTables = new();

    public MachineType Kind => MachineType.MZ700;
    Z80Core.IMemory IMachine.Mem => Mem;
    // IMachine.Cassette upcasts to CassetteTrapBase for the interface
    // consumers; MZ-700-typed callers keep the concrete Cassette
    // property above (with WriteTapeTrapHits, BreakWaitTrapHits, and
    // the SAVE-tape surface).
    CassetteTrapBase IMachine.Cassette => Cassette;
    // MainForm's paint reads whichever machine's rendered bitmap is
    // non-null; the concrete Video property stays for callers that
    // need MZ-700-specific renderer knobs.
    System.Drawing.Bitmap? IMachine.VideoFrame => Video.Frame;

    public const double CpuClockHz = 3546900.0;             // MZ-700 master clock ~3.5MHz
    public const double PitC0InputHz = 895000.0;            // counter 0 input clock
    public const double PitC2InputHz = 15700.0;             // counter 2 input (HBLANK rate)
    public const int FramesPerSecond = 60;
    public const int CyclesPerFrame = (int)(CpuClockHz / FramesPerSecond);

    private int _pitC0Accum;
    private int _pitC1Accum;
    private int _tempoAccum;
    // CPU cycles per TempoBit toggle. CpuClockHz / (2 * targetHz) gives
    // half-period in cycles. 3.5469MHz / (2 * 50) ≈ 35469 → 50 Hz toggle.
    // Empirically tuned against real-hardware MUSIC playback length: a
    // tune from Nightmare Park (np.mzf line 596) measured at 13 sec on
    // real MZ-700 plays at ~13 sec with this rate.
    private const int CyclesPerTempoToggle = 35469;

    // Debugger control (Paused, _stepFrameRequested, Pause/Resume/
    // StepInstruction/StepFrame) is inherited from MzMachineBase
    // (v1.2 audit F-060).

    public MZ700()
    {
        Cpu.Mem = Mem;
        Cpu.Io = Io;
        Io.Ppi = Ppi;
        Io.Pit = Pit;
        Io.Memory = Mem;
        Io.Sound = Sound;
        Mem.IoBus = Io;
        Mem.Cpu = Cpu;
        Ppi.Keyboard = Keyboard;
        Keyboard.Memory = Mem;
        Cassette.Memory = Mem;
        Cassette.Cpu = Cpu;
        Cassette.Keyboard = Keyboard;
        Io.Joystick = Joystick;
        Joystick.Cpu = Cpu;
        Cpu.PreStep = Cassette.OnPreStep;

        Pit.Counter2Out += _ =>
        {
            // Counter 2 OUT goes high once at the 12-hour terminal count
            // (per service manual: "OUT2 turns to a high level 12 hours
            // after... connected to the CPU interrupt pin"). Cursor blink
            // is NOT driven from C2 — it comes from the separate 555/556
            // oscillator now self-driven inside Ppi.SetVBlank. INTMSK in
            // PPI PortC bit 2 == 1 means interrupts ENABLED on MZ-700.
            if (Ppi.InterruptMask) Cpu.RequestInterrupt();
        };

        Ppi.SpeakerGateChanged += on => Sound.Enabled = on;
    }

    public void LoadRoms(string monitorRomPath, string? fontPath)
    {
        Mem.LoadRom(File.ReadAllBytes(monitorRomPath));
        KeyTables.Build(Mem.Rom);
        if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
        {
            // Dispatch on extension: .int = binary, .txt = hex dump.
            if (string.Equals(Path.GetExtension(fontPath), ".int", StringComparison.OrdinalIgnoreCase))
                Video.LoadFont(File.ReadAllBytes(fontPath));
            else
                Video.LoadFontHex(File.ReadAllText(fontPath));
        }
    }

    public void Reset()
    {
        Cpu.Reset();
        Cpu.IM = 1;
        // FF1.CL on the schematic is the system RESET line, so the
        // speaker-NAND hard gate clears on power-on / Ctrl+R. ROM
        // sets it back to 1 via a write to $E008 when it wants the
        // boot tone audible.
        Sound.HardGate = false;
        // Restore power-on bank state. Without this, a reset while BASIC
        // is running leaves RomEnabled=false (BASIC banks the monitor
        // ROM out at startup). The CPU then resumes at $0000 from RAM,
        // where BASIC's code is sitting — so reset just re-enters BASIC
        // instead of running the monitor's boot sequence.
        Mem.RomEnabled = true;
        Mem.VramIoEnabled = true;
        // Clear pending cassette state so a stale Pending image doesn't
        // get served to the freshly-booting monitor's tape traps.
        Cassette.Pending = null;
        Cassette.HeaderDelivered = false;
        Cassette.DataDelivered = false;
        Cassette.ResetSaveState();
        // Drop any matrix bits a host KeyDown asserted but hasn't yet
        // released. Ctrl+R is the canonical case: PC Ctrl down asserts
        // MZ CTRL (8,6), the menu shortcut then fires Reset, and without
        // this the monitor would boot with CTRL still held until the
        // user lifted the PC Ctrl key.
        Keyboard.ReleaseAll();
        // Clear VRAM to spaces, attributes to white-on-blue (MZ-700 default)
        for (int i = 0; i < 2048; i++) { Mem.Vram[i] = 0x00; Mem.Aram[i] = 0x71; }
        // Run monitor: the ROM handles its own startup
    }

    /// <summary>
    /// Execute a single video frame. Advances CPU for ~1/60s worth of cycles,
    /// tracking PIT counter ticks and VBLANK signalling.
    ///
    /// When <see cref="Paused"/> (debugger), the CPU is not advanced — the
    /// screen is still re-rendered so the display and debugger panes stay
    /// live. A one-shot "step frame" overrides the pause for one frame, and
    /// a breakpoint hit mid-frame stops the frame early and sets Paused.
    /// </summary>
    public void RunFrame()
    {
        if (Paused && !_stepFrameRequested)
        {
            Video.Render(Mem.Vram, Mem.Aram);
            return;
        }
        bool stepFrame = _stepFrameRequested;
        _stepFrameRequested = false;

        // Visible portion ~192 lines + blanking -> we'll pulse VBLANK at frame end
        Ppi.SetVBlank(false);
        // VBLK falling edge triggers the joystick 555 monostables.
        Joystick.OnVBlankFall(Cpu.TotalCycles);

        int cyclesThisFrame = 0;
        int cyclesToVBlank = (int)(CyclesPerFrame * 0.85);

        // Type-ahead (auto-typed commands) tick.
        Keyboard.AutoType.Tick();
        // Live-typing staged key bits: presses whose MzShift requirement
        // forced a $1170 transition land their key bit a couple of frames
        // after shift was set, so the ROM scan sees a consistent
        // (shift, key) pair rather than the key with stale cached shift.
        Keyboard.TickStagedKeyBits();

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

        // A breakpoint hit (or a one-shot step-frame) leaves the machine
        // paused so the debugger can inspect state.
        if (tripped || stepFrame) Paused = true;

        // Render to Video.Frame
        Video.Render(Mem.Vram, Mem.Aram);

        // Update sound reload value. When counter 0 isn't actively counting
        // (e.g. after a control word but before LSB/MSB are reloaded) the
        // speaker should be silent regardless of the PC3 gate state.
        Sound.SetReload(Pit.Counters[0].Running ? Pit.Counters[0].Reload : 0);
    }

    protected override void AccumulatePit(int cpuCycles)
    {
        // C0 ticks at 895kHz (audio); C1 ticks at 15.6kHz (HBLNK).
        // C2 is cascaded from C1.OUT inside the PIT (not clocked here).
        _pitC0Accum += cpuCycles * 895;   // 895/3547 ≈ 0.2523
        int c0 = _pitC0Accum / 3547;
        _pitC0Accum -= c0 * 3547;

        _pitC1Accum += cpuCycles * 157;   // 157/35469 ≈ 0.00443 → 15.6kHz
        int c1 = _pitC1Accum / 35469;
        _pitC1Accum -= c1 * 35469;

        Pit.Tick(c0, c1);

        // 555/556 cursor-osc derived TEMPO signal — toggle TempoBit at
        // ~100 Hz off the CPU clock so MUSIC duration matches real hardware.
        _tempoAccum += cpuCycles;
        while (_tempoAccum >= CyclesPerTempoToggle)
        {
            _tempoAccum -= CyclesPerTempoToggle;
            Ppi.TempoBit = !Ppi.TempoBit;
        }
    }

    public void AutoLoadBasic(string basicPath)
    {
        if (!File.Exists(basicPath)) throw new FileNotFoundException("BASIC cassette image not found", basicPath);
        var img = MzfImage.Parse(File.ReadAllBytes(basicPath));
        // Direct-inject: write the full image to its load address and jump to
        // its exec entry. This bypasses the monitor's LOAD dispatch, which is
        // unreliable in our emulation (keyboard-scan ISR doesn't run because
        // the monitor main loop stays in DI state most of the time).
        Cassette.DirectInject(img, jumpExec: true);
    }

    public void AutoLoadCassette(string path, bool autoRun)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Cassette image not found", path);
        var img = MzfImage.Parse(CassetteFile.ReadBytes(path));
        Cassette.Queue(img);
        if (autoRun)
        {
            // Trigger the monitor's LOAD command (the trap will inject data and monitor auto-runs)
            Keyboard.AutoType.TypeString("L\r");
        }
    }

    public void DirectInjectCassette(string path)
    {
        var img = MzfImage.Parse(CassetteFile.ReadBytes(path));
        Cassette.DirectInject(img, jumpExec: true);
    }
}
