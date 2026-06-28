# Debugger hooks

Z80Core exposes a few optional features that consumers building
debuggers, profilers, ROM-trap shims, or test harnesses can use without
having to fork the core. None of them are required for normal
emulation — leave them untouched and the core just runs.

## Breakpoints

`Z80Cpu.Breakpoints` is a public `bool[0x10000]`. Set
`cpu.Breakpoints[addr] = true` to arm a breakpoint at `addr`. Set it
back to `false` to clear.

When `Step()` is called with `Breakpoints[PC] == true`, it **declines
to execute** that instruction:

- `BreakpointTripped` is set to `true`
- `Step()` returns `0`
- `PC` is left pointing at the un-executed instruction

Your run loop should check `cpu.BreakpointTripped` after each `Step()`
and stop the frame:

```csharp
while (cyclesSpent < frameTarget)
{
    int cyc = cpu.Step();
    if (cpu.BreakpointTripped) break;
    cyclesSpent += cyc;
}
```

### Stepping off a breakpointed instruction

If you just clear `BreakpointTripped` and call `Step()` again, it'll
immediately re-trip — `PC` is still on the breakpoint. To step off
without disarming the breakpoint (you want it to fire again next time
PC returns), set `cpu.IgnoreBreakpointOnce = true` and call `Step()`:

```csharp
public void ResumeFromBreakpoint()
{
    cpu.IgnoreBreakpointOnce = true;
    cpu.BreakpointTripped = false;
    // Now the run loop will execute one instruction past PC, then
    // honour breakpoints again from the next instruction onwards.
}
```

This is the standard "continue from a breakpoint" pattern. The
`IgnoreBreakpointOnce` flag is auto-cleared after one `Step()`.

## PC trace ring buffer

For post-mortem traces of what the CPU was doing just before something
interesting happened (a crash, an unexpected jump, a stuck loop),
`Z80Cpu` keeps a rolling buffer of the last 256 PC values:

```csharp
public readonly ushort[] PcTrace = new ushort[256];
public int  PcTraceIdx;
public bool PcTraceEnabled;
public bool PcTraceFrozen;
```

Set `PcTraceEnabled = true` to start recording. The buffer is a true
ring — `PcTraceIdx` is the *next write position*, so the oldest entry
is at `PcTraceIdx` (wrapping), the newest at `(PcTraceIdx - 1) & 0xFF`.

Recording costs one array write per `Step()`; not free but cheap. Turn
it off in shipped builds if every cycle counts.

To freeze the buffer at a moment of interest (e.g. when a breakpoint
trips) without disabling recording entirely, set `PcTraceFrozen = true`.
The buffer keeps its current contents until you set it `false` again.

```csharp
// Dump trace in chronological order, oldest first.
void DumpTrace()
{
    cpu.PcTraceFrozen = true;
    for (int i = 0; i < 256; i++)
    {
        ushort pc = cpu.PcTrace[(cpu.PcTraceIdx + i) & 0xFF];
        Console.WriteLine($"  ${pc:X4}");
    }
    cpu.PcTraceFrozen = false;
}
```

## PreStep

The `Func<bool>? PreStep` hook is the most powerful tool in the box.
It's called at the top of `Step()` (after interrupt-check but before
the instruction fetch). If it returns `true`, the fetch is **skipped**
for this step and `Step()` charges a token 4 T-states. If it returns
`false` (or isn't set), `Step()` proceeds normally.

The use case is *replacing* whole instructions, subroutines, or
whole ROM routines with native C# implementations:

```csharp
cpu.PreStep = () =>
{
    if (cpu.PC == 0x0436)        // tape load entry
    {
        InjectTapeLoadResult();   // host writes the result into RAM
        cpu.PC = PopReturnAddr(); // synthesise RET
        return true;
    }
    return false;
};
```

The hook gets full mutable access to `cpu` (and through it your `Mem`
and `Io`), so it can set up state, synthesise return values, and rewrite
`PC`. Returning `true` means "the cycle was spent doing what the
original instruction would have done, just faster and in C#"; the
core just keeps going.

### Worked example: cassette ROM trap

The Sharp MZ-700 ROM has a tape-load routine that polls a 1-bit input
line for a kilobyte of header data, decoding bit timings as it goes.
Emulating that one bit at a time is fine but slow, and decouples
loading speed from real-tape timing for no benefit.

MZRaku installs a `PreStep` that fires when `PC` hits one of the tape
routine entry points (`$0436` read header, `$04D8` read data, `$0D47`
write tape), reads the bytes from the host-side cassette image, drops
them into the RAM buffer the ROM was going to fill, and synthesises
the `RET` that the ROM was going to execute. The ROM never sees its
tape line; it just finds the data already in memory and returns.

```csharp
// Sketch — see MZRaku's Cassette.cs for the real thing.
public bool OnPreStep()
{
    var cpu = _machine.Cpu;
    if (cpu.PC == 0x0436) { InjectHeader(); SynthRet(cpu); return true; }
    if (cpu.PC == 0x04D8) { InjectData();   SynthRet(cpu); return true; }
    if (cpu.PC == 0x0D47) { CaptureBlock(); SynthRet(cpu); return true; }
    return false;
}

private static void SynthRet(Z80Cpu cpu)
{
    byte lo = cpu.Mem.Read(cpu.SP++);
    byte hi = cpu.Mem.Read(cpu.SP++);
    cpu.PC  = (ushort)((hi << 8) | lo);
}
```

### Worked example: CP/M BDOS trap

The `samples/ZexHarness/` console app uses `PreStep` to emulate the
two CP/M BDOS functions that ZEXDOC needs (`2` = console char out,
`9` = print `$`-terminated string), without ever needing a real CP/M
ROM. PC hits `$0005`, the host reads `C` (BDOS function number),
emits to stdout, and synthesises `RET`.

This is the cleanest demonstration of why `PreStep` is useful — the
**entire** CP/M runtime that ZEX needs is a 30-line C# method. See
`samples/ZexHarness/Program.cs`.

## Inspecting state

Z80Core makes its registers public for direct inspection. There's no
property layer or copy step — what you see on `cpu` *is* the live
state:

- 8-bit registers: `A F B C D E H L` and primes `A_ F_ B_ C_ D_ E_ H_ L_`
- 16-bit pairs (read/write): `AF BC DE HL IX IY SP PC` and `AF_ BC_ DE_ HL_`
- Index halves: `IXH IXL IYH IYL`
- Interrupt state: `I R IFF1 IFF2 IM`
- Status: `Halted TotalCycles`
- WZ (MEMPTR): `WZ`

Writing to these from outside `Step()` is safe — Z80Core won't fight
you. Modifying them from inside `PreStep` is part of the contract
(it's how the ROM-trap pattern works).

If you need to save/restore a full snapshot (for save states, test
fixtures, etc.) just copy every field into your own struct/class.
There's nothing hidden — `TotalCycles`, `WZ`, and the breakpoint
arrays are part of the state too if you care about exact
reproducibility.
