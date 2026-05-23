using System;
using System.IO;
using System.Text;
using System.Threading;
using Z80Core;

namespace MZ700Emul;

/// <summary>
/// Runs a CP/M .com Z80 test ROM (ZEXDOC, ZEXALL, etc.) against the
/// emulator's Z80 core. Bypasses the MZ-700 hardware: banks the monitor
/// ROM out, loads the .com at $0100, traps BDOS calls at $0005, and
/// terminates when the program RETs to $0000 (CP/M WBOOT vector).
///
/// Saves/restores the full CPU + RAM/VRAM snapshot so the user's
/// running BASIC session survives the test.
/// </summary>
public sealed class Z80TestRunner
{
    private readonly MZ700 _machine;
    private readonly Action<string> _onOutput;
    private readonly Action<bool> _onComplete;
    private readonly CancellationToken _ct;
    private readonly StringBuilder _lineBuf = new();

    private Thread? _thread;
    private volatile bool _shouldExit;

    // Snapshot of pre-test state
    private Z80State? _savedCpu;
    private bool _savedRomEnabled;
    private bool _savedVramIoEnabled;
    private bool _savedPaused;
    private byte[]? _savedRam, _savedVram, _savedAram;
    private Func<bool>? _savedPreStep;

    public Z80TestRunner(MZ700 machine,
                         Action<string> onOutput,
                         Action<bool> onComplete,
                         CancellationToken ct)
    {
        _machine = machine;
        _onOutput = onOutput;
        _onComplete = onComplete;
        _ct = ct;
    }

    public void Start(string comPath)
    {
        var comBytes = File.ReadAllBytes(comPath);
        SaveSnapshot();
        SetupCpm(comBytes);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Z80TestRunner" };
        _thread.Start();
    }

    public void Stop() => _shouldExit = true;

    public void Join(int timeoutMs)
    {
        _thread?.Join(timeoutMs);
    }

    private void SaveSnapshot()
    {
        _savedCpu = Z80State.From(_machine.Cpu);
        _savedPreStep = _machine.Cpu.PreStep;
        _savedRomEnabled = _machine.Mem.RomEnabled;
        _savedVramIoEnabled = _machine.Mem.VramIoEnabled;
        _savedPaused = _machine.Paused;
        _savedRam = (byte[])_machine.Mem.Ram.Clone();
        _savedVram = (byte[])_machine.Mem.Vram.Clone();
        _savedAram = (byte[])_machine.Mem.Aram.Clone();
    }

    private void SetupCpm(byte[] comBytes)
    {
        var mem = _machine.Mem;
        // Bank monitor ROM out so $0000-$0FFF is RAM. ZEXDOC patches $0000
        // (for its own JP to start) and stores variables in low RAM.
        mem.RomEnabled = false;
        // VRAM/IO mapping stays unchanged — test doesn't touch those pages.

        // Wipe RAM so test starts from a clean slate.
        Array.Clear(mem.Ram, 0, mem.Ram.Length);

        // Load the .com at $0100 (CP/M TPA).
        Array.Copy(comBytes, 0, mem.Ram, 0x0100, comBytes.Length);

        // Minimal CP/M trampoline at $0000-$0007:
        //   $0000: HLT  (safety — test programs jp 0 to exit, but PC=$0000 is trapped)
        //   $0005: JP $E000  (BDOS vector — calls here are trapped before executing;
        //                     however ZEXDOC reads the word at $0006 with
        //                     `ld hl,(6) / ld sp,hl` to set its stack pointer to
        //                     the top of TPA, so this must be a real high-RAM addr).
        mem.Ram[0x0000] = 0x76; // HLT
        mem.Ram[0x0005] = 0xC3; mem.Ram[0x0006] = 0x00; mem.Ram[0x0007] = 0xE0;

        var c = _machine.Cpu;
        c.Reset();
        c.PC = 0x0100;
        c.SP = 0xE000;
        c.IFF1 = c.IFF2 = false;
        c.IM = 1;
        c.Halted = false;
        c.PreStep = OnPreStep;

        _shouldExit = false;
        // Stop the MZ700.RunFrame loop from stepping the CPU concurrently
        // with our background thread.
        _machine.Paused = true;
    }

    /// <summary>
    /// BDOS / exit trap. Runs on the test thread before each Cpu.Step.
    /// </summary>
    private bool OnPreStep()
    {
        var c = _machine.Cpu;

        if (c.PC == 0x0005)
        {
            // BDOS call. Function in C, args per function.
            byte fn = c.C;
            if (fn == 2)
            {
                // Console output: char in E.
                AppendChar((char)c.E);
            }
            else if (fn == 9)
            {
                // Print '$'-terminated string at DE.
                ushort addr = c.DE;
                for (int safety = 0; safety < 65536; safety++)
                {
                    byte b = _machine.Mem.Read(addr++);
                    if (b == (byte)'$') break;
                    AppendChar((char)b);
                }
            }
            // else: ignore other BDOS calls — ZEXDOC only uses 2 and 9.

            // Simulate RET — pop return address from stack into PC.
            byte lo = _machine.Mem.Read(c.SP);
            byte hi = _machine.Mem.Read((ushort)(c.SP + 1));
            c.SP += 2;
            c.PC = (ushort)((hi << 8) | lo);
            return true;
        }

        if (c.PC == 0x0000)
        {
            // CP/M WBOOT vector — test program is done.
            _shouldExit = true;
            return true;
        }

        return false;
    }

    private void AppendChar(char ch)
    {
        _lineBuf.Append(ch);
        // Flush on LF so the UI sees line-by-line progress.
        if (ch == '\n')
        {
            FlushLineBuf();
        }
    }

    private void FlushLineBuf()
    {
        if (_lineBuf.Length == 0) return;
        string s = _lineBuf.ToString();
        _lineBuf.Clear();
        try { _onOutput(s); } catch { /* UI disposed mid-test — ignore */ }
    }

    private void RunLoop()
    {
        bool faulted = false;
        try
        {
            while (!_shouldExit && !_ct.IsCancellationRequested)
            {
                _machine.Cpu.Step();
            }
        }
        catch
        {
            faulted = true;
        }
        finally
        {
            FlushLineBuf();
            RestoreSnapshot();
            try { _onComplete(faulted || _ct.IsCancellationRequested); } catch { }
        }
    }

    private void RestoreSnapshot()
    {
        if (_savedCpu == null) return;
        _savedCpu.Apply(_machine.Cpu);
        _machine.Cpu.PreStep = _savedPreStep;
        _machine.Mem.RomEnabled = _savedRomEnabled;
        _machine.Mem.VramIoEnabled = _savedVramIoEnabled;
        _machine.Paused = _savedPaused;
        if (_savedRam != null) Array.Copy(_savedRam, _machine.Mem.Ram, _savedRam.Length);
        if (_savedVram != null) Array.Copy(_savedVram, _machine.Mem.Vram, _savedVram.Length);
        if (_savedAram != null) Array.Copy(_savedAram, _machine.Mem.Aram, _savedAram.Length);
    }
}

/// <summary>Plain snapshot of Z80 register state for save/restore.</summary>
internal sealed class Z80State
{
    public byte A, F, B, C, D, E, H, L;
    public byte A_, F_, B_, C_, D_, E_, H_, L_;
    public ushort IX, IY, SP, PC, WZ;
    public byte I, R, IM;
    public bool IFF1, IFF2, Halted;
    public long TotalCycles;

    public static Z80State From(Z80Cpu c) => new()
    {
        A = c.A, F = c.F, B = c.B, C = c.C, D = c.D, E = c.E, H = c.H, L = c.L,
        A_ = c.A_, F_ = c.F_, B_ = c.B_, C_ = c.C_, D_ = c.D_, E_ = c.E_, H_ = c.H_, L_ = c.L_,
        IX = c.IX, IY = c.IY, SP = c.SP, PC = c.PC, WZ = c.WZ,
        I = c.I, R = c.R, IM = c.IM,
        IFF1 = c.IFF1, IFF2 = c.IFF2, Halted = c.Halted,
        TotalCycles = c.TotalCycles,
    };

    public void Apply(Z80Cpu c)
    {
        c.A = A; c.F = F; c.B = B; c.C = C; c.D = D; c.E = E; c.H = H; c.L = L;
        c.A_ = A_; c.F_ = F_; c.B_ = B_; c.C_ = C_; c.D_ = D_; c.E_ = E_; c.H_ = H_; c.L_ = L_;
        c.IX = IX; c.IY = IY; c.SP = SP; c.PC = PC; c.WZ = WZ;
        c.I = I; c.R = R; c.IM = IM;
        c.IFF1 = IFF1; c.IFF2 = IFF2; c.Halted = Halted;
        c.TotalCycles = TotalCycles;
    }
}
