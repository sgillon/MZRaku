# MZ700Emul

A Sharp MZ-700 emulator written in C# / .NET 8 (WinForms).

## Status

Functional. Boots the 1Z-013A monitor, runs S-BASIC (1Z-013B), loads `.mzf`
cassette images, plays sound, and types via a configurable PC-keyboard
mapping. Tested against several commercial titles (Nightmare Park, Star
Trek, Panic, etc.).

## Requirements

- Windows
- .NET 8 SDK
- Sharp MZ-700 ROMs and (optionally) S-BASIC + cassette images in
  `roms/`, `basic/`, `games/` next to the executable. The repo includes
  the ones used during development; provide your own if you don't have
  rights to those.

## Build & run

```
dotnet build
dotnet run
```

Or once built:

```
.\bin\Debug\net8.0-windows\MZ700Emul.exe [--basic] [path\to\cassette.mzf]
```

The launcher waits for the monitor to display its `MONITOR 1Z*` prompt
before injecting BASIC or a cassette, so startup is responsive
regardless of host speed.

## Command-line options

| Flag | Effect |
|---|---|
| `--basic` (`-b`) | Auto-load S-BASIC after the monitor is ready. |
| `<path>.mzf` | Auto-load a cassette image. With `--basic`, BASIC programs (type 0x02 / 0x05) are direct-injected into RAM, pointers fixed up, and `RUN` auto-typed. Without `--basic`, machine-code images are direct-injected and execution jumps to their entry. A `.zip` containing an `.mzf`/`.m12`/`.mzt` entry is also accepted (first cassette entry is used). |
| `--dump=<file>` | At frame 120 (configurable), dump CPU/PIT/PPI/VRAM state to a text file and exit — useful for offline diagnostics. |
| `--dumpframe=N` | Override the dump frame number. |
| `--help` (`-h`) | Show usage. |

## Menu and shortcuts

| Action | Shortcut |
|---|---|
| Load cassette… | Ctrl+O |
| Load BASIC | Ctrl+B |
| Reset | Ctrl+R |

You can also drag and drop an `.mzf` (or a `.zip` containing one) onto
the window.

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
                 SpecialKeyMap), video, sound, cassette
MainForm         Window, menu, timer-driven RunFrame loop, CLI auto-load
MZ700            Top-level "machine" that wires CPU + I/O + ROMs
docs/            Sharp service & owners' manuals (reference)
roms/            Monitor ROM (1Z-013A) + character generator
basic/           S-BASIC (1Z-013B) cassette image
games/           Sample MZF cassette images
```

## Known limitations

- Reset (Ctrl+R) while BASIC is running returns to the BASIC `READY`
  prompt rather than re-running the monitor boot sequence.
- MZ-only glyphs (graphics blocks, kana) aren't reachable from a PC
  keyboard in the current char-driven model — by design.

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
