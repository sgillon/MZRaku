# Architecture

How MZRaku's code is laid out, what may depend on what, and how a
frame gets run. This page is for someone changing the code; start
here, then read the topic pages:

- [`keyboard.md`](keyboard.md): the keyboard layer, from host key to
  ROM key scan.
- [`tape-traps.md`](tape-traps.md): every tape trap on every machine.
- [`regression.md`](regression.md): the regression suite and the
  command-line test tools.
- [`adding-a-machine.md`](adding-a-machine.md): the checklist the
  MZ-80A and MZ-800 followed.
- [`hardware-notes.md`](hardware-notes.md): hardware facts the code
  learned the hard way, machine by machine.

It describes v1.5.0. v1.6.0 adds a second front end (MZRakuCLI, on
SDL), which the core was split out for in v1.4.0.

## The projects

```
                 ┌──────────────────────────┐   ┌───────────────────┐
                 │ MZRaku  (MZRaku.csproj)  │   │ MZRakuHeadless    │
                 │ net10.0-windows WinForms │   │ net10.0, console  │
                 │  Program, MainForm       │   │ tools/MZRaku-     │
                 │  UI/   Platform/         │   │   Headless/       │
                 └────────────┬─────────────┘   └─────────┬─────────┘
                              │                           │
                              ▼                           ▼
                 ┌──────────────────────────────────────────────────┐
                 │ MZRaku.Core  (Core/MZRaku.Core.csproj)           │
                 │ net10.0, no Windows Forms                        │
                 │  Session/  Machines/  MZ700/ MZ80A/ MZ800/       │
                 │  Devices/  Keyboard/  Tape/  Host/               │
                 └────────────────────────┬─────────────────────────┘
                                          │
                                          ▼
                 ┌──────────────────────────────────────────────────┐
                 │ Z80Core  (submodule, github.com/sgillon/Z80Core) │
                 │ net8.0: the Z80 CPU and a disassembler           │
                 └──────────────────────────────────────────────────┘
```

Z80Core is its own repository and still targets `net8.0`; inside
MZRaku it runs on the app's .NET 10 runtime. Its own move to .NET 10
is a later Z80Core release.

| Project | Where | What it is |
|---|---|---|
| Z80Core | `Z80Core/` (git submodule) | The Z80 CPU and disassembler. Knows nothing about Sharp machines. Its own repository, with its own releases. After cloning MZRaku, `git submodule update --init` fetches it; a change to it is made and released there, then MZRaku moves the submodule pointer |
| MZRaku.Core | `Core/` | The emulation: the three machines, their chips, keyboard and tape handling, `settings.ini`, and `EmulatorSession`, which runs one machine |
| MZRaku | repository root | The Windows app: the window, menus, dialogs, debugger and diagnostics (`UI/`), and the Windows sound and gamepad code (`Platform/`) |
| MZRakuHeadless | `tools/MZRakuHeadless/` | Runs a session with no window and no sound device, as fast as it goes, and writes `--dump` files. The regression suite's second runner |
| MzfBuilder, RomAnalyse | `tools/` | Small developer helpers: build an `.mzf` from a binary; dump and disassemble the MZ-700 monitor's key tables |

`UI/` and `Platform/` are folders inside the one app project, not
projects of their own. `MZRaku.csproj` compiles every `.cs` under the
repository root except `Core/`, `Z80Core/` and `tools/`, so a stray
`.cs` file anywhere else (in a `_` working folder, say) gets built
into the app. Scratch projects go outside the repository.

### The repository

```
Z80Core/         Submodule: the Z80 CPU and disassembler (Z80Core.dll).
Core/            MZRaku.Core.dll, the emulation (see "Inside the core").
UI/              The app's windows, by feature:
  Keyboard/        The keyboard editor: diagram, per-key and per-VK
                   editors, matrix grid, key capture, one context and
                   physical layout per machine.
  Debugger/        Debugger and Memory Viewer.
  Diagnostics/     HID Diagnostic, Font Sheet, Sound Diagnostic.
  Settings/        The Settings dialog, its Apply summary, joystick
                   button capture.
  AboutForm.cs     Help → About.
  FrameBufferBitmap.cs, HostKeyParity.cs, SmoothControls.cs,
  EmbeddedResources.cs   Small shared pieces.
Platform/        Windows sound (WinMM wave out) and gamepad input.
MainForm.cs      The window, menus, status bar, display and frame
                 timer; drives the session.
Program.cs       Entry point: command line, startup checks.
MZRaku.csproj    The app project.
tools/
  MZRakuHeadless/  The headless runner.
  regress/         The regression suite: regress.py, key scripts,
                   settings presets.
  MzfBuilder/, RomAnalyse/   Small helpers.
docs/
  developer/       These pages.
  usage/           The user's guides.
  history.md, roadmap.md, release-check.md, v1.x plans.
assets/          The icon's source image and build script, README
                 images.
games/           joytest.bas / .mzf, the joystick test program.
roms/, basic/    Not in the repository: you supply the firmware.
```

Folders starting with `_` are local working folders, git-ignored and
never committed.

### What may depend on what

- **Z80Core depends on nothing.** It must not learn about MZ machines.
- **MZRaku.Core depends on Z80Core only.** No Windows Forms, no
  Windows APIs, no `System.Drawing`. Anything a front end has to
  supply comes in through the interfaces in `Core/Host/` (below).
  MZRakuHeadless builds on the core alone, which proves this on every
  suite run.
- **The front ends depend on the core.** The core never calls back
  into a front end except through the events and interfaces it
  defines.
- **The core has no `InternalsVisibleTo`.** Front ends see only
  `public` members. Machine parts that only the core uses (the MZ-80A
  and MZ-800 cassettes, PIT and PPI, the machine constructors) are
  `internal`; v1.4 Phase 5 tightened this. Making something `public`
  for a front end is a deliberate change to the core's surface.

### What a front end supplies (`Core/Host/`)

| Type | Direction | What it is |
|---|---|---|
| `FrameBuffer` | core → front end | The rendered picture: 32-bit ARGB pixels, 320 × 200 (640 × 200 in the MZ-800's 640 modes). The front end scales and shows it. The app copies it into a GDI+ bitmap (`UI/FrameBufferBitmap.cs`) |
| `IAudioOut` | front end → core | A sound device the core writes 16-bit mono PCM to. The app's is `Platform/WinmmWaveOut.cs`; the headless runner's is `NullAudioOut`. `Sound` feeds it from its own background thread |
| `IJoystickSource` | front end → core | The host gamepad, polled once a frame. The app's is `Platform/JoystickInput.cs` |
| `IKeyboardInput` | front end → core | Where host key events go: key down, key press (the character), key up, focus lost. Each machine's keyboard implements it |
| `HostKey` | front end → core | The core's key code. A copy of WinForms `Keys` (same names and values), checked at startup by `UI/HostKeyParity.cs` because the app casts between the two. Saved key overrides use its names; `HostKeyNames` pins the name of each value that has more than one |

## Inside the core

| Folder | What it holds |
|---|---|
| `Session/` | `EmulatorSession`; `AutoLoadOrchestrator` (boot-time loading of BASIC, cassettes and BASIC source); `Settings` (`settings.ini`); `KeyScript` (`--keys`); `DumpTraceRecorder` and `PngWriter` (`--dump` output and screenshots) |
| `Machines/` | `IMachine`, `MzMachineBase`, `MachineType` |
| `MZ700/`, `MZ80A/`, `MZ800/` | One folder per machine, every file prefixed with the machine (`Mz700…`, `Mz80a…`, `Mz800…`): the machine class, memory map, I/O bus, video, keyboard (keyboard class, special-key map, char map and its overrides, matrix reference, physical layout, ROM key tables) and cassette traps. `MZ800/` also has its ROM file loader and joystick ports, and `MZ700/` the glyph catalogue the Font Sheet uses |
| `Devices/` | Chips shared by the machines: `Ppi8255`, `Pit8253`, `Z80Pio`, `Sn76489` (PSG), `Sound`, `Joystick` (the MZ-1X03) |
| `Keyboard/` | The machine-independent keyboard parts: `KeyboardMatrixBase`, the MZ-700/MZ-800 auto-typer, key overrides, `.mzkbd` files, the startup matrix checks. See [`keyboard.md`](keyboard.md) |
| `Tape/` | `MzfImage` (the `.mzf` format), `CassetteFile` (reading `.mzf`, `.m12`, `.mzt` and zips), `CassetteTrapBase`. See [`tape-traps.md`](tape-traps.md) |
| `Host/` | The front-end interfaces above |

## The machines

`IMachine` (`Core/Machines/IMachine.cs`) is what the session and the
front ends drive: the CPU and memory, the debugger controls (pause,
resume, step an instruction, step a frame), `RunFrame`, `Reset`, ROM
loading, BASIC and cassette loading, and the parts every machine has
and the host reaches the same way (sound, the video frame, keyboard
input, the cassette traps). Machine-specific hardware (the PPI, PIT,
PSG, key tables) stays off it; a window that needs it casts to the
machine class and is only offered on that machine.

`MzMachineBase` holds what the three really share: the `Z80Cpu`, and
the pause and step logic. Each machine class derives from it and
implements `IMachine` itself.

| | `MZ700` | `MZ80A` | `MZ800` |
|---|---|---|---|
| CPU clock | 3.5469 MHz | 2 MHz | 3.5469 MHz |
| Frame | 60 per second | 60 per second | ≈ 50.08 per second: a PAL raster, 312 lines × 227 cycles |
| How a frame runs | 85 % of the frame's cycles, then VBLANK for the rest | as the MZ-700 | line by line, with blanking and the palette latch following the beam |
| Chips | 8255, 8253, sound | 8255, 8253, sound | 8255, 8253, Z80 PIO, SN76489 PSG, sound |
| Sound | square wave from PIT counter 0, two gates | square wave from counter 0, one gate | PSG and counter 0 rendered as PCM in emulated time |
| ROMs | `1z-013a.rom` (monitor 1Z-013A), `mz700fon.int` | `SA-1510.rom` (monitor SA-1510), `SA-CG.rom` | `MZ800.ROM` (monitor 1Z-013B, character ROM, IPL and monitor 9Z-504M), or the same as `MZ700IPL.ROM` + `FONT.ROM` + `MZ800IPL.ROM` |
| BASIC | `1Z-013B.mzf` (S-BASIC) | `SA-5510.mzf` | `1Z-016.mzf` |

Each machine class wires its own parts together in its constructor:
the CPU's `Mem` and `Io` (Z80Core's `IMemory` and `IIoBus`, which the
machine's memory map and I/O bus implement), the chips on the I/O
bus, the keyboard on the PPI, and the cassette trap on the CPU's
`PreStep` hook, which runs before every instruction and lets the trap
take over at a ROM tape entry point.

The firmware itself is never shipped. `Settings.EnsureRomPaths` looks
for the expected file names under `roms\` and `basic\` next to the
executable (and up the tree, so a source checkout works) and records
what it finds in `settings.ini`.

## EmulatorSession

`EmulatorSession` (`Core/Session/EmulatorSession.cs`) is one running
machine and everything that drives it, with no front end in it. It
holds one machine for its whole life: switching machine means a new
session, and the app does that by starting a new copy of itself with
`--mz700`, `--mz80a` or `--mz800`.

It owns:

- **Creating the machine** named by `Settings.CurrentMachine`, and
  handing it its settings (key overrides, char-map overrides, the
  MZ-80A screen colour and letter shift, the MZ-800 VRAM board).
  `Mz700`, `Mz80a` and `Mz800` give the machine class (one is
  non-null); `Active` gives it as an `IMachine`.
- **Starting:** `Start(openAudio, dumpPath, dumpFrame)` loads the ROMs,
  resets, starts sound on the device the front end opens, and, for a
  `--dump` run, turns on the trace logs and creates the recorder.
- **Loading:** `QueueStartupLoad` (the command line), `LoadCassette`
  (File → Load, drag and drop), `InsertCassette` (the machine's own
  LOAD reads it), `LoadBasic`, `LoadBasicSource`. Anything that has to
  wait for the ROM to boot is queued with the `AutoLoadOrchestrator`,
  which watches VRAM for each ROM's ready screen.
- **Keys:** `KeyDown`, `KeyPress`, `KeyUp` and `ReleaseHostKeys` pass
  host keys to the machine's keyboard; `UseKeyScript` plays a
  `--keys` script.
- **Running:** `RunFrame`, `Reset`, `Stop`, and the speed multiplier.
- **Live settings:** `ApplySettings` pushes the settings that change
  while running; volume and mute have their own properties.

It reports through events, which fire on the thread that called
`RunFrame`: `StatusMessage`, `FatalError`, `GraphModeEntered` (open
the Font Sheet), `GraphModeSampled` (the MZ-80A mode pane),
`FrameRan` and `DumpComplete`.

What stays in the front end: the window, menus and dialogs, frame
pacing, the debugger's pause (it calls `Active.Pause()` and
`Resume()`), and the sound and gamepad devices it hands in.

## A frame

`EmulatorSession.RunFrame()`:

1. Polls the host gamepad (MZ-700 and MZ-800; never in a `--dump`
   run, so a captured frame doesn't depend on what's plugged in).
2. Calls `Active.RunFrame()` once per speed step (1–4; once while
   paused). Inside, the machine ticks its keyboard's typer and staged
   keys, runs the CPU for a frame's worth of cycles while clocking
   the PIT and raising VBLANK, and renders the frame. A breakpoint
   stops the frame early and pauses the machine.
3. Counts the frame (`FrameCount` is in real-time frames, whatever
   the speed).
4. Gives the `AutoLoadOrchestrator` its turn: ready checks, queued
   BASIC and cassette loads, typed BASIC source, the GRAPH-mode
   watch.
5. Raises `FrameRan`.
6. Delivers the `--keys` events due at this frame.
7. Lets the `--dump` recorder write its files if this is the dump
   frame.

`Stop()` ends it all; a scripted Alt+F4 or a finished dump calls it
part-way through a frame.

## The two front ends

**The app** (`Program.cs`, `MainForm.cs`). `Program` parses the
command line, runs the startup checks (`MatrixValidation`,
`HostKeyParity`, `HostKeyNames`), loads a `--keys` script and the settings, and opens
`MainForm`. `MainForm` builds the session, opens the WinMM sound
device and the gamepad, and runs frames off a WinForms timer. The
timer can't tick 60 times a second on Windows, so each tick runs as
many frames as real time calls for, against a `Stopwatch` and the
machine's `FramesPerSecond`, and catches up at most four frames
before it resyncs. The display and any open debug windows refresh
once per tick. Keys go
through `ProcessCmdKey` (the hotkeys) before they reach the session;
[`keyboard.md`](keyboard.md) has the details.

**The headless runner** (`tools/MZRakuHeadless/Program.cs`). The same
command line, minus the window options (accepted and ignored). It
needs `--dump`, runs frames back to back until the dump is written,
and exits with a code (0 done, 1 can't start, 2 bad `--keys` script
or `--settings` file, 3 fatal error). Scripted keys meet a copy of the
app's hotkeys first, so a script behaves the same under both; that
copy (`Hotkey`) must be kept in step with `MainForm`.

The regression suite runs every case through either front end and
compares both against the same goldens; see
[`regression.md`](regression.md).

## Settings

`Core/Session/Settings.cs` reads and writes `settings.ini` next to
the executable: never the registry or `%APPDATA%`, so a copy of the
folder is a complete install. Every section explains its own format
in comments, because users edit the file by hand. Section and key
names, and the comments `Save` writes, are a file format: renaming
one breaks existing installs. `--settings=<ini>` loads another file
read-only, and nothing that run changes is written back.

## Further reading

- [`docs/history.md`](../history.md): what changed in each release, and
  why.
- [`docs/roadmap.md`](../roadmap.md): the releases ahead.
- [`docs/v1.4-plan.md`](../v1.4-plan.md): how the core was split out
  (decisions D7–D10: the core project, the session, the regression
  suite, the headless runner).
- [`docs/v1.5-plan.md`](../v1.5-plan.md), the `v1.2-audit-*` files and
  [`keyboard-editor-test.md`](../keyboard-editor-test.md): earlier
  plans, kept because the history links to them.
