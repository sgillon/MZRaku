using System;
using System.IO;
using MZRaku.Hardware;
using Z80Core;

namespace MZRaku;

/// <summary>
/// Assembled Sharp MZ-800 machine: Z80 (3.547 MHz) + 8255 PPI + 8253
/// PIT + Z80 PIO + SN76489 PSG + 64 KiB DRAM + 16 KiB combined ROM +
/// bank latches (OUT $E0-$E6, IN $E0/$E1) + dual-mode I/O layout.
///
/// Boot flow (Phase 1 spike, 2026-08-29): CPU starts at $0000 (JP $E800), the
/// MZ-800 IPL (1Z-016B) runs from ROM at CPU $E800 (file offset
/// $2800), flips to MZ-700 mode via OUT ($CE),A, banks the CG-ROM
/// in/out to copy PCG data, then MZ-700 monitor at CPU $0000 takes
/// over and writes the '*' prompt to VRAM at $D000.
///
/// Status (Phase 6.1): MZ-700-mode text + MZ-800 bitmap video,
/// keyboard, cassette traps, Z80 PIO timer interrupt, bank latches and
/// the SN76489 PSG are live. Remaining: joystick + PIO printer side
/// (Phase 7), polish (Phase 8).
/// </summary>
public sealed class MZ800 : MzMachineBase, IMachine
{
    // Cpu is inherited from MzMachineBase (v1.2 audit F-060).
    public MZ800Memory Mem { get; } = new();
    public Ppi8255 Ppi = new();
    public Pit8253 Pit = new();
    public Z80Pio Pio = new();
    public Mz800IoBus Io = new();
    public Mz800Video Video { get; } = new();
    public Mz800Keyboard Keyboard = new();
    public Mz800Cassette Cassette { get; } = new();
    // Audio output. Runs in external-PCM mode: RenderAudio mixes the
    // PSG + gated PIT counter-0 audio-in in emulated time and queues
    // samples; Sound's feed thread plays them. The $E008 D0 write in
    // MZ-700 mode still lands on Sound.HardGate (counter-0 gate).
    public Sound Sound { get; } = new();
    public Sn76489 Psg { get; } = new();

    public MachineType Kind => MachineType.MZ800;
    double IMachine.FramesPerSecond => FramesPerSecond;
    Z80Core.IMemory IMachine.Mem => Mem;
    CassetteTrapBase IMachine.Cassette => Cassette;
    // Video output surface: DMD-mode-aware. Phase 5.6 added the second
    // bitmap (FrameHi, 640×200) so 640-mode gets its native resolution
    // without stretching or downsampling the 320-mode/MZ-700-mode
    // output. MainForm.Display_Paint scales from whichever bitmap
    // comes back here — it reads size from the bitmap itself, not
    // from any constant.
    System.Drawing.Bitmap? IMachine.VideoFrame => Mem.Is640BitmapMode ? Video.FrameHi : Video.Frame;

    // MZ-800 CPU runs at 3.547 MHz (17.734 MHz crystal ÷ 5, per
    // tech-ref p. 9). Matches MZ-700 exactly; MZ-80A is the slower
    // one at 2 MHz.
    public const double CpuClockHz = 3_546_900.0;

    // ---- Raster timing (Phase 7.2) ----
    //
    // PAL: 312 lines × 64 µs (= 227 CPU cycles) → 70,824 cycles, 50.08
    // frames/s. Line 0 starts with vertical sync; the 200 display lines
    // follow a blank/border band. Within each line the horizontal
    // blank is the last HBlankCycles. Software syncs to this through
    // the CRTC status port (IN $CE, see CrtcStatus) — G.P.S. counts
    // lines from VSYNC to change the palette mid-frame for its rolling
    // colour bars — so the renderer takes a per-display-row palette
    // snapshot instead of using the end-of-frame registers.
    public const int CyclesPerLine = 227;
    public const int LinesPerFrame = 312;
    public const int CyclesPerFrame = CyclesPerLine * LinesPerFrame;
    public const double FramesPerSecond = CpuClockHz / CyclesPerFrame;   // ≈ 50.08
    // First display line after the start of VSYNC. Calibrated against
    // EmuZ-800 with G.P.S.'s title bars: its 40-line bar group sweeps
    // from VSYNC-end + 1 + offset, offset 0..231, and EmuZ-800 turns it
    // round exactly as the last bar reaches the bottom display row —
    // so line 3 + 1 + 231 + 39 = 274 is display row 199.
    public const int DisplayFirstLine = 75;
    public const int DisplayLines = 200;
    private const int VSyncLines = 3;
    private const int HBlankCycles = 44;

    // PIT input clocks. C0 is CKMS = 17.7344 MHz ÷ 16 ≈ 1.108 MHz
    // (tech-ref p. 28 "1.10 MHz"), i.e. exactly 5/16 of the CPU clock
    // (17.7344 ÷ 5). Phase 6.0 corrected this from the MZ-700's
    // 895 kHz — C0 now times BASIC's PIO interrupt, so its rate sets
    // the ISR cadence (10928 counts ≈ 9.9 ms ≈ 101 Hz).
    public const double PitC0InputHz = 1_108_400.0;
    public const double PitC2InputHz = 15_700.0;

    private int _pitC0Accum;
    private int _pitC1Accum;
    private int _tempoAccum;
    // Same TEMPO rate as MZ-700 (see MZ700.CyclesPerTempoToggle for
    // the calibration history). The MZ-700 monitor at $02DB polls $E008
    // bit 0 for this signal to advance out of the boot beep-wait loop;
    // without the toggle, the CPU spins there forever. Confirmed by
    // Phase 1 boot spike (2026-08-29): stuck at PC=$02DB until this
    // toggle was added.
    private const int CyclesPerTempoToggle = MZ700.CyclesPerTempoToggle;

    public MZ800()
    {
        Cpu.Mem = Mem;
        Cpu.Io = Io;
        Io.Ppi = Ppi;
        Io.Pit = Pit;
        Io.Memory = Mem;
        Io.Sound = Sound;
        Io.Cpu = Cpu;
        Io.Pio = Pio;
        Io.Psg = Psg;
        Io.CrtcStatus = CrtcStatus;
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

        // Phase 6.1: PSG path. Sound's square-wave generator is unused
        // (Enabled stays false); samples come from RenderAudio.
        Sound.Enabled = false;
        Sound.ExternalPcm = true;
        Pit.HaltOnControlWord = true;
        Pit.LoadOnNextClock = true;

        // Timer interrupt from PIT counter 2 — mirrors MZ-700. INTMSK
        // bit is PortC bit 2 (== INTMSK meaning D2=1 means interrupts
        // ENABLED on MZ-700; MZ-800 keeps the MZ-700 convention when
        // in MZ-700 mode).
        Pit.Counter2Out += _ =>
        {
            if (Ppi.InterruptMask) Cpu.RequestInterrupt();
        };

        // PIO interrupt (tech-ref p. 28): PIT OUT0 → inverter → PA4.
        // The PIO supplies its programmed vector byte, which IM 2
        // uses as the low byte of the vector-table address. BASIC's
        // MUSIC/PSG sequencer ISR ($421A via $0FFC) hangs off this
        // (Phase 6.0).
        Pio.InterruptRequested += vector =>
        {
            if (Io.IntIoWriteLog != null)
            {
                ushort va = (ushort)((Cpu.I << 8) | vector);
                ushort target = (ushort)(Mem.Read(va) | (Mem.Read((ushort)(va + 1)) << 8));
                Io.LogIntIoNote($"  [PIO INT] PC=${Cpu.PC:X4} SP=${Cpu.SP:X4} IFF1={Cpu.IFF1} IM={Cpu.IM} vec=${va:X4} -> ${target:X4} bank={Mem.BankState}");
            }
            Cpu.RequestInterrupt(vector);
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
        Pio.Reset();
        Psg.Reset();
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
            RenderCurrentMode();
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

        Cpu.BreakpointTripped = false;
        bool tripped = false;

        // _frameCycle persists across calls so a breakpoint mid-frame
        // resumes at the same beam position.
        while (_frameCycle < CyclesPerFrame)
        {
            int line = _frameCycle / CyclesPerLine;
            if (line != _currentLine) OnLineStart(line);
            int cyc = Cpu.Step();
            if (Cpu.BreakpointTripped) { tripped = true; break; }
            _frameCycle += cyc;
            AccumulatePit(cyc);
        }
        if (!tripped)
        {
            _frameCycle -= CyclesPerFrame;
            _currentLine = -1;
        }

        if (tripped || stepFrame) Paused = true;

        RenderCurrentMode();
    }

    private int _frameCycle;
    private int _currentLine = -1;
    private bool _inDisplay;
    // Palette state latched as each display row starts: PLT0-3 then
    // the 16-colour group, per row.
    private readonly byte[] _rowPalette = new byte[DisplayLines * 4];
    private readonly byte[] _rowPaletteGroup = new byte[DisplayLines];

    /// <summary>
    /// Beam enters a new scanline: update the blanking signals and, on
    /// display lines, latch the palette the row will be drawn with.
    /// Writes made during the previous line's horizontal blank (where
    /// raster code times them) therefore take effect from this row.
    /// </summary>
    private void OnLineStart(int line)
    {
        _currentLine = line;
        bool display = line >= DisplayFirstLine && line < DisplayFirstLine + DisplayLines;
        if (display != _inDisplay)
        {
            _inDisplay = display;
            // VBLANK = outside the display lines: 8255 PC7 (MZ-700-mode
            // $E008 path) and PIO PA5 (/VBLANK frame interrupt, Phase
            // 7.0 — tech-ref p. 31 calls PA5 "horizontal blanking,
            // active H", but Uridium/Jetpac use it as an active-low
            // once-per-frame sync).
            Ppi.SetVBlank(!display);
            Pio.SetPortAPin(5, display);
        }
        if (display)
        {
            int row = line - DisplayFirstLine;
            Array.Copy(Mem.Palette, 0, _rowPalette, row * 4, 4);
            _rowPaletteGroup[row] = (byte)Mem.PaletteGroup;
        }
    }

    /// <summary>
    /// CRTC status, IN $CE. Undocumented in the tech-ref (p. 23 just
    /// says "status read"); bit meanings inferred from software
    /// (Phase 7.2):
    ///   D7 /HBLANK  — 0 during horizontal blank. G.P.S. counts its
    ///                 0→1 edges to step down the screen line by line.
    ///   D6 /VBLANK  — 1 on display lines. BASIC spins while D6=1
    ///                 before rewriting SOF, i.e. waits for blanking.
    ///   D4 /VSYNC   — 0 during vertical sync. G.P.S. waits for its
    ///                 0→1 edge once per frame.
    ///   D1          — the IPL branches on it at $E853 (likely the SW1
    ///                 MZ-700/MZ-800 switch); left 0, as before.
    /// Phase 5.3 had D7 = VBLANK; no software found relies on that.
    /// </summary>
    public byte CrtcStatus()
    {
        int line = _frameCycle / CyclesPerLine;
        int inLine = _frameCycle % CyclesPerLine;
        byte status = 0;
        if (inLine < CyclesPerLine - HBlankCycles) status |= 0x80;
        if (line >= DisplayFirstLine && line < DisplayFirstLine + DisplayLines) status |= 0x40;
        if (line >= VSyncLines) status |= 0x10;
        return status;
    }

    /// <summary>
    /// Pick a renderer per <see cref="MZ800Memory.Mz700Mode"/> and, in
    /// MZ-800 mode, the DMD register (tech-ref p. 17 Table-1):
    ///
    ///   DMD  resolution  colours  planes shown        lookup
    ///   $00  320×200     4        I + II   (Frame A)  PLT0-3
    ///   $01  320×200     4        III + IV (Frame B)  PLT0-3   [MZ-1R25]
    ///   $02  320×200     16       I-IV                16-colour [MZ-1R25]
    ///   $04  640×200     1        I        (Frame A)  PLT0/1
    ///   $05  640×200     1        III      (Frame B)  PLT0/1   [MZ-1R25]
    ///   $06  640×200     4        I + III             PLT0-3   [MZ-1R25]
    ///
    /// DMD1:0 = 11 is prohibited; treated as Frame A. Without the
    /// MZ-1R25, planes III/IV stay zero (their writes are dropped), so
    /// the expansion-only modes degrade the way real hardware's "not
    /// assured" output would rather than showing stale data. SOF
    /// scroll (Phase 5.7): <c>Sof / 5</c> scanlines.
    /// </summary>
    private void RenderCurrentMode()
    {
        if (Mem.Mz700Mode)
        {
            Video.Render(Mem.Vram, Mem.Aram);
            return;
        }
        int scrollLines = Mem.Sof / 5;
        int frame = Mem.DmdRegister & 0x03;
        byte[]? plIII = Mem.VramExpansion ? Mem.PlaneIII : null;
        byte[]? plIV  = Mem.VramExpansion ? Mem.PlaneIV : null;
        bool sixteen = !Mem.Is640BitmapMode && frame == 2;
        var palette = BuildRowLuts(sixteen);

        if (Mem.Is640BitmapMode)
        {
            if (frame == 1)
                Video.RenderPlanes640(plIII, null, palette, scrollLines);
            else if (frame == 2)
                Video.RenderPlanes640(Mem.PlaneI, plIII, palette, scrollLines);
            else
                Video.RenderPlanes640(Mem.PlaneI, null, palette, scrollLines);
            return;
        }

        if (frame == 1)
            Video.RenderPlanes320(plIII, plIV, null, null, palette, scrollLines);
        else if (frame == 2)
            Video.RenderPlanes320(Mem.PlaneI, Mem.PlaneII, plIII, plIV, palette, scrollLines);
        else
            Video.RenderPlanes320(Mem.PlaneI, Mem.PlaneII, null, null, palette, scrollLines);
    }

    private readonly int[][] _rowLuts = new int[DisplayLines][];

    /// <summary>
    /// One colour lookup per display row from the palette latched as
    /// that row started (<see cref="OnLineStart"/>). Consecutive rows
    /// with the same palette share a lookup.
    /// </summary>
    private int[][] BuildRowLuts(bool sixteenColour)
    {
        var pal = new byte[4];
        int[]? prev = null;
        for (int row = 0; row < DisplayLines; row++)
        {
            Array.Copy(_rowPalette, row * 4, pal, 0, 4);
            int group = _rowPaletteGroup[row];
            bool same = prev != null && row > 0
                && _rowPalette[row * 4] == _rowPalette[row * 4 - 4]
                && _rowPalette[row * 4 + 1] == _rowPalette[row * 4 - 3]
                && _rowPalette[row * 4 + 2] == _rowPalette[row * 4 - 2]
                && _rowPalette[row * 4 + 3] == _rowPalette[row * 4 - 1]
                && group == _rowPaletteGroup[row - 1];
            if (!same)
                prev = sixteenColour ? Mz800Video.SixteenColourLut(pal, group) : Mz800Video.PaletteLut(pal);
            _rowLuts[row] = prev!;
        }
        return _rowLuts;
    }

    protected override void AccumulatePit(int cpuCycles)
    {
        // C0 = CPU × 5/16 (1.108 MHz CKMS — see PitC0InputHz). C1 as
        // MZ-700 (15.7 kHz HSYN).
        _pitC0Accum += cpuCycles * 5;
        int c0 = _pitC0Accum / 16;
        _pitC0Accum -= c0 * 16;

        _pitC1Accum += cpuCycles * 157;   // 157/35469 ≈ 0.00443 → 15.7 kHz
        int c1 = _pitC1Accum / 35469;
        _pitC1Accum -= c1 * 35469;

        Pit.Tick(c0, c1);
        // PA4 is the inverted OUT0. Sampled after every instruction so
        // both terminal count and a CPU reload (mode 0 → OUT low) reach
        // the PIO's edge detector.
        Pio.SetPortAPin(4, !Pit.Counters[0].Out);

        RenderAudio(cpuCycles);

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

    // ---- Audio (Phase 6.1) ----
    //
    // The PSG divides its input clock (the CPU clock, 3.5469 MHz) by
    // 16, so it steps every 16 CPU cycles (~221.7 kHz). Each output
    // sample (44.1 kHz, ~5 PSG steps) is the average of the steps it
    // spans — a box filter that tames the aliasing of high tones.
    // Mixed in: PIT counter 0 through the PSG's audio-in, gated by
    // 8255 PC0 (tech-ref p. 31: PC0 masks counter-0 sound so it can
    // double as the MZ-800-mode timer) and, in MZ-700 mode, by the
    // $E008 D0 latch driving GATE0 (p. 28). A one-pole high-pass
    // (~35 Hz) removes the DC the unipolar chip output carries.
    private const int CpuClockInt = 3_546_900;
    private const int PsgDivider = 16;
    private const float CounterZeroLevel = 0.25f;   // ≈ one PSG channel at full volume
    private const float OutputGain = 24000f;
    private const float DcBlockPole = 0.995f;
    private int _psgCycleAccum;
    private long _sampleAccum;
    private float _mixSum;
    private int _mixCount;
    private float _dcPrevIn, _dcPrevOut;

    /// <summary>
    /// Optional capture of every rendered sample (16-bit LE mono PCM at
    /// <see cref="Sound.OutputSampleRate"/>). Set under --dump=;
    /// DumpTraceRecorder writes it out as a .wav for offline checks.
    /// </summary>
    public MemoryStream? AudioCapture;

    private void RenderAudio(int cpuCycles)
    {
        bool c0Audible = Pit.Counters[0].Out
                         && (Ppi.PortCOut & 0x01) != 0
                         && (!Mem.Mz700Mode || Sound.HardGateLatch);
        float c0 = c0Audible ? CounterZeroLevel : 0f;

        _psgCycleAccum += cpuCycles;
        while (_psgCycleAccum >= PsgDivider)
        {
            _psgCycleAccum -= PsgDivider;
            _mixSum += Psg.Step() + c0;
            _mixCount++;
            _sampleAccum += (long)PsgDivider * Sound.OutputSampleRate;
            if (_sampleAccum < CpuClockInt) continue;
            _sampleAccum -= CpuClockInt;

            float x = _mixSum / _mixCount;
            _mixSum = 0f;
            _mixCount = 0;
            float y = x - _dcPrevIn + DcBlockPole * _dcPrevOut;
            _dcPrevIn = x;
            _dcPrevOut = y;
            short sample = (short)Math.Clamp((int)(y * OutputGain), short.MinValue, short.MaxValue);
            Sound.PushSample(sample);
            if (AudioCapture != null)
            {
                AudioCapture.WriteByte((byte)sample);
                AudioCapture.WriteByte((byte)(sample >> 8));
            }
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
        // $0000, not the MZ-700 monitor ROM (OUT $E0 below).
        //
        // Trap-driven LOAD via M/L or the IPL's C option also loads
        // the binary, but during Phase 4c bring-up 1Z-016B's
        // JP <header exec = $0000> landed on the monitor ROM's
        // `JP $E800` and restarted the IPL ("load then boot menu
        // again"). That was before OUT $E0-$E6 banking existed
        // (Phase 6.0), so the C path may work now — unverified.
        // AutoLoadBasic skips that dance entirely: write to Ram[]
        // directly, set the banks, hand off to BASIC's own cold-boot
        // at PC=$0000.
        //
        // The payload spans $0000-$A3F9, which overlaps the MZ-800
        // bitmap VRAM window ($8000-$9FFF) — writing straight to
        // Ram[] puts it in DRAM regardless of the bank latches, the
        // way the "loaded binary owns exactly what its header says"
        // contract needs.
        for (int i = 0; i < CassetteTrapBase.HeaderSize; i++)
            Mem.Ram[CassetteTrapBase.HeaderBufferAddr + i] = img.Header[i];
        for (int i = 0; i < img.Data.Length; i++)
            Mem.Ram[img.LoadAddr + i] = img.Data[i];

        // Hand over with the map BASIC runs in: MZ-800 mode, DRAM at
        // $0000-$7FFF (OUT $E0) and $E000-$FFFF (OUT $E1), VRAM still
        // at $8000. BASIC banks the ROM back in itself (OUT $E3 ...
        // OUT $E1 around `JP $F4xx`) for IOCS calls.
        Mem.Mz700Mode = false;
        Mem.HandleBankOut(0x00);
        Mem.HandleBankOut(0x01);
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
