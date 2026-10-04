using System;
using System.IO;
using System.Text;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Owns the <c>--dump=&lt;file&gt;</c> CLI flow: gather a per-frame
/// trace of the CPU / PIT / cassette state, then at frame
/// <c>_dumpFrame</c> write a comprehensive dump file plus a
/// <c>.trace</c> companion and signal the host to close. All the
/// dump-and-close plumbing MainForm carried before v1.2 audit F-055
/// lives here.
///
/// The recorder is a no-op when <c>dumpPath == null</c> (99.9% of
/// runs) — the WriteLog / BankSwitchLog StringBuilders on MZ-700
/// stay null in that case too (see MainForm.Start's tracing wire),
/// so PIT writes and bank switches short-circuit their
/// null-conditional AppendLines with zero cost.
/// </summary>
internal sealed class DumpTraceRecorder
{
    private readonly string? _dumpPath;
    private readonly int _dumpFrame;
    private readonly IMachine _active;
    private readonly MZ700? _mz700;           // null on MZ-80A / MZ-800
    private readonly MZ800? _mz800;           // null on MZ-700 / MZ-80A
    private readonly string _machineLabel;
    private readonly StringBuilder _traceLog = new();

    /// <summary>Fired with a short error message if the dump write throws.</summary>
    public event Action<string>? OnError;

    /// <summary>
    /// Fired after a successful dump + .trace write. MainForm
    /// subscribes and closes the form (dump-and-exit shape).
    /// </summary>
    public event Action? OnDumpComplete;

    public DumpTraceRecorder(string? dumpPath, int dumpFrame, IMachine active, string machineLabel)
    {
        _dumpPath = dumpPath;
        _dumpFrame = dumpFrame;
        _active = active;
        _mz700 = active as MZ700;
        _mz800 = active as MZ800;
        _machineLabel = machineLabel;
    }

    /// <summary>
    /// Called from Timer_Tick once per frame. Emits the periodic
    /// trace line and, when bootFrames hits _dumpFrame, writes the
    /// dump + .trace file and fires <see cref="OnDumpComplete"/>.
    /// </summary>
    public void OnFrame(int bootFrames)
    {
        if (_dumpPath == null) return;

        // Trace state every 20 frames to help diagnose boot/load
        // issues. MZ-700 gets the rich Pit/Ppi/Cassette-flavoured
        // line; MZ-80A gets a shorter CPU-only line (no PIT sound
        // / cassette-trap gates to report yet).
        if ((bootFrames <= 10 || bootFrames % 20 == 0) && bootFrames <= _dumpFrame)
        {
            if (_mz700 != null)
            {
                var c0 = _mz700.Pit.Counters[0];
                var c2 = _mz700.Pit.Counters[2];
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_mz700.Cpu.PC:X4} SP=${_mz700.Cpu.SP:X4} IFF1={_mz700.Cpu.IFF1} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} val={c2.Value} run={c2.Running} out={c2.Out} INTMSK={_mz700.Ppi.InterruptMask} hdr={_mz700.Cassette.HeaderDelivered} dat={_mz700.Cassette.DataDelivered}");
            }
            else if (_mz800 != null)
            {
                var c0 = _mz800.Pit.Counters[0];
                var c2 = _mz800.Pit.Counters[2];
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_mz800.Cpu.PC:X4} SP=${_mz800.Cpu.SP:X4} IFF1={_mz800.Cpu.IFF1} IM={_mz800.Cpu.IM} I=${_mz800.Cpu.I:X2} bank={_mz800.Mem.BankState} mz700={_mz800.Mem.Mz700Mode} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} val={c2.Value} run={c2.Running} out={c2.Out} INTMSK={_mz800.Ppi.InterruptMask}");
            }
            else
            {
                string pit = _active is MZ80A a
                    ? $" C2.rel={a.Pit.Counters[2].Reload} val={a.Pit.Counters[2].Value}"
                    : "";
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_active.Cpu.PC:X4} SP=${_active.Cpu.SP:X4} IFF1={_active.Cpu.IFF1}{pit}");
            }
        }

        if (bootFrames == _dumpFrame)
        {
            try
            {
                DumpState(_dumpPath, bootFrames);
                AppendPcTrace();
                AppendMz700WriteLogs();
                File.WriteAllText(_dumpPath + ".trace", _traceLog.ToString());
                // Raw 64 KB DRAM image for offline
                // disassembly of code software relocates at runtime
                // (e.g. BASIC's high-RAM routines above the .mzf image).
                if (_mz800 != null) File.WriteAllBytes(_dumpPath + ".ram", _mz800.Mem.Ram);
                // Rendered PSG + counter-0 audio since boot.
                if (_mz800?.AudioCapture != null) SaveWav(_dumpPath + ".wav", _mz800.AudioCapture);
                SaveVideoFramePng();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Dump failed: {ex.Message}");
                return;
            }
            OnDumpComplete?.Invoke();
        }
    }

    private static void SaveWav(string path, MemoryStream pcm)
    {
        int dataLen = (int)pcm.Length;
        int rate = Hardware.Sound.OutputSampleRate;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16);
        w.Write((short)1); w.Write((short)1);          // PCM, mono
        w.Write(rate); w.Write(rate * 2);              // sample rate, byte rate
        w.Write((short)2); w.Write((short)16);         // block align, bits
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        pcm.WriteTo(w.BaseStream);
    }

    /// <summary>
    /// Alongside the .txt + .trace, emit a .png of the current video
    /// frame — visual verification without a live GUI session (and what
    /// the local golden-image regression harness compares).
    ///
    /// For MZ-800 dumps we also emit a `.test.png` where we seed the
    /// planes with a known 4-colour-bar pattern (black/blue/red/white
    /// top-to-bottom) and render that. Proves end-to-end that the
    /// renderer reads plane bytes, decodes the 2-bit colour code, and
    /// resolves through the palette + IrgbToArgb correctly — separate
    /// from whether the CURRENT plane content happens to be black.
    /// </summary>
    private void SaveVideoFramePng()
    {
        var frame = _active.VideoFrame;
        if (frame == null || _dumpPath == null) return;
        using (var snapshot = new System.Drawing.Bitmap(frame))
            snapshot.Save(_dumpPath + ".png", System.Drawing.Imaging.ImageFormat.Png);

        if (_mz800 != null)
        {
            SaveMz800TestPatternPng(_dumpPath + ".test.png");
            SaveMz800Test640PatternPng(_dumpPath + ".test640.png");
            SaveMz800TestScrollPng(_dumpPath + ".testscroll.png");
            SaveMz800TestSplitPng(_dumpPath + ".testsplit.png");
        }
    }

    /// <summary>
    /// Seed planes with a 4-horizontal-bar pattern, render, save, then
    /// restore the original plane data. Palette used comes from
    /// whatever BASIC / the IPL programmed at the time of the dump — so
    /// the four bars should match BASIC's actual palette
    /// (typically black / dark-blue / dark-red / bright-white).
    /// </summary>
    private void SaveMz800TestPatternPng(string path)
    {
        if (_mz800 == null) return;
        var mem = _mz800.Mem;
        // Back up plane I + II (Frame A). We only test Frame A rendering.
        var savedI  = (byte[])mem.PlaneI.Clone();
        var savedII = (byte[])mem.PlaneII.Clone();
        try
        {
            // 320×200 = 40 bytes wide × 200 rows. 50 rows per colour bar.
            const int bytesPerRow = 40;
            for (int y = 0; y < 200; y++)
            {
                int rowBase = y * bytesPerRow;
                // Colour code 0..3 depending on which quarter of the screen.
                int code = y / 50;
                byte piBits = (code & 1) != 0 ? (byte)0xFF : (byte)0x00;
                byte p2Bits = (code & 2) != 0 ? (byte)0xFF : (byte)0x00;
                for (int col = 0; col < bytesPerRow; col++)
                {
                    mem.PlaneI[rowBase + col]  = piBits;
                    mem.PlaneII[rowBase + col] = p2Bits;
                }
            }
            _mz800.Video.RenderPlanes320(mem.PlaneI, mem.PlaneII, null, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), Mz800Video.ScrollRegs.None);
            using var snapshot = new System.Drawing.Bitmap(_mz800.Video.Frame);
            snapshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Array.Copy(savedI,  mem.PlaneI,  savedI.Length);
            Array.Copy(savedII, mem.PlaneII, savedII.Length);
            // Re-render so the live frame reflects the real (post-restore)
            // plane state, not the test pattern.
            _mz800.Video.RenderPlanes320(mem.PlaneI, mem.PlaneII, null, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), Mz800Video.ScrollRegs.None);
        }
    }

    /// <summary>
    /// 640-mode pattern: seed Plane I with a pattern that exercises
    /// both bit-ordering (LSB-first) and the even/odd-bank decode of
    /// 640-mode, render FrameHi, save, restore.
    ///
    /// Pattern: every EVEN display byte = $FF, every ODD display byte
    /// = $00. On a correct decode that produces 8-pixel-wide vertical
    /// bars alternating on/off across the full 640-pixel width. If
    /// the even/odd decode is inverted you'd instead see one solid
    /// on/off band per half-screen. If bit ordering is wrong the bar
    /// widths would be off inside each 8-pixel group.
    /// </summary>
    private void SaveMz800Test640PatternPng(string path)
    {
        if (_mz800 == null) return;
        var mem = _mz800.Mem;
        var savedI = (byte[])mem.PlaneI.Clone();
        try
        {
            // Even display bytes live at Plane I offset $0000-$1F3F,
            // odd at $2000-$3F3F (tech-ref p. 15). 40 even bytes per
            // row × 200 rows fills the low half; ditto for the high.
            const int rowSpan = 40;
            const int oddBase = 0x2000;
            for (int y = 0; y < 200; y++)
            {
                int rowBaseEven = y * rowSpan;
                int rowBaseOdd  = oddBase + y * rowSpan;
                for (int c = 0; c < rowSpan; c++)
                {
                    mem.PlaneI[rowBaseEven + c] = 0xFF; // even display byte → all pixels on
                    mem.PlaneI[rowBaseOdd  + c] = 0x00; // odd  display byte → all pixels off
                }
            }
            _mz800.Video.RenderPlanes640(mem.PlaneI, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), Mz800Video.ScrollRegs.None);
            using var snapshot = new System.Drawing.Bitmap(_mz800.Video.FrameHi);
            snapshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Array.Copy(savedI, mem.PlaneI, savedI.Length);
            _mz800.Video.RenderPlanes640(mem.PlaneI, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), Mz800Video.ScrollRegs.None);
        }
    }

    /// <summary>
    /// Scroll-verification pattern: seed 320-mode Plane I +
    /// Plane II with the same 4-horizontal-bar pattern as .test.png,
    /// then apply SOF = 250 (scrolls display up 50 scanlines = one
    /// full bar), render, save, restore. Expected: the bars appear
    /// cyclically rotated — top band (black) wraps to the bottom
    /// instead of appearing at the top. Proves the renderer's SOF
    /// path works end-to-end.
    /// </summary>
    private void SaveMz800TestScrollPng(string path)
    {
        if (_mz800 == null) return;
        var mem = _mz800.Mem;
        var savedI  = (byte[])mem.PlaneI.Clone();
        var savedII = (byte[])mem.PlaneII.Clone();
        ushort savedSof = mem.Sof;
        try
        {
            const int bytesPerRow = 40;
            for (int y = 0; y < 200; y++)
            {
                int rowBase = y * bytesPerRow;
                int code = y / 50;
                byte piBits = (code & 1) != 0 ? (byte)0xFF : (byte)0x00;
                byte p2Bits = (code & 2) != 0 ? (byte)0xFF : (byte)0x00;
                for (int col = 0; col < bytesPerRow; col++)
                {
                    mem.PlaneI[rowBase + col]  = piBits;
                    mem.PlaneII[rowBase + col] = p2Bits;
                }
            }
            // SOF unit is 8 bytes (5 = one raster line). SOF=250 → shift
            // up 50 scanlines = one full 4-band bar. Result: the bar order visibly
            // rotates (black moves from top to bottom).
            mem.Sof = 250;
            _mz800.Video.RenderPlanes320(mem.PlaneI, mem.PlaneII, null, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), new Mz800Video.ScrollRegs(0x00, 0x7D, 0x7D, mem.Sof));
            using var snapshot = new System.Drawing.Bitmap(_mz800.Video.Frame);
            snapshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Array.Copy(savedI,  mem.PlaneI,  savedI.Length);
            Array.Copy(savedII, mem.PlaneII, savedII.Length);
            mem.Sof = savedSof;
            _mz800.Video.RenderPlanes320(mem.PlaneI, mem.PlaneII, null, null, Mz800Video.UniformRows(Mz800Video.PaletteLut(mem.Palette)), new Mz800Video.ScrollRegs(0x00, 0x7D, 0x7D, mem.Sof));
        }
    }

    /// <summary>
    /// Split-screen scroll pattern: the tech-ref's own example (p. 10
    /// §5 — SSA=$19, SEA=$5A, SW=$41, fixed bands above raster 40 and
    /// from raster 144) with SOF=$5 (one raster). Plane I byte for raster
    /// r is r itself (plane II zero), drawn with a fixed black / white
    /// palette, so every display row shows which VRAM raster it fetched:
    /// rows 0-39 and 144-199 unchanged, 40-143 advanced by one raster
    /// with raster 40 wrapping to row 143.
    /// </summary>
    private void SaveMz800TestSplitPng(string path)
    {
        if (_mz800 == null) return;
        var mem = _mz800.Mem;
        var savedI  = (byte[])mem.PlaneI.Clone();
        var savedII = (byte[])mem.PlaneII.Clone();
        try
        {
            for (int y = 0; y < 200; y++)
                for (int col = 0; col < 40; col++)
                {
                    mem.PlaneI[y * 40 + col]  = (byte)y;
                    mem.PlaneII[y * 40 + col] = 0;
                }
            var lut = Mz800Video.PaletteLut(new byte[] { 0x0, 0xF, 0x0, 0x0 });
            _mz800.Video.RenderPlanes320(mem.PlaneI, mem.PlaneII, null, null,
                Mz800Video.UniformRows(lut), new Mz800Video.ScrollRegs(0x19, 0x5A, 0x41, 0x05));
            using var snapshot = new System.Drawing.Bitmap(_mz800.Video.Frame);
            snapshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Array.Copy(savedI,  mem.PlaneI,  savedI.Length);
            Array.Copy(savedII, mem.PlaneII, savedII.Length);
        }
    }

    private void DumpState(string path, int bootFrames)
    {
        using var w = new StreamWriter(path);
        w.WriteLine($"{_machineLabel} state after {bootFrames} frames");
        var cpu = _active.Cpu;
        w.WriteLine($"CPU: PC=${cpu.PC:X4} SP=${cpu.SP:X4} A=${cpu.A:X2} F=${cpu.F:X2} HL=${cpu.HL:X4} BC=${cpu.BC:X4} DE=${cpu.DE:X4}");
        w.WriteLine($"IM={cpu.IM} IFF1={cpu.IFF1} Halted={cpu.Halted} Cycles={cpu.TotalCycles}");
        // The PPI / PIT / cassette-trap counters and the
        // bank-switch gate are MZ-700 concrete-class members;
        // MZ-80A doesn't expose matching surfaces yet. Guard so
        // the shared preamble above still fires on
        // --mz80a --dump=…
        if (_mz700 != null)
        {
            w.WriteLine($"PPI PortA=${_mz700.Ppi.PortA:X2} PortCOut=${_mz700.Ppi.PortCOut:X2} PortCIn=${_mz700.Ppi.PortCIn:X2}");
            w.WriteLine($"Mem RomEnabled={_mz700.Mem.RomEnabled} VramIoEnabled={_mz700.Mem.VramIoEnabled}");
            w.WriteLine($"PIT C0.Reload={_mz700.Pit.Counters[0].Reload} C2.Reload={_mz700.Pit.Counters[2].Reload}");
            var sb0 = new StringBuilder("RAM @ $1200: ");
            for (int i = 0; i < 32; i++) sb0.Append($"{_mz700.Mem.Read((ushort)(0x1200 + i)):X2} ");
            w.WriteLine(sb0.ToString());
            w.WriteLine($"Tape trap hits: BreakWait={_mz700.Cassette.BreakWaitTrapHits} Header={_mz700.Cassette.HeaderTrapHits} Data={_mz700.Cassette.DataTrapHits} WriteTape={_mz700.Cassette.WriteTapeTrapHits}");
        }
        else if (_mz800 != null)
        {
            w.WriteLine($"PPI PortA=${_mz800.Ppi.PortA:X2} PortCOut=${_mz800.Ppi.PortCOut:X2} PortCIn=${_mz800.Ppi.PortCIn:X2}");
            w.WriteLine($"Mem Bank={_mz800.Mem.BankState} Mz700Mode={_mz800.Mem.Mz700Mode}");
            w.WriteLine($"PIT C0.Reload={_mz800.Pit.Counters[0].Reload} C2.Reload={_mz800.Pit.Counters[2].Reload}");
            // Phase 2.5 spots: RAM at the LDIR destination ($C000),
            // and interrupt handler install at RAM $1038 (expected
            // C3 8D 03 = "JP $038D").
            var sbC = new StringBuilder("RAM @ $C000: ");
            for (int i = 0; i < 32; i++) sbC.Append($"{_mz800.Mem.Ram[0xC000 + i]:X2} ");
            w.WriteLine(sbC.ToString());
            var sb38 = new StringBuilder("RAM @ $1038: ");
            for (int i = 0; i < 8; i++) sb38.Append($"{_mz800.Mem.Ram[0x1038 + i]:X2} ");
            w.WriteLine(sb38.ToString());
            var sbATB = new StringBuilder("ARAM @ $D800: ");
            for (int i = 0; i < 32; i++) sbATB.Append($"{_mz800.Mem.Aram[i]:X2} ");
            w.WriteLine(sbATB.ToString());
            // CRTC registers and plane occupancy — enough to tell which
            // display mode a program is in and which planes it drew on.
            var m = _mz800.Mem;
            w.WriteLine($"CRTC DMD=${m.DmdRegister:X2} WF=${m.WfRegister:X2} RF=${m.RfRegister:X2} " +
                        $"SSA=${m.Ssa:X2} SEA=${m.Sea:X2} SW=${m.Sw:X2} SOF=${m.Sof:X3} " +
                        $"MZ-1R25={(m.VramExpansion ? "fitted" : "absent")}");
            var sbPal = new StringBuilder("Palette IGRB: ");
            for (int i = 0; i < 4; i++)
                sbPal.Append($"[{i}]=${m.Palette[i]:X1}→ARGB={Mz800Video.IrgbToArgb(m.Palette[i]):X8} ");
            sbPal.Append($"group={m.PaletteGroup}");
            w.WriteLine(sbPal.ToString());
            w.WriteLine($"Border IGRB: ${m.BorderColour:X1}→ARGB={Mz800Video.IrgbToArgb(m.BorderColour):X8}");
            static int NonZero(byte[] plane) { int n = 0; foreach (var b in plane) if (b != 0) n++; return n; }
            w.WriteLine($"Plane non-zero bytes: I={NonZero(m.PlaneI)} II={NonZero(m.PlaneII)} " +
                        $"III={NonZero(m.PlaneIII)} IV={NonZero(m.PlaneIV)}");
            w.WriteLine($"Tape trap hits: Header={_mz800.Cassette.HeaderTrapHits} Data={_mz800.Cassette.DataTrapHits}");
        }

        // VRAM is 40x25 on all three machines; grab it via the
        // machine that's actually running. Bytes are the raw display
        // codes each machine renders through its own font ROM.
        byte[] vram = _mz700 != null ? _mz700.Mem.Vram
                    : _mz800 != null ? _mz800.Mem.Vram
                    : ((MZ80A)_active).Mem.Vram;
        w.WriteLine();
        w.WriteLine("VRAM (40x25 text codes):");
        for (int row = 0; row < 25; row++)
        {
            var sb = new StringBuilder();
            sb.Append($"[{row:D2}] ");
            for (int col = 0; col < 40; col++)
                sb.Append($"{vram[row * 40 + col]:X2} ");
            w.WriteLine(sb.ToString());
        }
        // ASCII rendering is a best-effort MZ-700 display-code →
        // ASCII walk; the MZ-80A display-code mapping isn't
        // identical, so this block stays MZ-700-only for now.
        if (_mz700 != null)
        {
            w.WriteLine();
            w.WriteLine("VRAM as ASCII (best-effort):");
            for (int row = 0; row < 25; row++)
            {
                var sb = new StringBuilder();
                sb.Append($"[{row:D2}] ");
                for (int col = 0; col < 40; col++)
                {
                    byte b = _mz700.Mem.Vram[row * 40 + col];
                    sb.Append(MzDisplayToAscii(b));
                }
                w.WriteLine(sb.ToString());
            }
        }
    }

    private void AppendPcTrace()
    {
        // Append last 256 PC values (oldest first). Cpu is Z80Cpu
        // on both machines, so this works via the interface.
        _traceLog.AppendLine();
        _traceLog.AppendLine("Recent PC trace (oldest first):");
        int start = _active.Cpu.PcTraceIdx;
        for (int i = 0; i < _active.Cpu.PcTrace.Length; i++)
        {
            _traceLog.Append($"${_active.Cpu.PcTrace[(start + i) & 0xFF]:X4} ");
            if (i % 16 == 15) _traceLog.AppendLine();
        }
    }

    private void AppendMz700WriteLogs()
    {
        // Pit / Mem write logs are MZ-700 / MZ-800 concrete-class
        // members; MZ-80A doesn't expose analogues yet.
        var pitLog = _mz700?.Pit.WriteLog ?? _mz800?.Pit.WriteLog;
        var memLog = _mz700?.Mem.BankSwitchLog ?? _mz800?.Mem.BankSwitchLog;
        if (pitLog != null)
        {
            _traceLog.AppendLine();
            _traceLog.AppendLine("PIT write log:");
            _traceLog.Append(pitLog);
        }
        if (memLog != null)
        {
            _traceLog.AppendLine();
            _traceLog.AppendLine("Bank-switch log:");
            _traceLog.Append(memLog);
        }

        // MZ-800 diagnostics.
        if (_mz800 != null)
        {
            if (_mz800.Io.CrtcWriteLog != null)
            {
                _traceLog.AppendLine();
                _traceLog.AppendLine("CRTC / palette write log ($CC/$CD/$CE/$CF/$F0):");
                _traceLog.Append(_mz800.Io.CrtcWriteLog);
            }
            if (_mz800.Mem.VideoWriteLog != null)
            {
                _traceLog.AppendLine();
                _traceLog.AppendLine("Bitmap VRAM window write log ($8000-$BFFF):");
                _traceLog.Append(_mz800.Mem.VideoWriteLog);
            }
            if (_mz800.Mem.ModeFlipLog != null)
            {
                _traceLog.AppendLine();
                _traceLog.AppendLine("DMD mode-flip log ($CE) — mode + config transitions only:");
                _traceLog.Append(_mz800.Mem.ModeFlipLog);
            }
            if (_mz800.Io.IntIoWriteLog != null)
            {
                _traceLog.AppendLine();
                _traceLog.AppendLine("PIO / PSG / PPI write log ($FC-$FF / $F2 / $D0-$D3):");
                _traceLog.Append(_mz800.Io.IntIoWriteLog);
            }
        }
    }

    private static char MzDisplayToAscii(byte b)
    {
        // MZ display codes: 0x00=@, 0x01-0x1A=A-Z, 0x20-0x29=0-9,
        // punctuation varies. Best-effort mapping for the dump's
        // ASCII pane.
        if (b == 0x00) return ' ';
        if (b >= 0x01 && b <= 0x1A) return (char)('A' + (b - 0x01));
        if (b >= 0x20 && b <= 0x29) return (char)('0' + (b - 0x20));
        if (b == 0x2A) return ' ';
        if (b == 0x67) return ' ';
        if (b == 0xCE) return ' '; // MZ "space" in some sets
        if (b >= 0x20 && b <= 0x7E) return (char)b;
        return '.';
    }
}
