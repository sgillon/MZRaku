# Wiring Z80Core into a host

This is the practical "how do I make this drive a machine" guide. For
the internal design of the core itself, see
[`architecture.md`](architecture.md). For debugger-tooling hooks
(breakpoints, traces, ROM traps) see
[`debugger-hooks.md`](debugger-hooks.md).

## The wire-up surface in one paragraph

You implement two interfaces — `IMemory` for the 16-bit address space
and `IIoBus` for the 8-bit I/O space (`IN`/`OUT`). You construct a
`Z80Cpu`, assign your `IMemory` and `IIoBus` to its `Mem` and `Io`
fields, call `Reset()`, then drive `Step()` in a loop. The core never
sleeps, never calls back into your host except through those two
interfaces, and never reaches outside them.

```csharp
var cpu = new Z80Cpu { Mem = myMemory, Io = myIoBus };
cpu.Reset();
while (true) cpu.Step();
```

That's the whole contract. Everything below is detail.

## Implementing `IMemory`

`Z80Cpu` calls `Mem.Read(addr)` for every opcode fetch and every memory
operand read, and `Mem.Write(addr, value)` for every memory store. That
is the *only* path it has to the 64 KB address space. So your `IMemory`
is also where you put:

- **Banked ROM / RAM**: branch on `addr` and read from whichever array
  is currently exposed. Toggle the bank state from `IIoBus.Out` (or
  from a memory-mapped control register inside `IMemory.Write`).
- **Memory-mapped I/O**: a region of the address space whose reads and
  writes go through hardware registers instead of RAM. The Z80 doesn't
  know; only your `IMemory` does.
- **Video RAM**: typically just plain RAM that your renderer also reads
  out-of-band on a frame timer.
- **Shadow writes**: e.g. writes to the ROM region land in a parallel
  RAM array that becomes visible when the bank flips. Hosts that need
  this do it inside `IMemory.Write`.

The core makes no assumption about ordering, caching, or volatility. If
a read returns 0xC9 (RET) one cycle and 0x00 (NOP) the next, that's a
host decision the core will execute as-is.

```csharp
sealed class BankedMemory : IMemory
{
    private readonly byte[] _rom = new byte[0x1000];
    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _vram = new byte[0x1000];
    public bool RomEnabled = true;
    public bool VramVisible = true;

    public byte Read(ushort a) => a switch
    {
        <= 0x0FFF when RomEnabled => _rom[a],
        >= 0xD000 and <= 0xDFFF when VramVisible => _vram[a - 0xD000],
        >= 0xE000 and <= 0xE00F when VramVisible => MmioRead(a),
        _ => _ram[a],
    };

    public void Write(ushort a, byte v)
    {
        if (a >= 0xE000 && a <= 0xE00F && VramVisible) { MmioWrite(a, v); return; }
        if (a >= 0xD000 && a <= 0xDFFF && VramVisible) { _vram[a - 0xD000] = v; return; }
        _ram[a] = v; // ROM region: shadow into RAM for when bank flips
    }

    private byte MmioRead(ushort a) { /* dispatch to PPI/PIT/etc. */ }
    private void MmioWrite(ushort a, byte v) { /* same */ }
}
```

## Implementing `IIoBus`

`Z80Cpu` calls `Io.In(port)` for `IN A,(n)` / `IN r,(C)` and
`Io.Out(port, value)` for `OUT (n),A` / `OUT (C),r`. Same shape as
memory — your wire-up code decides what those ports mean.

Note: `port` is a `ushort`. The Z80's actual I/O cycles drive the
top half of the port from `B` (for `IN r,(C)` / `OUT (C),r`) or from
`A` (for `IN A,(n)` / `OUT (n),A`); the core hands you the full 16-bit
value and you mask down as appropriate.

```csharp
sealed class IoBus : IIoBus
{
    private readonly BankedMemory _mem;
    public IoBus(BankedMemory mem) { _mem = mem; }

    public byte In(ushort port) => (port & 0xFF) switch
    {
        0xE4 => /* read keyboard column */,
        _    => 0xFF, // unmapped ports float high
    };

    public void Out(ushort port, byte value)
    {
        switch (port & 0xFF)
        {
            case 0xE0: _mem.RomEnabled = false;   break;
            case 0xE1: _mem.VramVisible = false;  break;
            case 0xE2: _mem.RomEnabled = true;    break;
            case 0xE3: _mem.VramVisible = true;   break;
        }
    }
}
```

## Driving `Step()`

`Step()` executes one instruction (including any prefix bytes — a
`DD CB d op` group counts as one `Step`) and returns its T-state cost.
For a free-running emulator you call it in a loop and let the host's
frame rendering or scheduler decide when to stop.

The pattern most emulators use is a **cycle-budgeted run-frame**: pick
a target rate (e.g. 60 Hz on a 3.5 MHz CPU → ~58,333 cycles/frame),
step until you've spent that many cycles, then render. Peripherals
that count CPU cycles (timer chips, video raster scan) get fed the
returned cycle count so they advance in lockstep.

```csharp
const int CyclesPerFrame = 58_333;

void RunFrame()
{
    int spent = 0;
    while (spent < CyclesPerFrame)
    {
        int cyc = cpu.Step();
        if (cpu.BreakpointTripped) break; // see debugger-hooks.md
        spent += cyc;

        // Advance anything that's clocked off CPU cycles:
        pit.Tick(cyc);
        joystick.Tick(cyc);
    }

    video.RenderFrame();
}
```

If your host machine has a frame interrupt (e.g. VBLANK fires an IRQ at
the start of vertical blanking), split the budget around that boundary
so the interrupt has the right cycle-accurate position within the
frame:

```csharp
const int VisibleCycles = (int)(CyclesPerFrame * 0.85);

void RunFrame()
{
    int spent = 0;
    while (spent < VisibleCycles)
    {
        int cyc = cpu.Step();
        spent += cyc;
        pit.Tick(cyc);
    }

    ppi.SetVBlank(true);          // raise VBLANK signal
    cpu.RequestInterrupt();        // and optionally fire IRQ

    while (spent < CyclesPerFrame)
    {
        int cyc = cpu.Step();
        spent += cyc;
        pit.Tick(cyc);
    }

    ppi.SetVBlank(false);
    video.RenderFrame();
}
```

For a real worked example, see
[MZRaku's `MZ700.RunFrame`](https://github.com/sgillon/MZRaku/blob/main/MZ700.cs)
(lines ~137–198): it splits the frame at 85% / 15% for visible/VBLANK,
breaks early on a debugger breakpoint, and feeds the PIT (programmable
interval timer) the per-step cycle count.

## Interrupts

The Z80 has two interrupt inputs (NMI and INT). Z80Core currently
models **maskable INT only**, in any of the three interrupt modes
(`IM 0`, `IM 1`, `IM 2`).

The contract is edge-triggered: your peripheral calls
`cpu.RequestInterrupt()` once when its IRQ asserts; the core handles
the interrupt at the top of the next `Step()` *if* `IFF1` is set, and
clears the pending flag automatically. If the CPU has interrupts
disabled (`DI`), the request remains latched and fires the first time
the program issues `EI` followed by an instruction.

```csharp
// In a peripheral's tick:
if (counterReachedZero && !_lastIrqState)
{
    cpu.RequestInterrupt();
}
_lastIrqState = counterReachedZero;
```

To revoke a pending request (e.g. the peripheral was reset before the
CPU got round to acknowledging), call `cpu.ClearInterrupt()`.

In `IM 2`, the CPU reads a vector word from `(I:0xFF)` — i.e. the
high byte of the vector address is `I` and the low byte is `0xFF`
(the core doesn't model a device-supplied vector byte; it uses `0xFF`,
which is the standard behaviour for systems without one).

## Reset and run from a clean slate

`cpu.Reset()` zeros / un-initialises everything the Z80 hardware reset
does:

- `A = F = B = C = ... = 0xFF` (main set)
- All alternate registers `= 0`
- `IX = IY = 0xFFFF`
- `SP = 0xFFFF`
- `PC = 0x0000`
- `I = R = 0`
- `IFF1 = IFF2 = false`
- `IM = 0`
- `Halted = false`
- `TotalCycles = 0`
- Breakpoint state cleared

After `Reset()`, the CPU will fetch its first instruction from
`Mem.Read(0)`. Most ROM-based machines put their boot ROM there.

## Building Z80Core into your project

Z80Core is intentionally not on NuGet. Pick one of:

- **Git submodule** (recommended for multi-project workspaces):
  ```
  git submodule add https://github.com/sgillon/Z80Core.git Z80Core
  ```
  Then add a `<ProjectReference>` to `Z80Core/Z80Core.csproj` from your
  host `.csproj`. If your host project lives in the same directory as
  the submodule, add `<Compile Remove="Z80Core/**/*.cs" />` and
  `<None Remove="Z80Core/**/*" />` to your host `.csproj` so the SDK's
  default glob doesn't try to double-compile the library's sources.

- **Sibling clone**: clone both repos side-by-side and use
  `<ProjectReference Include="..\Z80Core\Z80Core.csproj" />`.

- **Vendored copy**: drop the four `Z80*.cs` files into your project
  and call it done. Lowest ceremony; you re-copy when the upstream
  fixes something.

## Things you might want to do beyond the basics

- **Trap a ROM entry point** — e.g. replace the tape-load routine with
  a host implementation that injects bytes into RAM directly. See
  [`debugger-hooks.md`](debugger-hooks.md#prestep) for the `PreStep`
  hook.
- **Add breakpoints / step / pause** for a debugger. See
  [`debugger-hooks.md`](debugger-hooks.md#breakpoints).
- **Render a disassembly view** without triggering side-effect reads
  on MMIO ports. See [`disassembler.md`](disassembler.md).
- **Validate your wire-up against ZEXDOC** — run
  `samples/ZexHarness/` against your host's `IMemory`/`IIoBus`
  implementations. See [`zex-validation.md`](zex-validation.md).
