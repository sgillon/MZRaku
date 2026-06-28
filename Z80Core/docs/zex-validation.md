# Validating Z80Core with ZEXDOC / ZEXALL

The most reliable way to prove a Z80 implementation is correct is to
run it against Frank Cringle's **ZEX** test programs. They drive
hundreds of thousands of instances of every documented (ZEXDOC) and
documented+undocumented (ZEXALL) instruction, compare CRC checksums of
the results against the values produced by real silicon, and report
"OK" or "ERROR" per opcode group.

Z80Core ships a console-app harness — `samples/ZexHarness/` — that
runs either program against the core and prints the CRC results to
stdout.

## Running

From the repo root:

```
dotnet run --project samples/ZexHarness -- zexdoc.com
dotnet run --project samples/ZexHarness -- zexall.com
```

You'll see output that looks roughly like:

```
Z80 instruction exerciser
<adc,sbc> hl,<bc,de,hl,sp>....  OK
add hl,<bc,de,hl,sp>..........  OK
add ix,<bc,de,ix,sp>..........  OK
...
Tests complete
```

Every line should end in `OK`. A line ending in `ERROR <expected> <got>`
means that opcode group produced the wrong CRC — i.e. some
combination of operands and starting flags produced a result different
from what real silicon does. The expected/got CRCs are 32-bit hex.

ZEXDOC runs to completion in ~6–10 minutes on a modern machine.
ZEXALL is a few times slower (it tests more cases) — budget 30 minutes
to an hour.

## What the harness does

`samples/ZexHarness/Program.cs` builds a minimal CP/M-style host:

- A flat 64 KB RAM as `IMemory`. No banking, no MMIO. Plain `byte[]`.
- A no-op `IIoBus` that returns `0xFF` on read and discards writes.
  ZEX doesn't use IN/OUT.
- The `.com` file is loaded at `$0100` (CP/M TPA).
- A two-instruction trampoline at `$0000`–`$0007`:
  - `$0000`: `HLT` — CP/M's WBOOT vector. ZEX returns here when done.
  - `$0005`: `JP $E000` — CP/M's BDOS vector. Real CP/M would route to
    the OS; we intercept it before execution via a `PreStep` hook.
  - The word at `$0006`–`$0007` reads as `$E000` because ZEX uses
    `LD HL,(6) / LD SP,HL` to find the top of usable memory.
- `cpu.Reset()`, then `PC = $0100`, `SP = $E000`, `IM = 1`, interrupts
  off.
- The `PreStep` hook fires whenever `PC == $0005` and emulates two
  BDOS functions:
  - **Function 2** (`C = 2`, char in `E`): write the character to
    stdout.
  - **Function 9** (`C = 9`, `$`-terminated string at `DE`): write the
    string to stdout.
  - Other BDOS functions are silently ignored — ZEX doesn't call them.
  - The hook synthesises a `RET` (pop return address into PC) and
    returns `true`, skipping the `JP $E000` that's actually at `$0005`.
- When `PC` lands on `$0000` (program exit), the run loop stops.

That's the entire harness. About 100 lines of C# plus the bundled
`.com` files. It's a working example of `IMemory` / `IIoBus` /
`PreStep` use, in addition to a regression test for the core.

## Confirming a clean run

For an automated check (CI, regression sweep), grep the output for
`ERROR`. Empty grep → all tests pass:

```bash
dotnet run --project samples/ZexHarness -- zexdoc.com | tee zexdoc.log
grep ERROR zexdoc.log && echo "FAIL" || echo "PASS"
```

Or just look at the last line — ZEX prints `Tests complete` only after
running every test, regardless of pass/fail, so its presence means the
suite at least ran to completion.

## Why both ZEXDOC and ZEXALL

- **ZEXDOC** tests only documented flag behaviour. A core that passes
  ZEXDOC will run real Z80 software correctly *as long as* that
  software doesn't rely on the undocumented bits 3 and 5 of `F`.
- **ZEXALL** tests both documented and undocumented behaviour,
  including flag bits that aren't named in the Zilog manual but are
  produced by the actual silicon. Some games and demos do depend on
  these.

If you're starting fresh, pass ZEXDOC first — it's a much smaller
target and catches the obvious wrong-flag bugs. Once it's clean, move
to ZEXALL for the full picture.

## About the bundled binaries

`samples/ZexHarness/zexdoc.com` and `zexall.com` are Frank Cringle's
original distributions, unmodified. They are licensed under the
**GNU GPL v2** (see `samples/ZexHarness/Copying`).

The Z80Core library and `Program.cs` of the harness itself are
**MIT-licensed**, like the rest of this project. The GPL'd `.com`
files are guest programs the harness **runs**, not code the harness
*links against* — they share the address space at runtime but not
the build. This is "mere aggregation" under GPL v2 §2 and has no
impact on the library's licence.

If you'd rather not ship the GPL binaries, the harness will run any
ZEX-conformant `.com` file (or any CP/M `.com` that uses only BDOS
functions 2 and 9). You can rebuild them from the Cringle source if
you want a "clean" copy, or strip the harness entirely from your
distribution and run it as a separate dev tool.
