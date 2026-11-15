# Tape traps

How MZRaku loads and saves cassettes without emulating a tape signal:
every trap on every machine, what each one checks, and what it hands
back to the ROM. This page is for someone changing the code; see
[`architecture.md`](architecture.md) for where it fits.

It describes v1.5.0.

## The idea

MZRaku has no tape deck. When the machine's own tape code is about
to read or write a block, MZRaku steps in at the start of that code,
does the transfer in C#, and returns to the caller as the routine
would after a good read or write. The program never knows.

The hook is Z80Core's `PreStep`: a function the CPU calls before
every instruction. Each machine sets it to its cassette's
`OnPreStep()` (the MZ-800 notes BASIC starting first). If the PC is at
a trap address, and the trap's conditions hold, the trap does its work,
changes the registers and PC, and returns true; the CPU then skips
that instruction fetch and counts 4 cycles.

| File | What it holds |
|---|---|
| `Core/Tape/CassetteTrapBase.cs` | Shared by the three machines: the pending image, header and data delivery, the success return, SAVE to `.mzf` |
| `Core/Tape/MzfImage.cs` | The `.mzf` format: a 128-byte header, then the data |
| `Core/Tape/CassetteFile.cs` | Reads `.mzf`, `.m12` and `.mzt` files and zips of them; `ReadTape` returns every file in order |
| `Core/MZ700/Mz700Cassette.cs` | 1Z-013A monitor and S-BASIC (1Z-013B) traps |
| `Core/MZ80A/Mz80aCassette.cs` | SA-1510 traps (used by SA-5510 BASIC too), and SA-5510 program injection |
| `Core/MZ800/Mz800Cassette.cs` | 1Z-013B monitor (MZ-700 mode) and 1Z-016 BASIC traps, and the mounted tape |

### The header

Every machine reads the 128-byte header into `$10F0`–`$116F`, in the
`.mzf` layout:

| Offset | Address | What |
|---|---|---|
| `$00` | `$10F0` | Type: `01` machine code, `02` MZ-80A BASIC, `03` BASIC data, `04` ASCII, `05` MZ-700/MZ-800 BASIC, `A0`/`A1` Pascal |
| `$01`–`$11` | `$10F1` | File name, up to 16 characters, ended by `$0D` |
| `$12` | `$1102` | Data size |
| `$14` | `$1104` | Load address |
| `$16` | `$1106` | Execution address |
| `$18`–`$7F` | `$1108` | Comment |

### The success return

`CassetteTrapBase.SynthesiseSuccess()` is what a good read or write
looks like to the caller: carry clear (CY=0 means success on all
three monitors), the return address popped off the stack, the PC set
to it. Each trap adds what its own routine's exit does: interrupts
back on (the 1Z-013A and 1Z-013B routines end with EI), A=0, BASIC's
tape flag cleared, a saved register popped.

### Checking the code is the code

A trap address is only a trap while the right code is there. The
traps check one or both of:

- **The memory map.** The monitor traps only fire with the monitor
  ROM at `$0000` (`Mz700Memory.RomEnabled`, `Mz800Memory.RomLow`,
  `!Mz80aMemory.RomSwapped`). With RAM there, the same address is
  someone else's code: under S-BASIC, `$0436` is in the keyboard
  decoder.
- **The bytes.** Most traps compare the instructions at (or near) the
  trap with what the known ROM or BASIC has there, so a different
  BASIC, or a program loaded over BASIC, is left alone.

## Loading

A cassette is inserted with `Queue` (one image: `CassetteTrapBase.Pending`)
or, on the MZ-800, `Mount` (a whole tape). The header trap writes the
header to `$10F0`; the data trap writes the data where the header (or
the caller) says, then clears `Pending` so a second LOAD doesn't read
the same file again. `HeaderTrapHits` and `DataTrapHits` count the
hits; the front end's TAPE indicator flashes when they change.

### MZ-700

| Code | Address | Checks | Does | Returns |
|---|---|---|---|---|
| 1Z-013A RDINF (header) | `$04D8` | Monitor ROM in; a cassette inserted; header not yet read | Header to `$10F0` | CY=0, pop, EI |
| 1Z-013A RDDAT (data) | `$04F8` | As above; data not yet read | Data to the load address and size in `$10F0` (header first if it wasn't read); cassette cleared | CY=0, pop, EI |
| S-BASIC read header | `$0A7A` | ROM out; a cassette inserted; bytes `LD A,$CC; JR` | BC bytes of the header to HL; VRAM banked out | A=0, flag `$0050` cleared, CY=0, pop, EI |
| S-BASIC read data | `$0A7E` | ROM out; a cassette inserted; bytes `LD A,$53; PUSH DE; LD D,$D2` | BC bytes of the data to HL; VRAM banked out; cassette cleared | As above |

The monitor's jump table sends RDINF (`$0027`) to `$04D8` and RDDAT
(`$002A`) to `$04F8`; the monitor's L command calls both. S-BASIC
banks the monitor out and keeps its own tape code, with its own jump
table at the same vectors (`$0027` → `$0A7A`, `$002A` → `$0A7E`), so
it needs traps of its own. A read with BC=0 is left to the real
routine, which returns at once. The S-BASIC reads write straight to
RAM and leave VRAM banked out, as the real routine does, so a long
program's tail lands in RAM rather than on the screen.

### MZ-80A

| Code | Address | Checks | Does | Returns |
|---|---|---|---|---|
| SA-1510 header read | `$04CF` | SA-1510 not swapped out; bytes `DI; PUSH DE; PUSH BC; PUSH HL`; a cassette inserted; header not yet read | Header to `$10F0` | CY=0, pop |
| SA-1510 data read | `$04EF` | As above; header read; data not yet read | Data to the address and size in `$1104` / `$1102`; cassette cleared | CY=0, pop |

These are SA-1510's read code, not its jump-table entries (RDINF
`$0027` → `$04CF`, RDDAT `$002A` → `$04EF`), because the monitor's L
command calls them directly. SA-5510 BASIC calls the jump table with
SA-1510 still at `$0000`, so one pair of traps serves both. The data
goes where `$1104` says, not where the file's header said: SA-5510's
LOAD sets `$1104` to `$505C`, its program start, after the header is
read.

### MZ-800

The MZ-800 keeps a whole tape mounted: every file of a zip or `.mzt`,
and a head position. `Pending` is the file under the head. A header
read on a file whose data was never read moves past it first, which
is how a named LOAD skips files it doesn't want on a real tape.

| Code | Address | Checks | Does | Returns |
|---|---|---|---|---|
| Monitor ROM header read | `$04D8` | ROM at `$0000` (`RomLow`); a file under the head | Header to `$10F0` | CY=0, pop, EI |
| Monitor ROM data read | `$04F8` | As above | Data to the load address and size in `$10F0`; head moves to the next file | CY=0, pop, EI |
| 1Z-016 BASIC read entry | `$3872` | RAM at `$0000`; the 13 bytes at `$386C` match 1Z-016's read entry | A=`$CC` header, else data: BC bytes to HL. With no file under the head, the real routine runs on and waits for PLAY | A=0, CY=0, pop |
| 1Z-016 BASIC, tape inserted while waiting | anywhere in `$3881`–`$3B9F` | A read started with no file and one is now mounted | The read it was waiting for | A=0, CY=0, SP=`$10EE`, jump to BASIC's exit at `$385F` |

The IPL's C option and the monitor's L command (M at the IPL menu;
the L code is at `$EB54`) call `$04D8` and `$04F8`, the tape routines
of the MZ-700-mode monitor ROM at `$0000`. 1Z-016 BASIC doesn't call
them: it reads the tape bit by bit itself, with RAM at `$0000`, from
one entry shared by header and data reads. Its exit at `$385F` puts
back BASIC's stack and state, which is why the waiting case jumps
there rather than popping.

## Saving

SAVE is trapped where the write routine is about to send a block to
tape, after the ROM has printed `WRITING "name"`. The trap copies the
block and gives it to `CassetteTrapBase.TakeSaveBlock`: a header block
is kept, and the data block after it completes a `.mzf` in `saves\`
next to the executable, named after the header's file name (with
`-1`, `-2`… rather than overwrite). `WriteTapeTrapHits` counts files
written, and `OnSaved` reports them.

| Machine | Code | Address | Checks | Does | Returns |
|---|---|---|---|---|---|
| MZ-700 | S-BASIC write body | `$0A67` | ROM out; the bytes `DI; CALL $0B5A; CALL NC,$0B85` | Takes BC bytes at HL; E=`$CC` header, `$53` data | DE popped, flag `$0050` cleared, A=0, CY=0, pop, EI |
| MZ-80A | SA-1510 RECORD.PLAY wait | `$0447` | SA-1510 not swapped out; D=`$D7` (a write); the bytes there | Goes on as if RECORD.PLAY were down, so the ROM still prints `WRITING` | CY=0, continue at `$044A` |
| MZ-80A | SA-1510 write body | `$045E` | As above | Header: 128 bytes at `$10F0`; data: the address and size in `$1104` / `$1102` | A=0, CY=0, continue at SA-1510's exit `$0464` |
| MZ-800 | 1Z-016 sense-line poll | `$38D0` | D=`$D7`; the poll's bytes and the write entry's 17 bytes at `$382E` | Goes on as if the deck were running | Continue at `$38DD` |
| MZ-800 | 1Z-016 write body | `$3858` | D=`$D7`; the body's bytes and the write entry's | Takes BC bytes at HL; E=`$CC` header, `$53` data | A=0, CY=0, continue at BASIC's exit `$385F` |

SA-1510's write (WRINF `$0021` → `$0436`, WRDAT `$0024` → `$0470`)
is shared by the monitor and SA-5510; SA-1510 has no save command of
its own, so in practice it is SA-5510's SAVE.

## Loads that don't use the traps

Loading from the File menu, by drag and drop or from the command line
puts the file straight into RAM instead, once the ROM (and BASIC, if
needed) is ready. `AutoLoadOrchestrator` does this, using:

- `CassetteTrapBase.DirectInject`: header to `$10F0`, data to the
  load address, then optionally jump to the execution address. Used
  for machine code on all three machines. The MZ-700 version writes
  straight to RAM and banks VRAM out if the data runs past `$CFFF`.
- MZ-700 BASIC programs: `DirectInject` without the jump, then
  `Mz700Cassette.FixupBasicProgramPointers` (S-BASIC's text start at
  `$6ABF` and end pointers at `$6AB3`–`$6AB8`), then RUN is typed.
- MZ-80A SA-5510 programs: `Mz80aCassette.InjectBasicProgram` does
  what LOAD does after reading (load at `$505C`, relink the lines,
  move the variable table at `$4E4E` up past the program), without
  touching SA-1510's key-scan state at `$1164`+; then RUN is typed.
  Other BASIC types go through the traps: the cassette is inserted
  and LOAD typed, then RUN once the data trap has fired.
- MZ-800 BASIC programs: the tape is mounted and `LOAD` and `RUN` are
  put in 1Z-016's key buffer (`MZ800.TypeIntoBasic`), so BASIC's own
  LOAD reads it through the traps.

File → Insert cassette (`--insert`) only inserts or mounts; the user
types the LOAD.

## Not trapped

- The MZ-700 monitor's own save (WRINF `$0436`, WRDAT): only S-BASIC's
  SAVE is taken.
- The MZ-800 monitor's save (M at the IPL menu), and the MZ-700's S-BASIC run on
  the MZ-800 in MZ-700 mode (neither its LOAD nor its SAVE).
- VERIFY on any machine.

## Differences worth knowing

Written down for whoever next works on tape handling; none of these
is a bug today.

- **When a read is served.** The MZ-700 and MZ-80A traps act only at
  a routine's entry, so the cassette must be inserted before LOAD
  runs. The MZ-800's BASIC trap also serves a tape inserted while
  LOAD waits for PLAY.
- **What is checked.** The MZ-700 and MZ-800 monitor traps check the
  memory map only; the MZ-80A's and all the BASIC traps check bytes
  too.
- **One image or a tape.** The MZ-700 and MZ-80A hold one image; the
  MZ-800 holds a whole tape.
- **Where data goes.** The MZ-700 and MZ-800 monitor traps read the
  load address and size from the header at `$10F0`; the MZ-80A reads
  `$1104` / `$1102`, which SA-5510 changes; the BASIC traps use HL and
  BC.
- **Status messages.** Each machine words its load message
  differently, and the MZ-800's monitor trap still reports
  diagnostic detail (return address, mode, bank) from v1.3.0's
  blank-screen hunt.
