# MZ700Emul

A Sharp MZ-700 emulator written in C# / .NET 8 (WinForms). The aims of this emulator are:

1. Work well enough play the MZ-700 games I remember from my childhood
2. Be useable from a launcher such as Launchbox or Playnite, taking into account the need for a lot of games to have BASIC present before they can be loaded

This means that the goal is for the emulator to work 'well enough' without necessarily worrying about accurately reproducing how the actual MZ-700 hardware works.

IMPORTANT NOTE - The emulator code is *entirely* AI generated. Although I have some development experience, how CPUs etc work is outside my skillset so what is here is a result of several days of me working with Claude to produce the features and refinements I need for my use case. I chose to use C# as it is a language I know, so I can use how the project has been put together to educate myself on what it takes to create an emulator.

## Status

The emulator is generally functional with some known imperfections (listed below). 

Boots the 1Z-013A monitor, runs S-BASIC (1Z-013B), plays
sound, and accepts PC keystrokes via the host keyboard layout (no
per-key config needed). Cassette images load via menu, drag-drop, or
the command line — the MZF type byte is inspected so BASIC programs
auto-load BASIC and `RUN`, and machine-code programs jump to their
entry directly. `.zip` archives containing a cassette are accepted
transparently. Tested against several commercial titles (Nightmare
Park, Star Trek, Space Panic, etc.).


## Requirements

- Windows
- .NET 8 SDK
- Sharp MZ-700 ROMs (`1z-013a.rom`, `mz700fon.int`) and the S-BASIC
  cassette image (`1Z-013B.mzf`). On first run the emulator scans
  alongside the executable and inside `roms/` / `basic/` for these
  files and records their locations in `settings.ini` — you can move
  them around afterwards by editing the `[Roms]` section. The repo
  includes the files used during development; provide your own if you
  don't have rights to those.

## Build & run

```
dotnet build
dotnet run
```

Or once built:

```
.\[Working dir]\MZ700Emul.exe [--basic] [path\to\cassette.mzf]
```

The launcher waits for the monitor to display its `MONITOR 1Z*` prompt
before injecting BASIC or a cassette, so startup is responsive
regardless of host speed.

## Command-line options

| Flag | Effect |
|---|---|
| `--basic` (`-b`) | Auto-load S-BASIC after the monitor is ready. Implied automatically when the cassette is a BASIC program. |
| `<path>.mzf` | Auto-load a cassette image. The MZF type byte is inspected: BASIC programs (type 0x02 / 0x05) auto-load BASIC, then direct-inject + `RUN`; machine-code images (type 0x01) skip BASIC and jump straight to their entry. A `.zip` containing an `.mzf`/`.m12`/`.mzt` entry is also accepted (first cassette entry is used). |
| `--dump=<file>` | At frame 120 (configurable), dump CPU/PIT/PPI/VRAM state to a text file and exit — useful for offline diagnostics. |
| `--dumpframe=N` | Override the dump frame number. |
| `--help` (`-h`) | Show usage. |

## Menu and shortcuts

| Action | Shortcut |
|---|---|
| Load cassette… | Ctrl+O |
| Load BASIC | Ctrl+B |
| Load BASIC source… | Ctrl+Shift+B |
| Reset | Ctrl+R |
| Display 1× / 2× / 3× | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Debugger… | Ctrl+D |

"Load BASIC source…" reads a plain-text `.bas` file and types each
non-blank, non-comment line into the running BASIC interpreter. Lines
starting with `;` or `'` are stripped on the host side. If BASIC
isn't loaded yet the emulator resets, auto-loads BASIC, then types
the source once the READY prompt is up. End the file with `RUN` to
auto-start the program. See `games/joytest.bas` for an example.

You can also drag and drop an `.mzf` (or a `.zip` containing one) onto
the window. Loading a cassette resets the emulator first, so opening
a different program mid-execution Just Works regardless of whether the
old or new program is BASIC or machine code.

User preferences live in `settings.ini` next to the executable — a
plain INI file you can edit by hand if you prefer:

```ini
[Display]
Scale=2

[Roms]
Monitor=roms\1z-013a.rom
Font=roms\mz700fon.int
Basic=basic\1Z-013B.mzf
```

Paths are written relative to the executable when possible (so the
install stays portable) and absolute when the file lives elsewhere.
If a path goes stale (file moved or deleted), the next launch
re-scans the standard locations and patches the file up.

## Debugger

`Debug > Debugger…` (Ctrl+D) opens a debugger window. It currently
provides CPU execution control and inspection:

- **Pause / Resume** (F5), **Step** one instruction (F10), **Step
  Frame** (F11), and **Reset**. Pausing freezes the CPU between
  instructions while the screen keeps refreshing — nothing blocks, so
  the emulator and debugger stay responsive.
- A live **Z80 register view**: `PC`, `SP`, the main and alternate
  register pairs, `IX`/`IY`, `I`/`R`/`IM`, the interrupt flip-flops and
  halt state, decoded flags, and the total cycle count.
- An address-based **breakpoint manager**: enter a hex address to add a
  breakpoint; execution stops with `PC` parked on that instruction.

A disassembly view and BASIC-aware panes (program listing, current
line, variable table) are planned next.

## Keyboard

The emulator drives the MZ-700 matrix from the **character** your PC
keystroke produces, after Windows has applied your keyboard layout.
Type `;` and the MZ sees `;`; type `+` and it sees `+`; type `:` and it
sees `:`. There is no per-key configuration — the host OS handles
layout, AltGr, and dead keys for you, and the emulator translates the
resulting Unicode character to the corresponding MZ-700 matrix
position (and shift state, where needed).

Non-character keys are mapped directly:

| PC key | MZ-700 |
|---|---|
| Enter | CR |
| Backspace / Delete | DEL |
| Insert | INS |
| Esc | BREAK |
| Cursor keys | Cursor |
| Left/Right Ctrl | MZ Ctrl |
| F1–F4 | F1–F4 |

The translation table lives in `Hardware/CharMap.cs` (printables) and
`Hardware/SpecialKeyMap.cs` (non-printables). MZ-only glyphs (graphics
blocks, kana) aren't reachable in this scheme — that's an intentional
trade-off for not having to configure anything.

## Project layout

```
Z80/             Z80 CPU core (main, ED, CB, IX/IY prefixes)
Hardware/        8255 PPI, 8253 PIT, memory map, keyboard (CharMap +
                 SpecialKeyMap), video, sound, cassette + zip loader
MainForm         Window, menu, timer-driven RunFrame loop, CLI auto-load
MZ700            Top-level "machine" that wires CPU + I/O + ROMs
DebuggerForm.cs  Debugger window (execution control, registers, breakpoints)
Settings.cs      INI-backed user preferences (settings.ini)
docs/            Sharp service & owners' manuals (reference)
roms/            Monitor ROM (1Z-013A) + character generator
basic/           S-BASIC (1Z-013B) cassette image
games/           Sample MZF cassette images
```

## Known limitations & imperfections

- MZ-only glyphs (graphics blocks, kana) aren't reachable from a PC
  keyboard in the current char-driven model — by design.
- Sound reproduction isn't quite right. It works well enough to play most games, but sometimes sounds are missing and I'm not confident about the timings
- Some issues in BASIC. For example, in Solo Software's version of Star Trek, there seems to be an issue parsing variables for things like the long range and galactic maps

## Hardware notes

A handful of MZ-700 hardware quirks the code learned the hard way and
documents inline:

- The PIT topology is C0 standalone (audio at ~895 kHz); C1 standalone
  (rate generator at 15.6 kHz, OUT1 → CLK2); C2 cascaded from C1.OUT
  (12-hour RTC, OUT2 → CPU INT). Earlier "obvious" wirings were wrong.
- `$E008` bit 0 is the 555/556 cursor-osc / "tempo" signal at ~50 Hz
  toggle (tuned against real-hardware MUSIC playback length), **not**
  the 8253 OUT1.
- BASIC's MUSIC duration polls `$E008` bit 0 — getting that signal's
  rate right is what makes a 13-second tune actually take 13 seconds.
- BASIC's text-area pointer (TXTTAB) lives at `$6ABF`; the cassette
  loader updates it so programs whose load address differs from the
  default `$6BCF` (e.g. `trek.mzf` at `$6BDF`) `LIST` and `RUN`
  correctly.
- `$E008` bits 1-6 are joystick lines (active-low, via an LS367 buffer)
  and must default to **1** ("idle / not pressed") when no joystick is
  connected. Returning 0 there makes joystick games (e.g.
  `panic.mzf`) auto-start and run with all directions held.
