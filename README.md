# MZ700Emul

A Sharp MZ-700 emulator written in C# / .NET 8 (WinForms). The aims of this emulator are:

1. Work well enough play the MZ-700 games I remember from my childhood
2. Be useable from a launcher such as Launchbox or Playnite, taking into account the need for a lot of games to have BASIC present before they can be loaded

This means that the goal is for the emulator to work 'well enough', and with some quality-of-life features to enable the above, without necessarily worrying about accurately reproducing how the actual MZ-700 hardware works.

***
IMPORTANT NOTE - The emulator code is *entirely* AI generated. Although I have some development experience, how CPUs etc work is outside my skillset so what is here is a result of several days of me working with Claude to produce the features and refinements I need for my use case. I chose to use C# as it is a language I know, so I can use how the project has been put together to educate myself on what it takes to create an emulator. The choice to use WinForms, effectively tying the current implementation tightly to Windows, was also made as it suits my specific needs.

Another aim was to see whether something like this is even possible using an AI tool. I think the result is pretty impressive. It's not perfect, far from it, but it does work. I think this is an appropriate use of these tools. I don't think anyone would be using a trivial emulator such as this for anything critical to their lives.

***

## Status

The emulator is generally functional with some known imperfections (listed below).

Boots the 1Z-013A monitor, runs S-BASIC (1Z-013B), plays
sound, and accepts PC keystrokes via the host keyboard layout (no
per-key config needed). Cassette images load via menu, drag-drop, or
the command line — the MZF type byte is inspected so BASIC programs
auto-load BASIC and `RUN`, and machine-code programs jump to their
entry directly. `.zip` archives containing a cassette are accepted
transparently. MZ-1X03 joysticks are emulated and driven from any
Windows-recognised game controller. Tested against several commercial
titles (Nightmare Park, Star Trek, Space Panic, etc.).

**Z80 core: passes both ZEXDOC and ZEXALL** — the de-facto Z80
instruction exercisers from the CP/M Users Group, covering documented
and undocumented behaviour (including the X/Y flag bits). The harness
is built in (`Debug → Run Z80 Test…`) and can be re-run any time
against the supplied `tools/CPM/zexdoc.com` / `zexall.com`. The Z80
core is also a separate class-library project (`Z80Core.dll`),
reusable for other Z80-based machines.

**Single-file release publish** — `dotnet publish -c Release` produces
a single self-extracting `MZ700Emul.exe` (about 300 KB) with no DLLs,
no `.pdb`, no JSON config files alongside. Framework-dependent
(assumes .NET 8 Desktop Runtime is installed); sound goes through
Windows' built-in `winmm.dll` directly (the same DLL the joystick
code uses).

## Quickstart

The emulator itself is freely available, but the MZ-700 firmware that
makes it useful is Sharp Corporation's copyright and is **not**
distributed here. You'll need to source three files yourself (search
for "MZ-700 ROMs" — they're widely archived by MZ enthusiasts) and
drop them into the install directory:

| File | Where it goes | What it is |
|---|---|---|
| `1z-013a.rom` | `roms\` | The MZ-700 monitor ROM (4 KiB). |
| `mz700fon.int` | `roms\` | The character-generator ROM (font data). |
| `1Z-013B.mzf` | `basic\` (or `roms\`) | Sharp's S-BASIC interpreter, supplied on cassette. |

Layout next to `MZ700Emul.exe`:

```
MZ700Emul.exe
roms\
  1z-013a.rom
  mz700fon.int
basic\
  1Z-013B.mzf
```

The first launch scans these folders, records the resolved paths in
`settings.ini`, and starts the emulator. If a file is missing the
emulator reports it with a modal error and tells you exactly where
it looked.

### Using the emulator from a game launcher

See [Launcher setup](docs/usage/launcher-setup.md) for step-by-step
instructions on wiring MZ700Emul into popular Windows game launchers
(Launchbox so far, more to follow).

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

### Producing a release build

```
dotnet publish -c Release -r win-x64 --self-contained false -o publish\MZ700Emul
```

Release publishes a single self-extracting `MZ700Emul.exe` (debug
symbols embedded, framework-dependent). Assumes the .NET 8 Desktop
Runtime is installed on the target machine. Drop the user-supplied
ROMs / BASIC alongside the exe per [Quickstart](#quickstart).

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
| Settings… | Ctrl+S |
| Display 1× / 2× / 3× | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Debugger… | Ctrl+D |
| Memory Viewer… | Ctrl+M |
| HID Diagnostic… | Ctrl+H |

You can also drag and drop an `.mzf` (or a `.zip` containing one) onto
the window. Loading a cassette resets the emulator first, so opening
a different program mid-execution Just Works regardless of whether the
old or new program is BASIC or machine code.

User preferences live in `settings.ini` next to the executable.
**File → Settings…** (Ctrl+S) opens a tabbed dialog covering Display,
ROMs, and Joystick — or you can edit the INI by hand if you prefer:

```ini
[Display]
Scale=2

[Roms]
Monitor=roms\1z-013a.rom
Font=roms\mz700fon.int
Basic=basic\1Z-013B.mzf

[Joystick]
; PC gamepad button index (0..31) that drives each MZ-1X03 stick button.
Button1=0
Button2=1
```

Paths are written relative to the executable when possible (so the
install stays portable) and absolute when the file lives elsewhere.
If a path goes stale (file moved or deleted), the next launch
re-scans the standard locations and patches the file up.

## Documentation

Topic-by-topic guides live under [`docs/usage/`](docs/usage/):

- [Debugger](docs/usage/debugger.md) — execution control, register
  view, disassembly pane, breakpoints.
- [Memory viewer](docs/usage/memory-viewer.md) — live hex / ASCII view
  of the 64K address space with PC and SP highlighting.
- [HID Diagnostic](docs/usage/hid-diagnostic.md) — live view of
  host keyboard / joystick input and the resolved MZ-700 matrix state.
- [Keyboard](docs/usage/keyboard.md) — how host keystrokes are mapped
  to the MZ-700 matrix; per-key editor in Settings; Font Sheet for
  GRAPH glyphs; Import / Export `.mzkbd`; loading `.bas` source files.
- [Joystick](docs/usage/joystick.md) — MZ-1X03 emulation driven from
  any Windows-recognised game controller.
- [Hardware notes](docs/usage/hardware-notes.md) — MZ-700 hardware
  quirks the code learned the hard way (PIT topology, $E008, etc.).
- [Launcher setup](docs/usage/launcher-setup.md) — wiring MZ700Emul
  into Launchbox (and other launchers to come).
- [Project history](docs/history.md) — chronological record of major
  changes and architectural decisions, for the curious or for
  future-maintainer orientation.

## Project layout

```
Z80Core/         Separate class-library project (Z80Core.dll) — Z80 CPU
                 core (main, ED, CB, IX/IY prefixes) and a standalone
                 disassembler. Pure net8.0, no WinForms, no MZ-700-
                 specific code; reusable for other Z80 machines.
Hardware/        8255 PPI, 8253 PIT, memory map, keyboard (CharMap +
                 SpecialKeyMap + Mz700MatrixReference), video, sound,
                 cassette + zip loader, joystick (MZ-1X03 + WinMM
                 bridge).
UI/              All WinForms surfaces, grouped by feature area:
  Keyboard/        Diagram, per-key + per-VK editor, matrix grid,
                   capture controls — the diagram-first editing flow
                   in Settings → Keyboard plus the advanced child.
  Debugger/        DebuggerForm, MemoryViewer, Z80 test runner.
  Diagnostics/     HID Diagnostic + Font Sheet — live observation
                   windows under the Debug menu.
  Settings/        SettingsForm tabs + Joystick button capture
                   dialog.
  AboutForm.cs     Help → About dialog (icon, version, build date).
  SmoothControls.cs  Double-buffered Label / ListBox / TableLayout
                   subclasses shared by the debugger windows.
MainForm.cs      Window, menu, timer-driven RunFrame loop, CLI auto-load.
MZ700.cs         Top-level "machine" that wires CPU + I/O + ROMs.
Program.cs       Main entry point + CLI argument parsing.
Settings.cs      INI-backed user preferences (settings.ini).
docs/usage/      Topic-by-topic usage docs.
roms/            (You supply) Monitor ROM + character generator.
basic/           (You supply) S-BASIC cassette image.
games/           Joystick test program (joytest.bas / .mzf).
```

## Known limitations & imperfections

- MZ-only glyphs (graphics blocks, kana) aren't reachable from a PC
  keystroke in the char-driven model — by design. The **Font Sheet**
  window (View → Font Sheet…, Ctrl+G) bridges most of the gap with a
  click-to-type catalogue; bank-1 click-to-type has a known
  attribute-bit gap that's tracked in
  [docs/usage/keyboard.md](docs/usage/keyboard.md#known-limitations).
- Keyboard: a handful of parked items (bank-1 click-to-type, MZ-shift
  assertion race on rapid char input, no Left/Right Ctrl distinction)
  are listed with full context in
  [docs/usage/keyboard.md](docs/usage/keyboard.md#known-limitations)
  and surfaced as a callout on the Settings → Keyboard tab.
- Sound reproduction isn't quite right. It works well enough to play most games, but sometimes sounds are missing and I'm not confident about the timings.
- Auto-typed input (BASIC source paste / command auto-load) runs at
  around 6–8 chars/sec — fine for short snippets, slow for long
  listings.

## Planned future work

Items I'd like to come back to (rough priority order):

- **Direct-jump shortcuts to settings tabs** — once the settings UI
  gets busier, add menu entries that open the dialog already focused
  on a specific tab (e.g. *Settings → Joystick…*) so users don't have
  to navigate inside.
- **BASIC-aware debugger panes** — program lister with de-tokenised
  output, current-line indicator, variable-table reader.
- **Current-line highlighting** in the source view once the BASIC
  line pointer is wired up.
- **Persisted debugger state** — remember window placement and the
  breakpoint list across runs (in `settings.ini`).
- **BASIC source editor pane** — read the live BASIC program out of
  RAM, render it in an editable text pane, and write edits back.
- **Hotkeys for the remaining menu items.**

## Acknowledgements

- **Sharp Corporation** — original MZ-700 hardware and ROM firmware.
  All ROM/BASIC files referenced in [Quickstart](#quickstart) remain
  Sharp's copyright; this project ships neither, and only describes
  how to locate copies you are entitled to use.
- The wider **MZ-700 enthusiast community** for the disassemblies,
  service manuals, and games preservation work that made this project
  possible.
- Ben at **Sharpworks (https://mz-sharpworks.co.uk/)** - Ben also maintains
  the Sharp MZ Software Archive (https://mz-archive.co.uk/) which is an
  invaluable resource for MZ software. Sharpworks also publish brand new MZ
  titles on cassette and should be supported for that alone.
- **Anthropic Claude** — as noted at the top of this README, the
  entire codebase was generated through pair-programming with Claude.
