using System;
using System.IO;
using MZ700Emul.Hardware;
using MZ700Emul.Z80;

namespace MZ700Emul;

/// <summary>
/// Assembled Sharp MZ-700 machine: Z80 + memory + I/O devices + cassette.
/// </summary>
public sealed class MZ700
{
    public Z80Cpu Cpu = new();
    public MZ700Memory Mem = new();
    public Ppi8255 Ppi = new();
    public Pit8253 Pit = new();
    public IoBus Io = new();
    public Keyboard Keyboard = new();
    public VideoRenderer Video = new();
    public Cassette Cassette = new();
    public Sound Sound = new();
    public Joystick Joystick = new();

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

    public MZ700()
    {
        Cpu.Mem = Mem;
        Cpu.Io = Io;
        Io.Ppi = Ppi;
        Io.Pit = Pit;
        Io.Memory = Mem;
        Mem.IoBus = Io;
        Mem.Cpu = Cpu;
        Ppi.Keyboard = Keyboard;
        Keyboard.Memory = Mem;
        Cassette.Memory = Mem;
        Cassette.Cpu = Cpu;
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
        // Clear VRAM to spaces, attributes to white-on-blue (MZ-700 default)
        for (int i = 0; i < 2048; i++) { Mem.Vram[i] = 0x00; Mem.Aram[i] = 0x71; }
        // Run monitor: the ROM handles its own startup
    }

    /// <summary>
    /// Execute a single video frame. Advances CPU for ~1/60s worth of cycles,
    /// tracking PIT counter ticks and VBLANK signalling.
    /// </summary>
    public void RunFrame()
    {
        // Visible portion ~192 lines + blanking -> we'll pulse VBLANK at frame end
        Ppi.SetVBlank(false);
        // VBLK falling edge triggers the joystick 555 monostables.
        Joystick.OnVBlankFall(Cpu.TotalCycles);

        int cyclesThisFrame = 0;
        int cyclesToVBlank = (int)(CyclesPerFrame * 0.85);

        // Type-ahead (auto-typed commands) tick
        Keyboard.TickAutoType();

        while (cyclesThisFrame < cyclesToVBlank)
        {
            int cyc = Cpu.Step();
            cyclesThisFrame += cyc;
            AccumulatePit(cyc);
        }

        Ppi.SetVBlank(true);

        while (cyclesThisFrame < CyclesPerFrame)
        {
            int cyc = Cpu.Step();
            cyclesThisFrame += cyc;
            AccumulatePit(cyc);
        }

        // Render to Video.Frame
        Video.Render(Mem.Vram, Mem.Aram);

        // Update sound reload value. When counter 0 isn't actively counting
        // (e.g. after a control word but before LSB/MSB are reloaded) the
        // speaker should be silent regardless of the PC3 gate state.
        Sound.SetReload(Pit.Counters[0].Running ? Pit.Counters[0].Reload : 0);
    }

    private void AccumulatePit(int cpuCycles)
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
        var img = Cassette.Parse(File.ReadAllBytes(basicPath));
        // Direct-inject: write the full image to its load address and jump to
        // its exec entry. This bypasses the monitor's LOAD dispatch, which is
        // unreliable in our emulation (keyboard-scan ISR doesn't run because
        // the monitor main loop stays in DI state most of the time).
        Cassette.DirectInject(img, jumpExec: true);
    }

    public void AutoLoadCassette(string path, bool autoRun)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Cassette image not found", path);
        var img = Cassette.Parse(CassetteFile.ReadBytes(path));
        Cassette.Queue(img);
        if (autoRun)
        {
            // Trigger the monitor's LOAD command (the trap will inject data and monitor auto-runs)
            Keyboard.TypeString("L\r");
        }
    }

    public void DirectInjectCassette(string path)
    {
        var img = Cassette.Parse(CassetteFile.ReadBytes(path));
        Cassette.DirectInject(img, jumpExec: true);
    }
}
