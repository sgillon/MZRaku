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
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_mz700.Cpu.PC:X4} SP=${_mz700.Cpu.SP:X4} IFF1={_mz700.Cpu.IFF1} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} run={c2.Running} out={c2.Out} INTMSK={_mz700.Ppi.InterruptMask} hdr={_mz700.Cassette.HeaderDelivered} dat={_mz700.Cassette.DataDelivered}");
            }
            else if (_mz800 != null)
            {
                var c0 = _mz800.Pit.Counters[0];
                var c2 = _mz800.Pit.Counters[2];
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_mz800.Cpu.PC:X4} SP=${_mz800.Cpu.SP:X4} IFF1={_mz800.Cpu.IFF1} cfg={_mz800.Mem.Config} mz700={_mz800.Mem.Mz700Mode} C0.rel={c0.Reload} run={c0.Running} out={c0.Out} C2.rel={c2.Reload} run={c2.Running} out={c2.Out} INTMSK={_mz800.Ppi.InterruptMask}");
            }
            else
            {
                _traceLog.AppendLine($"[F{bootFrames:D4}] PC=${_active.Cpu.PC:X4} SP=${_active.Cpu.SP:X4} IFF1={_active.Cpu.IFF1}");
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

    /// <summary>
    /// Phase 5.5: alongside the .txt + .trace, also emit a .png of the
    /// current video frame. Gives visual verification of the renderer's
    /// output without needing a live GUI session — the CI-friendly form
    /// of "look at the screen".
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
            SaveMz800TestPatternPng(_dumpPath + ".test.png");
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
            _mz800.Video.RenderBitmap(mem.PlaneI, mem.PlaneII, mem.Palette, mem.BorderColour);
            using var snapshot = new System.Drawing.Bitmap(_mz800.Video.Frame);
            snapshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            Array.Copy(savedI,  mem.PlaneI,  savedI.Length);
            Array.Copy(savedII, mem.PlaneII, savedII.Length);
            // Re-render so the live frame reflects the real (post-restore)
            // plane state, not the test pattern.
            _mz800.Video.RenderBitmap(mem.PlaneI, mem.PlaneII, mem.Palette, mem.BorderColour);
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
            w.WriteLine($"Mem Config={_mz800.Mem.Config} Mz700Mode={_mz800.Mem.Mz700Mode}");
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
            // Phase 5.1: plane samples. Prove BASIC's writes now land
            // in plane storage rather than vanishing. Address $9F00 is
            // near the end of a 320×200 plane (row 199 area) where the
            // clear loop starts; $8000 is row 0 (should stay zero if
            // nothing wrote there yet). PlaneIII[$1FFF] captures the
            // two probe writes BASIC does with WF=$94 at CPU $9FFF.
            var sbP1a = new StringBuilder("PlaneI  @ $8000: ");
            for (int i = 0; i < 16; i++) sbP1a.Append($"{_mz800.Mem.PlaneI[i]:X2} ");
            w.WriteLine(sbP1a.ToString());
            var sbP1b = new StringBuilder("PlaneI  @ $9F00: ");
            for (int i = 0; i < 16; i++) sbP1b.Append($"{_mz800.Mem.PlaneI[0x1F00 + i]:X2} ");
            w.WriteLine(sbP1b.ToString());
            var sbP2 = new StringBuilder("PlaneII @ $9F00: ");
            for (int i = 0; i < 16; i++) sbP2.Append($"{_mz800.Mem.PlaneII[0x1F00 + i]:X2} ");
            w.WriteLine(sbP2.ToString());
            var sbP3 = new StringBuilder("PlaneIII @ $9FF0: ");
            for (int i = 0; i < 16; i++) sbP3.Append($"{_mz800.Mem.PlaneIII[0x1FF0 + i]:X2} ");
            w.WriteLine(sbP3.ToString());
            // Sanity check: Ram[$8000-$800F] should now stay zero (writes
            // route to planes instead). Contrast against Phase 5.0 where
            // the whole $8000-$BFFF area was Ram[] and was still zero
            // only because BASIC's writes were being absorbed into DRAM.
            var sbRam8000 = new StringBuilder("Ram @ $8000: ");
            for (int i = 0; i < 16; i++) sbRam8000.Append($"{_mz800.Mem.Ram[0x8000 + i]:X2} ");
            w.WriteLine(sbRam8000.ToString());
            // Phase 5.4: palette + border in resolved form so the dump
            // reader can eyeball whether BASIC's cold-boot palette
            // (research/05-palette.md predicts black/blue/red/white
            // from $00 $11 $22 $3F) landed correctly.
            var sbPal = new StringBuilder("Palette IRGB: ");
            for (int i = 0; i < 4; i++)
                sbPal.Append($"[{i}]=${_mz800.Mem.Palette[i]:X1}→ARGB={Mz800Video.IrgbToArgb(_mz800.Mem.Palette[i]):X8} ");
            w.WriteLine(sbPal.ToString());
            w.WriteLine($"Border IRGB: ${_mz800.Mem.BorderColour:X1}→ARGB={Mz800Video.IrgbToArgb(_mz800.Mem.BorderColour):X8}");
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

        // Phase 5.0 diagnostics — MZ-800 only.
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
