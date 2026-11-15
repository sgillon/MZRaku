# Hardware notes

Hardware facts the code learned the hard way, and where they live.
Each one is also written in the class comments next to the code that
depends on it; this page gathers them so they can be found without
knowing where to look. This page is for someone changing the code.

It describes v1.5.0. Tape handling has its own page,
[`tape-traps.md`](tape-traps.md), and the keyboard its own,
[`keyboard.md`](keyboard.md).

## All machines

- **The 8253's counter 2 is cascaded from counter 1's output** and
  counts once per counter-1 period. Before v1.3.0 it counted on both
  edges of OUT1, so the clock (`TI$`) ran twice as fast on every
  machine (`Core/Devices/Pit8253.cs`).
- **Frame pacing.** The WinForms timer can't tick 60 times a second on
  Windows (a 16 ms interval measured about 40 a second), so the app
  runs as many frames per tick as real time calls for. Before v1.3.0
  every machine ran at about two-thirds speed, and timings tuned then
  (the tempo signal below) were rescaled afterwards
  ([`architecture.md`](architecture.md#the-two-front-ends)).

## MZ-700

- **PIT wiring.** Counter 0 is on its own, clocked at about 895 kHz,
  for sound. Counter 1 is a rate generator at about 15.6 kHz, its
  output clocking counter 2. Counter 2 is the 12-hour clock, its
  output wired to the CPU's interrupt (masked by the PPI's port C
  bit 2, where 1 means enabled). Earlier "obvious" wirings were wrong;
  the service manual settles it (`MZ700.cs`).
- **The tempo signal.** `$E008` bit 0 is the cursor-blink 555/556
  oscillator, not the 8253's OUT1. S-BASIC's `MUSIC` times its notes
  from it, so its rate decides how long a tune takes. It was tuned
  against a tune measured on a real MZ-700 (13 seconds), then rescaled
  when the frame-pacing fix landed: it now toggles every 53,204 CPU
  cycles, a 33.3 Hz square wave (`MZ700.CyclesPerTempoToggle`).
- **The speaker has two gates,** NANDed: the PPI's PC3 and the latch
  at `$E008` bit 0 (written). Reset clears the latch; the ROM sets it
  when it wants the boot tone heard (`Core/Devices/Sound.cs`).
- **Joystick lines.** `$E008` bits 1–6 are the MZ-1X03's lines,
  active low, through an LS367 buffer. With no joystick they must
  read 1, "not pressed": reading 0 makes joystick games (Space Panic)
  start themselves with every direction held.
- **S-BASIC's program pointers.** The text start (TXTTAB) is at
  `$6ABF` and the end pointers at `$6AB3`–`$6AB8`. A program injected
  at another address than the default `$6BCF` (`trek.mzf` loads at
  `$6BDF`) only lists and runs once these are set
  (`Mz700Cassette.FixupBasicProgramPointers`).
- **Long BASIC programs cross `$D000`.** S-BASIC's LOAD banks VRAM
  out and leaves it out, so a program longer than about 25 KB runs on
  through `$D000`–`$DFFF`. An injection that wrote through the VRAM
  window put the program's tail on the screen (Dragon Caves, fixed in
  v1.3.0).

## MZ-80A

- **The timer interrupt mask is the other way round.** `$E002` bit 2
  set *masks* the interrupt on the MZ-80A; on the MZ-700 the same bit
  set enables it (`MZ80A.cs`).
- **The memory swap is done by reads.** Reading `$E00C` moves the
  monitor ROM to `$C000`, reading `$E010` puts it back at `$0000`;
  the MZ-700 switches banks with port writes (`Mz80aMemory.cs`).
  Reads of `$E014` / `$E015` set normal or reverse video, and reads of
  `$E200`–`$E2FF` the hardware scroll offset (`Mz80aIoBus.cs`).
- **The character ROM is MSB-first.** SA-CG.rom stores each glyph row
  with the leftmost pixel in bit 7; `mz700fon.int` is LSB-first. The
  first picture came out mirrored (`Mz80aVideo.cs`).
- **`$E008` reads:** bit 0 is H-blank, which SA-1510's early wait loop
  and SA-5510's `MUSIC` timing poll; bit 7 is counter 2's output (the
  tempo-timer status). Written, bit 0 is the speaker's only gate.
- **SA-1510's key scan keeps state inside the tape header buffer,** at
  `$1164`–`$116E`. Writing a file's whole 128-byte header there drops
  the next key typed ([`tape-traps.md`](tape-traps.md)).

## MZ-800

- **The frame is a PAL raster:** 312 lines of 227 CPU cycles, about
  50.08 frames a second. The CPU runs line by line, and the blanking
  signals and palette latch follow the beam, which some titles rely on
  to change the palette mid-frame (`MZ800.cs`).
- **Banks are set by I/O:** `OUT` to `$E0`–`$E6` and `IN` from `$E0` /
  `$E1` latch the memory configuration (`Mz800Memory.cs`).
- **Interrupts come through the Z80 PIO.** PIT counter 0's output,
  inverted, is PA4; PA5 is the frame (VBLANK) interrupt that titles
  such as Uridium use (`MZ800.cs`, `Core/Devices/Z80Pio.cs`).
- **Sound is rendered in emulated time:** the SN76489 PSG and PIT
  counter 0, mixed as PCM, so it keeps exact step with the CPU
  (`Core/Devices/Sn76489.cs`, `MZ800.RenderAudio`).
- **The MZ-1R25 VRAM expansion** adds planes III and IV, which the
  320 × 200 16-colour and 640 × 200 4-colour modes (and frame B) need.
  Without it, writes to those planes are dropped and reads float high.
  It can be turned off to see a title without it
  (`Mz800Memory.VramExpansion`).

## Joystick

The code is in three places:

| File | What it does |
|---|---|
| `Platform/JoystickInput.cs` | The Windows side: WinMM `joyGetPosEx`, not XInput, so older and non-XInput pads work too. Up to two pads |
| `Core/Devices/Joystick.cs` | The MZ-700's MZ-1X03 on `$E008`: stick axes become 555 monostable pulses on bits 1–4 after VBLANK falls, timed against Space Panic's read routine (full deflection reads 0 or 255, centre 128); the POV hat, when held, overrides the axes with clean 0 / 128 / 255; buttons 1 and 2 are SW1 and SW2, read during VBLANK |
| `Core/MZ800/Mz800Joystick.cs` | The MZ-800's two digital ports at `$F0` / `$F1`: a direction counts once the stick is a quarter of the way from centre |

The button numbers come from `[Joystick] Button1=` / `Button2=` in
`settings.ini`. The MZ-80A has no joystick; its session keeps a
detached `Joystick` so the front end's gamepad code needn't care.
`games/joytest.bas` (and `.mzf`) draws a cross that follows stick 1.
