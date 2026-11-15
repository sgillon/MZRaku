# Adding a machine

The checklist the MZ-80A (v1.0.1–v1.1.0) and the MZ-800 (v1.3.0)
followed, updated for the code as it is now. This page is for whoever
adds the next one; v2.x plans new machines (the MZ-1500 and the
MZ-80K/B are the candidates). Read [`architecture.md`](architecture.md)
first.

It describes v1.5.0. v1.6.0 reworks the keyboard layer, which will
change the keyboard items below; check
[`keyboard.md`](keyboard.md) for what that rework settled.

## Before any code

- **A feasibility study and a plan.** Both machines started with a
  written study: what the machine is next to the ones MZRaku already
  has, subsystem by subsystem (memory map, video, sound, keyboard,
  cassette, BASIC), what is reused, what is new, the risks and open
  questions, and a phase plan. The MZ-800's is in the private
  information repository (`MZ-800/Research/MZ800-FEASIBILITY.md`),
  with a plan per large phase.
- **The firmware and documents.** The monitor ROM, the character ROM
  and the BASIC tape, plus the owner's and service manuals; the
  service manual settles I/O and timer wiring. None of these is ever
  committed: ROMs and software are Sharp or publisher copyright, and
  live in the git-ignored `_mz-information-repository/`.
- **A reference emulator** to compare screens and sound against,
  where one exists (EmuZ-800 was the MZ-800's).

## The phase order

The order both machines followed, each phase ending with something
that runs:

1. **Selection.** `MachineType`, the `--<machine>` flag, the
   System → Machine menu entry, the settings sections, and a machine
   class that does nothing yet.
2. **Boot.** Machine class, memory map and I/O bus: the monitor runs
   and draws its prompt into VRAM (check in the memory viewer before
   there's a picture).
3. **Text video.** The prompt on screen.
4. **Keyboard.** The matrix reference first, then the special-key map,
   char map and keyboard class, checked key by key on the machine's
   own monitor.
5. **Cassette and BASIC.** The tape traps, BASIC autoload, loading a
   cassette from the menu and the command line.
6. **The rest of the hardware,** one phase each: on the MZ-800, the
   bitmap video, the PIO and interrupts, the PSG and joystick.
7. **Integration.** Settings dialog, keyboard editor, diagnostics,
   About, status bar, docs, regression cases.

## The checklist

Every place that names a machine today. Search the code for the
newest machine (`Mz800`, `MZ800`, `MachineType.MZ800`) to find any
this list has missed.

### Core: the machine's own folder (`Core/<MZxxxx>/`)

Every file prefixed with the machine (`Mz800…`); copy the shape of
the closest existing machine.

- [ ] **Machine class** (`MZ800.cs`): derives from `MzMachineBase`,
  implements `IMachine`. Constructor wires the CPU's `Mem` and `Io`,
  the chips onto the I/O bus, the keyboard onto the PPI, and the
  cassette on `Cpu.PreStep`. `RunFrame` ticks the keyboard (auto-typer
  and staged keys), runs the CPU for a frame, clocks the timers,
  renders. `FramesPerSecond` is the real frame rate (the MZ-800's is
  PAL, ≈ 50.08). Constructor `internal`.
- [ ] **Memory** (`…Memory.cs`): Z80Core's `IMemory`, plus `IsIoRead`
  for the addresses the debugger must not read (memory-mapped I/O with
  side effects).
- [ ] **I/O bus** (`…IoBus.cs`): Z80Core's `IIoBus`, for machines with
  port I/O.
- [ ] **Video** (`…Video.cs`): renders into a `FrameBuffer` at native
  size. Check the character ROM's bit order: SA-CG.rom is MSB-first,
  `mz700fon.int` LSB-first.
- [ ] **Keyboard**: `…MatrixReference.cs` (every cell, from the
  manuals; the char map is built from it), `…SpecialKeyMap.cs`,
  `…CharMap.cs` and `…CharMapOverrides.cs`, `…Keyboard.cs` (derives
  from `KeyboardMatrixBase`, implements `IKeyboardInput`),
  `…KeyboardLayout.cs` (the physical keyboard, for drawing it),
  `…KeyTables.cs` (the ROM's key tables read backwards, for the Font
  Sheet). See [`keyboard.md`](keyboard.md).
- [ ] **Cassette** (`…Cassette.cs`): derives from `CassetteTrapBase`.
  Find the monitor's and BASIC's tape read and write code by
  disassembly, trap it there, and check the bytes at each trap. See
  [`tape-traps.md`](tape-traps.md).
- [ ] **ROM loading:** `LoadRoms`, and a loader of its own if the ROM
  comes in more than one layout (`Mz800RomFiles`).

### Core: shared code

- [ ] `Core/Machines/MachineType.cs`: the new value. Its name is
  written to `settings.ini` (`DefaultMachine=`), so choose it once.
- [ ] `Core/Devices/`: reuse the chips that are the same (8255, 8253,
  Z80 PIO, SN76489); a new chip goes here if another machine could use
  it. Same chip doesn't mean same wiring: the MZ-80A's interrupt mask
  bit has the opposite sense to the MZ-700's.
- [ ] `Core/Keyboard/MatrixValidation.cs`: add the new reference,
  special-key map, char map and layout validators.
- [ ] `Core/Keyboard/KeyboardMapFile.cs`: the machine's name in
  `.mzkbd` files.
- [ ] `Core/Session/Settings.cs`: `[Roms.<MACHINE>]`,
  `[KeyOverrides.<MACHINE>]`, `[CharMap.<MACHINE>]` and any
  `[Hardware.<MACHINE>]`, each explained in the comments `Save`
  writes; the firmware names `EnsureRomPaths` looks for; the
  `DefaultMachine` value. Section and key names are a file format
  from the first release that writes them (which is why the MZ-700's
  char-map section is plain `[CharMap]`: it predates the second
  machine).
- [ ] `Core/Session/EmulatorSession.cs`: create the machine and hand
  it its settings and overrides; `MachineLabel`; `ExpectedFirmware`;
  a ready check (what the ROM draws in VRAM once it's waiting for
  keys); `NeedsBasic` (which cassette types are BASIC programs);
  the branches in `LoadCassette` and `InsertCassette`;
  `TypeIntoActiveMachine`; `ApplySettings` for settings that change
  live; the `--dump` trace logs in `Start`.
- [ ] `Core/Session/AutoLoadOrchestrator.cs`: the machine's pipeline:
  wait for ready, load BASIC, wait for BASIC's Ready, load the
  cassette or type the source, the mode pane.
- [ ] `Core/Session/DumpTraceRecorder.cs`: the trace line and state
  dump for the machine's chips.

### Front ends

- [ ] `Program.cs`: the `--<machine>` flag, and the `--help` text
  (flags and firmware names).
- [ ] `tools/MZRakuHeadless/Program.cs`: the same flag and usage text.
- [ ] `MainForm.cs`: the System → Machine entry, the flag used when
  switching (`SwitchMachine` relaunches with it), status bar items
  and mode panes, which menu items the machine offers, the keyboard
  editor context.
- [ ] `UI/AboutForm.cs`: the machine's name.
- [ ] `UI/Debugger/MemoryViewerForm.cs`: the bank-state line and its
  help text.
- [ ] `UI/Diagnostics/`: HID Diagnostic (keyboard, mode, joystick
  lines), Font Sheet (key tables), Sound Diagnostic (`Supports`, and
  its gate lines).
- [ ] `UI/Keyboard/`: a `…KeyboardEditorContext.cs` and
  `…PhysicalKeyboardLayout.cs`, and the machine's row in
  `MzKbdIoCoordinator.cs`.
- [ ] `UI/Settings/SettingsForm.cs` and `SettingsDiff.cs`: ROM rows,
  hardware options, keyboard options, and the summary of what Apply
  changes.

### Tests and docs

- [ ] **Regression cases** (`tools/regress/regress.py`): the firmware
  with SHA-1 pins, then at least the monitor boot, BASIC boot, typing,
  music, a machine-code cassette, a BASIC cassette, Insert + LOAD,
  SAVE, and the keys and monitor-typing cases. See
  [`regression.md`](regression.md).
- [ ] `tools/regress/settings.ini`: the shipped defaults, with the new
  sections.
- [ ] `docs/release-check.md`: a section for the machine.
- [ ] `docs/usage/<machine>.md`, in the shape of `mz800.md`, and the
  README's firmware list and machine list.
- [ ] `docs/developer/keyboard.md` and `tape-traps.md`: the machine's
  rows and sections.
- [ ] `docs/history.md`: the arc, as it lands.

## Things learned the hard way

- **Detect, don't wait.** Autoload waits for what the ROM draws (the
  monitor banner, the prompt, BASIC's Ready) rather than a frame
  count. SA-5510's cold start takes about 3.3 seconds, so the
  MZ-700's 60-frame wait fired too early on the MZ-80A.
- **Frame rate is real.** The MZ-800 runs at its PAL rate; a 60 Hz
  guess would have made every timer and tune wrong.
- **Same address, different code.** With the monitor banked out, its
  trap addresses belong to BASIC or the program. Check the memory map
  and the bytes before a trap acts.
- **Write down the addresses you find,** with how you found them, in
  the class comments: the next person will need them, and so will
  you.
