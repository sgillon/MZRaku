# MZ-80A

MZRaku emulates the Sharp MZ-80A with its monochrome display, the
beeper and Sharp's SA-5510 BASIC. The MZ-80A has no joystick.

## Getting started

You need three files (see the [Quickstart](../../README.md#quickstart)):

| File | Where it goes | What it is |
|---|---|---|
| `SA-1510.rom` | `roms\` | The MZ-80A monitor ROM (4 KiB). |
| `SA-CG.rom` | `roms\` | The character generator ROM (the font). |
| `SA-5510.mzf` | `basic\` (or `roms\`) | Sharp's SA-5510 BASIC, supplied on cassette. |

To start the MZ-80A:

- for one run, use `MZRaku.exe --mz80a`;
- to switch for this session, use **System → Machine → MZ-80A**;
- to make it the default, use **System → Settings → Startup → Default
  machine** (stored as `[Machine] DefaultMachine=MZ80A`).

The machine boots to the SA-1510 monitor's `*` prompt. **File → Load
BASIC** (Ctrl+B) or `--basic` loads SA-5510 and leaves you at its
`Ready` prompt; it takes about three seconds to start, as on the real
machine.

## Loading software

**Load cassette** (Ctrl+O), drag and drop, and a file on the command
line all load a program and run it for you:

- **Machine-code programs** are loaded and started straight away.
- **SA-5510 BASIC programs** reset the machine, load BASIC, put the
  program in memory the way BASIC's own `LOAD` would, and type `RUN`.

`.zip`, `.mzf`, `.m12` and `.mzt` files all work; from a `.zip` or
`.mzt`, the first file is used.

**File → Insert cassette for LOAD…** (or `--insert=<file>` on the
command line) puts a tape in the cassette deck without resetting, for
the machine's own loading: the monitor's `L`, or SA-5510's `LOAD`.

**BASIC `SAVE`** writes the program as an `.mzf` in a `saves\` folder
next to `MZRaku.exe`, named after the program (`SAVE "GAME"` writes
`GAME.mzf`; an existing file isn't overwritten: `GAME-1.mzf` is
written instead). BASIC returns to `Ready` as usual.

**File → Load BASIC source…** (Ctrl+Shift+B) types a plain-text
listing into SA-5510. If BASIC isn't running it's loaded first. See
[Loading BASIC source](keyboard.md#loading-basic-source).

## Keyboard

The character your PC produces is sent to the MZ-80A, whatever your
keyboard layout (see [Keyboard](keyboard.md)).

**Letters work the other way round from a PC,** as on the real
MZ-80A: a letter key on its own types a capital, and with Shift a
small letter. So BASIC keywords are typed without Shift. If you'd
rather have PC behaviour, turn on **Invert letter Shift** under
**System → Settings → Keyboard** (`[Keyboard.MZ80A]
InvertLetterShift=`).

The non-character keys are:

| PC key | MZ-80A |
|---|---|
| Enter | CR |
| Backspace / Delete | DEL |
| Insert | INST |
| Home | HOME (**Shift+Home** is CLR) |
| Cursor up / right | Cursor up / right |
| Cursor down / left | SHIFT + cursor up / right, the MZ-80A's down and left |
| Esc | CTRL (**Shift+Esc** is BREAK) |
| Ctrl | CTRL |
| F11 | GRPH (press again to leave) |

Pressing GRPH doesn't open the Font Sheet on the MZ-80A, but **View →
Font Sheet…** (Ctrl+G) works, and in SA-5510 you can click a
character to type it (see [Font Sheet](keyboard.md#font-sheet)).

**System → Settings → Keyboard** shows the MZ-80A's keyboard. Click a
key to change which PC key types it. Your changes are saved to
`[CharMap.MZ80A]` and `[KeyOverrides.MZ80A]` in `settings.ini`.

## Status bar

From the left: the machine name, messages, then panes that appear
when they apply (speed, `MUTED`), the **TAPE** light (pale yellow
while a cassette is inserted and waiting, orange as it's read or
written), and the **keyboard mode**: `ALPHA` or `GRAPH`. The mode is
read from the SA-1510 monitor, which SA-5510 uses for its keyboard
too. When a program has moved the monitor out of the way, the pane
shows `—`.

## Display

40 × 25 characters, in green on black like the MZ-80A's own screen.
For white on black, turn off **Green phosphor tint** under
**System → Settings → Display** (`[Display.MZ80A] GreenScreen=`). The
MZ-80A's reverse video and hardware scrolling are emulated.

Window size, full screen, scanlines and **View → Save screenshot**
(Ctrl+P) work as on the other machines.

## Sound

The MZ-80A's beeper, from the 8253 timer. SA-5510's `MUSIC` and the
boot tone play through it. **System → Sound** has the volume and
**Mute** (Ctrl+U).

## Debug tools

The [Debugger](debugger.md), [Memory Viewer](memory-viewer.md),
[HID Diagnostic](hid-diagnostic.md) and Keyboard Matrix windows work on
the MZ-80A. The Memory Viewer's bank line shows whether the monitor
ROM is at `$0000` or moved to `$C000`.

## Known limitations

- **No Sound Diagnostic.** The menu item is greyed out on the MZ-80A.
- **No floppy disk or printer.**
- **`MUSIC` timing** comes from the CPU clock rather than an emulated
  oscillator: right by ear, not measured to the millisecond.
- **Typed BASIC source is slow**, a few characters a second.
