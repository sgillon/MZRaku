# Disassembler

`Z80Disassembler` is a static, single-instruction disassembler. Give
it an `IMemory` and an address, and it returns the formatted mnemonic
plus the number of bytes consumed.

```csharp
var result = Z80Disassembler.Disassemble(mem, addr: 0x0100);
Console.WriteLine($"{result.Text}  ({result.Length} bytes)");
// e.g. "LD HL,$1234  (3 bytes)"
```

To disassemble a range, walk forward by `result.Length` after each
call:

```csharp
ushort pc = 0x0100;
for (int i = 0; i < 10; i++)
{
    var r = Z80Disassembler.Disassemble(mem, pc);
    Console.WriteLine($"${pc:X4}: {r.Text}");
    pc = (ushort)(pc + r.Length);
}
```

## Decoding model

Same x/y/z/p/q form as the executor — see
[`architecture.md`](architecture.md). Most of the disassembler is
table lookups against the same encoding, so a fix to the executor's
decode model tends to land in both places at once.

Prefix handling:

- A `DD` or `FD` byte switches subsequent `H`/`L` into `IXH`/`IXL` /
  `IYH`/`IYL` and `(HL)` into `(IX+d)` / `(IY+d)`. Multiple `DD`/`FD`
  bytes are tolerated; the last one wins (this matches Z80 silicon).
- A `CB` byte selects the rotate / bit-test table. Combined `DD CB d
  op` and `FD CB d op` forms are decoded with the displacement in the
  middle, as the silicon expects.
- An `ED` byte selects the block-op / 16-bit ALU table.

## Side-effect-aware reads

The disassembler reads from `IMemory` to fetch the opcode and its
operand bytes. For most addresses that's fine — RAM and ROM are pure.
But some hosts memory-map I/O ports into the address space, and those
ports do things when you read them: counters latch, keyboard scan rows
shift, status flags self-clear.

If a debugger UI re-renders its disassembly view every frame and that
view contains addresses inside the MMIO region, the disassembler would
poke those ports thousands of times per second and corrupt the
emulator's state. So `Disassemble` takes an optional predicate:

```csharp
public static Result Disassemble(
    IMemory mem,
    ushort  addr,
    Func<ushort, bool>? isSideEffectAddr = null);
```

When `isSideEffectAddr` is provided and returns `true` for a particular
address, the disassembler treats the byte at that address as `0x00`
instead of calling `mem.Read(addr)`. The disassembly will be slightly
wrong for those bytes (it'll decode the MMIO bytes as a stream of
`NOP`s) but the live emulator state is preserved.

```csharp
// MZRaku example: the MZ-700 maps PPI / PIT / sound / joystick / VBLANK
// status into $E000-$E00F. Reading any of those addresses latches
// counters or shifts state.
static bool IsMzIoWindow(ushort a) => a >= 0xE000 && a <= 0xE00F;

var result = Z80Disassembler.Disassemble(mem, addr, IsMzIoWindow);
```

If your host has no MMIO at all (ports are exclusively in the I/O
space via `IN`/`OUT`), pass `null` or omit the parameter — the
disassembler will read freely.

## What you don't get

`Z80Disassembler` is **just** a decoder. It doesn't symbolise
addresses, look up subroutine names, follow jumps, or know anything
about the program structure. If you want labels and cross-references,
that's a layer on top of this one. The intent is to give you a
reliable mnemonic for a single instruction and let your debugger /
profiler / trace formatter compose whatever it needs above that.
