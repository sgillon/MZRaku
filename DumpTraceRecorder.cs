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
    private readonly MZ700? _mz700;           // null on MZ-80A
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
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Dump failed: {ex.Message}");
                return;
            }
            OnDumpComplete?.Invoke();
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

        // VRAM is 40x25 on both machines; grab it via the machine
        // that's actually running. Bytes are the raw display codes
        // each machine renders through its own font ROM.
        byte[] vram = _mz700 != null ? _mz700.Mem.Vram : ((MZ80A)_active).Mem.Vram;
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
        // Pit / Mem write logs are MZ-700 concrete-class members;
        // MZ-80A doesn't expose analogues yet.
        if (_mz700 == null) return;
        if (_mz700.Pit.WriteLog != null)
        {
            _traceLog.AppendLine();
            _traceLog.AppendLine("PIT write log:");
            _traceLog.Append(_mz700.Pit.WriteLog);
        }
        if (_mz700.Mem.BankSwitchLog != null)
        {
            _traceLog.AppendLine();
            _traceLog.AppendLine("Bank-switch log:");
            _traceLog.Append(_mz700.Mem.BankSwitchLog);
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
