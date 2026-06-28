# Z80Core architecture

This is the design-and-rationale doc — what's in each source file, why
it's structured this way, and where to look when something's wrong.
For wire-up see [`usage.md`](usage.md); this doc is for people working
*inside* the core.

## File layout

| File | Lines | Contains |
|---|---|---|
| `Z80.cs` | ~500 | Interfaces (`IMemory`, `IIoBus`), `Z80Cpu` registers + flag constants, ALU helpers, rotate/shift helpers, `Reset`, `Step` entry point, interrupt handling |
| `Z80Main.cs` | ~620 | Unprefixed opcode table + DD/FD index-prefix entry; register read/write by 3-bit encoding |
| `Z80CB.cs` | ~150 | CB-prefixed rotates / shifts / BIT / SET / RES |
| `Z80ED.cs` | ~240 | ED-prefixed block ops (LDIR/CPIR/INIR/OTIR), 16-bit ADC/SBC, IM, IN (C) / OUT (C), NEG, RETI/RETN |
| `Z80Disassembler.cs` | ~380 | Static, side-effect-aware single-instruction disassembler |

`Z80Cpu` is a `partial sealed class` split across the four executor
files. The partial-class split lets each prefix table sit in its own
file with all its decode logic next to the cycle counts, while still
sharing private helpers (`Fetch`, `IncR`, `Push`, `Pop`,
`Add8`/`Sub8`/etc., flag-setting helpers) without re-declaring them or
making them internal.

## Opcode decoding model

Z80Core decodes opcodes by **byte structure** rather than as a 256-way
switch. Every byte breaks into:

```
   76 543 210
    x   y   z
        |   |
        p = y >> 1
        q = y & 1
```

- `x = (op >> 6) & 3` selects the major group (load / ALU / control)
- `y = (op >> 3) & 7` selects within the group (which ALU op, which condition)
- `z = op & 7` selects the register operand or the secondary group
- `p`, `q` further split `y` for cases like reg-pair encoding

This is the same scheme as Sean's
[Decoding Z80 Opcodes](http://www.z80.info/decoding.htm) reference,
which Z80Core follows verbatim. It means most of `Z80Main.cs` is
table-driven (`y`/`z` indexed switches with shared helpers) instead of
255 individual cases.

Example, the `LD r,r'` block (`x=1`, all 64 cases) is one helper call:

```csharp
case 1: // LD r, r' (or HALT when both are 6)
    if (y == 6 && z == 6) { Halted = true; return 4; }
    byte v = GetR(z, out int rc);
    SetR(y, v, out int wc);
    return 4 + rc + wc;
```

Where `GetR(int r)` and `SetR(int r, byte v)` read/write registers by
3-bit encoding (`0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A`) and return any
extra cycles for the `(HL)` case (memory access).

## DD/FD index-prefix state machine

`DD` and `FD` change subsequent register accesses: instead of `H`/`L`,
the instruction uses `IXH`/`IXL` (for DD) or `IYH`/`IYL` (for FD), and
when `(HL)` is encoded it becomes `(IX+d)` / `(IY+d)` with a
displacement byte fetched after the opcode.

The core models this as a single `_idx` field on the `Z80Cpu`:

- `_idx == 0` — no prefix (HL mode)
- `_idx == 1` — DD prefix active (IX mode)
- `_idx == 2` — FD prefix active (IY mode)

`Step()` sees the prefix as an ordinary opcode, sets `_idx`
accordingly, then calls into the main executor — which now reads
through `GetR(int r)` and gets `IXH`/`IXL` back from `r=4`/`r=5`, and
fetches the displacement byte from `GetHLorIdxWithDisp` when `r=6`.
At the end of the instruction `_idx` is reset to 0.

### The two-displacement problem

Read-modify-write opcodes on `(IX+d)` access the same memory cell twice
(read old value, modify, write new value) but the displacement byte
appears in the instruction stream **once**. A naive implementation
that calls `GetHLorIdxWithDisp` twice would Fetch from the stream
twice and corrupt the decode.

Z80Core handles this with `IdxAddrFetchD()`: fetch the displacement
**once**, compute the effective address, and pass it through to both
the read and the write phase. RMW opcodes use this; ordinary
single-access opcodes use `GetHLorIdxWithDisp`.

## WZ (MEMPTR)

The internal **WZ** register (a.k.a. MEMPTR) is the Z80's hidden
16-bit address latch. It captures the effective address of the last
memory access made by certain instructions. The reason it matters: the
**undocumented Y/X flags** for `BIT n,(HL)`, `CPI`/`CPIR`, `OUTI`/`INI`
and friends are derived from bits of `WZ`, not from the result. ZEXALL
specifically tests this behaviour.

Z80Core updates `WZ` in the same places real silicon does. If you're
hacking the executor and a ZEXALL test fails on `BIT n,(IX+d)` while
ZEXDOC passes, suspect a missing `WZ = …` update.

## Cycle accounting

Every opcode returns its T-state cost as an `int`. `Step()` accumulates
the cost into `TotalCycles` and returns it to the host.

For opcodes whose cost depends on operand mode (e.g. `LD A,(HL)` is 7 T,
`LD A,(IX+d)` is 19 T), the helpers thread an `out int extraCycles`
back to the caller, which adds the extra. This avoids duplicating the
register-encoding switch inside each opcode case.

For repeating block ops (`LDIR`, `CPIR`, `INIR`, `OTIR` and friends),
the cost is 21 T per iteration when `BC ≠ 0` and 16 T on the final
iteration. The executor returns the total for the iteration it just
performed; the `PC` is left at the prefix byte until `BC` reaches zero,
so the next `Step()` re-enters the same opcode and does another
iteration. This is what real silicon does — interrupts can be taken
between iterations.

## Interrupt handling

`Step()` checks for a pending interrupt **at the top** before
executing anything. If `IFF1` is set and a request is pending, the
interrupt is taken, the appropriate vector or ISR address is jumped
to, and `Step()` returns the interrupt acknowledge cycle cost (13 T
for IM 0/1, 19 T for IM 2). The opcode at the original PC is **not**
executed this step — the next `Step()` will fetch from the ISR.

`Halted == true` is treated as "executing NOPs at 4 T per step" until
an interrupt fires (which also clears `Halted` and bumps `PC` past the
HALT instruction, per Z80 hardware behaviour).

The `_intRequested` flag is set by `RequestInterrupt()` and cleared by
either `ClearInterrupt()` or by the interrupt actually being taken.
Edge-triggered, not level-triggered: peripherals should call
`RequestInterrupt()` once on the IRQ assertion edge, not every cycle
that the line is held low.

## Flag handling

Each ALU helper (`Add8`, `Sub8`, `Inc8`, `Dec8`, `And8`/`Or8`/`Xor8`,
`Add16`, `Adc16`, `Sbc16`, `Daa`) rebuilds `F` from scratch using bit
operations on the 8/16-bit result. Parity flag for the logical ops
uses a precomputed 256-entry `ParityTable` populated by the static
constructor.

The S/Z/Y/X flag bits track the **result byte** (high bit, zero, and
the two "undocumented" bits 5 and 3). H (half-carry) and V (overflow,
shared with Parity) are computed from the operand and result. C
(carry) comes from bit 8 / bit 16 of the wider intermediate. N (add /
subtract) is forced to 1 for subtractive ops.

For `DAA`, the algorithm follows the Z80 hardware reference: a
correction value of 0x06 / 0x60 (or both) is applied to A depending
on the half-carry, carry, and current value of A, with the N flag
selecting between add and subtract correction. See `Z80.cs` for the
implementation.

## ZEXDOC vs ZEXALL

- **ZEXDOC** tests documented flag behaviour. Easy to pass for a
  reasonably careful implementation.
- **ZEXALL** also tests **undocumented** Y/X flag bits, including
  those derived from WZ/MEMPTR.

Z80Core passes both. The undocumented flag handling is verified, but
the cleanest way to be sure your modifications haven't regressed it is
to run ZEXALL after any change to the executor — it'll catch a wrong
flag value with high probability within minutes.

See [`zex-validation.md`](zex-validation.md) for how to run the
bundled harness.

## Historical notes

A couple of bugs that were caught only because they specifically
broke higher-level use cases and not toy tests:

- **Indexed INC/DEC affecting flags incorrectly (2026-05-23)** —
  `INC (IX+d)` / `DEC (IY+d)` were computing flags from the wrong
  source. Broke S-BASIC's floating-point display path (`PRINT 1.5`
  produced wrong output) and Star Trek (`trek.mzf`) crashed during
  combat. ZEXDOC didn't catch this because the affected encoding
  isn't covered separately from the non-indexed path. Fix: thread the
  indexed displacement correctly into the Inc8/Dec8 wrapper.

The lesson, which generalises: an opcode test suite catches encoding
errors and flag-value errors, but it can't catch errors in the
*plumbing* between encoding and execution. Running a real program
that exercises floating-point or block math is the strongest signal
that the wire-up is right.

## References

- Sean Young, [The Undocumented Z80 Documented](http://www.z80.info/zip/z80-documented.pdf) — the bible.
- Sean Riddle, [Decoding Z80 Opcodes](http://www.z80.info/decoding.htm) — the x/y/z/p/q decomposition.
- Zilog Z80 CPU User Manual — official documented behaviour.
- Frank Cringle, [ZEXDOC / ZEXALL](https://mdfs.net/Software/Z80/Exerciser/) — the exercisers.
