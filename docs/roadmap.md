# Project roadmap

A forward-looking plan for MZRaku's next several releases. Baseline
agreed 2026-07-25; revised 2026-08-27 to bring MZ-800 forward ahead
of the settings v2 sweep; v1.3.0 shipped 2026-10-10; revised
2026-10-17 to put the cross-platform move ahead of the UI polish
sweeps and any further machines; revised 2026-10-18 to clear the
parked bugs in v1.5.0 and to make the v2.0.0 GUI depend on how the
cross-platform CLI build is received; v1.4.0 shipped 2026-10-31
(tag only); revised 2026-11-01 to bring the docs restructure forward
into v1.5.0 and move Quick Disk to v2.x. Every placement is expected to move
as reality lands — this document is the shape, not the contract.

For what has already shipped, see `history.md`. For the current
release-in-progress detail, see the version-specific plan memories
maintained by the AI assistant.

## The shape

- **v1.x** — MZRaku grows as a Sharp emulator. v1.4.0 separated the
  emulation core from the WinForms front end; v1.5.0 adds features
  that don't depend on the UI and clears the parked bugs; v1.6.0 adds
  MZRakuCLI, a command-line-driven SDL build for Windows, macOS and
  Linux, alongside the WinForms build.
- **v2.x** — cross-platform shell, built on Avalonia unless the
  toolkit check at v2.0.0 kickoff says otherwise, **if** the
  MZRakuCLI release shows that a GUI matters off Windows. v2.0.0
  ports at parity and redesigns the settings once, in the new shell.
  Later v2.x releases add dockable panes, tabbed machine instances
  and new machines. A deliberate paradigm shift, signalled by the
  major version bump.

## Already shipped

- **v1.0.0** (2026-06-20) — first public release. MZ-700 focus.
- **v1.0.1-preview.1** (2026-07-18) — settings-persistence and
  keyboard fixes; preview only.
- **v1.1.0** (2026-08-09) — status-bar polish, MUSIC pitch
  calibration, keyboard hardening, MZ-80A settings parity. Six
  phases; Phase 6 (GRAPH click-to-type) skipped as polish-not-
  tablestakes after Phase 5.4 Font Sheet.
- **v1.2.0** (2026-08-25) — codebase audit + resulting refactors.
  Seven phases, 65 category-(b) findings + 5 late-fix items across
  41+5 commits. Tag-only close (no packaged release — refactors +
  parity fixes with no user-facing improvements; next packaged
  release is v1.3.0).
- **v1.3.0** (2026-10-10) — Sharp MZ-800 support (all display
  modes incl. MZ-1R25 16-colour, hardware + split-screen scroll,
  SN76489 PSG, Z80 PIO interrupts, joystick ports, tape + 1Z-016
  BASIC, keyboard editor and UI parity), plus the BASIC cold-start
  Overflow fix, real-time frame pacing (every machine had run at
  ~2/3 speed) and the RTC cascade fix. Release notes carry the v1.2
  reminder: users upgrading straight from v1.0.x lose custom
  `[KeyOverrides]` and explicit `[Roms]` paths — run 1.1 (or later)
  once first to migrate.
- **v1.4.0** (2026-10-31) — core extraction. The emulation core is
  its own `net8.0` library, `MZRaku.Core`, with no WinForms: plain
  pixel frames, MZRaku's own host-key codes, audio and joystick
  behind interfaces, and an `EmulatorSession` that creates and runs
  the machine. A headless runner proves it needs no window. A
  27-case regression suite (all three machines) guarded every step,
  then a code review pass renamed, trimmed and narrowed the public
  surface. Tag-only close (no user-facing change); next packaged
  release is v1.5.0.

## v1.5.0 — features that don't depend on the UI

The next packaged release after v1.3.0, and the first built on the
v1.4.0 core. Features whose substance is emulation or core logic,
with only a small UI touchpoint. They carry over to the new shell
almost unchanged, so they are safe to ship before the port.

- **Volume control and mute** — mixer-level volume plus a mute
  toggle, with a menu item, a slider, a hotkey and a `settings.ini`
  key. The current level shows in the UI.
- **MZ-800 BASIC SAVE** — completeness: trap 1Z-016's own tape-write
  entries ($382E header / $3832 data, common $3834) so SAVE writes a
  .mzf like MZ-700's S-BASIC SAVE. Today SAVE waits for a tape
  forever.
- **MZ-800 and MZ-80A Font Sheet click-to-type** — clicking a glyph
  types it, as on the MZ-700's bank 0. Deferred from v1.3.0 Phase 8.5
  once the auto-typer was machine-neutral; the MZ-80A joined on
  2026-11-01. Both sheets are view-only until then.
- **MZ-800 split ROM files** — accept the MZ-800 ROM as the three
  files other emulators use (e.g. EmuZ-800: `MZ700IPL.ROM` 4 KB,
  `FONT.ROM` 4 KB, `MZ800IPL.ROM` 8 KB) as well as the combined
  `MZ800.ROM`. Joined in that order they are byte-identical to
  `MZ800.ROM` (checked 2026-10-10), so emulation is unchanged: the
  ROM scan looks for `MZ800.ROM` first, then joins the three parts in
  memory. `[Roms.MZ800]` records either form (the unused `Font=` key
  can take `FONT.ROM`), the missing-ROM message names both forms and
  which part is missing, and release-check gains a split-files-only
  boot.
- **`--settings=<path>` CLI flag** — read-only alternate INI for
  presets and reproducible bug reports.
- **`--keys=<file>` scripted input, documented as a user feature** —
  built in v1.4.0 for the regression suite (frame-stamped `type` /
  `key` / `down` / `up`). v1.5.0 adds a syntax reference to the usage
  docs and the `--help` text, so users can automate demos and
  reproduce input-dependent bugs. The same format becomes MZRakuCLI's
  automation input in v1.6.0.
- **.mzf machine auto-detect** — sniff MZ-700 vs MZ-80A vs MZ-800
  from the cassette header; corpus study first.
- **CPU speed multiplier (1× to 4×)** — the whole machine runs
  faster (every timer, as if over-clocked) while the display,
  keyboard and joystick stay real time; sound is muted while the
  speed isn't 1×. System menu and `--speed=N`; each launch starts at
  1×.
- **Screenshot capture** — View → Save screenshot (Ctrl+P), a
  timestamped PNG in `screenshots\` next to the exe (the `--dump` path
  already writes PNGs).
  Hotkey and path configuration arrive with the v2.0.0 settings
  redesign.
- **Font Sheet: glyphs the keyboard can't type** — on the MZ-700,
  clicking a glyph no key reaches (bank-1 glyphs, and bank-0 codes
  with no key) shows the BASIC that produces it (`PRINT CHR$(n)`, or
  `POKE` to character and colour RAM) and can type that command, so
  the sheet is useful for every glyph.
- **MZ-80A tape parity** — SA-5510 `SAVE` writes a `.mzf` (trapping
  SA-1510's tape-write entries) instead of waiting for a tape, and
  Insert cassette works on the MZ-80A as on the other two machines.
  Done with MZ-800 BASIC SAVE, which uses the same pattern.
- **Documentation for two readers** (moved forward from v2.0.0) —
  user docs and developer docs become separate sets. A new
  `docs/developer/` covers the architecture (Core, Platform and UI,
  `EmulatorSession`, the headless runner), the regression suite and
  adding a machine, ahead of MZRakuCLI joining the same core. The
  user docs get one guide per machine in the shape of `mz800.md`
  (the MZ-700 and MZ-80A pages are new), a slimmer README that links
  to them, and topic pages without code notes. Written at release so
  they describe what ships; v2.0.0 then updates only the pages its
  new dialogs change.

### Clearing the decks — the parked bugs (done 2026-11-01)

v1.5.0 fixes, or deliberately closes, the bugs parked since earlier
releases, so the cross-platform work in v1.6.0 starts from a clean
list. All fourteen are dealt with; causes and fixes are recorded in
`docs/v1.5-plan.md` (Phases 1–3), and the regression suite grew from
27 to 31 cases along the way.

- **Fixed on the MZ-700:**
  - Insert cassette loads with the monitor's `L` (the read traps sat
    one routine off) and with S-BASIC's `LOAD` (S-BASIC has its own
    tape routines, now trapped too).
  - S-BASIC `SAVE` traps BASIC's real tape-write routine and returns
    to Ready, instead of being forced out with BREAK.
  - Keyboard dead after Settings Apply: opening Settings with
    Ctrl+Shift+K left MZ CTRL held, because the dialog took the
    key-ups. Held keys are now released whenever the window loses
    focus, on every machine.
  - Font Sheet GRAPH click-to-type
    ([#3](https://github.com/sgillon/MZRaku/issues/3), closed): GRAPH keys
    type bank-0 codes from their own table (S-BASIC has its own tables
    too); bank 1 is only reached by programs.
  - The boot tone, and every note the monitor's melody routine plays,
    is heard in full (a tape-wait trap had cut each one short).
- **Fixed on the MZ-80A:** BASIC cassette autorun loads without a
  typed `LOAD` (the program is relinked as SA-5510's `LOAD` does);
  PIT counter 0 counts at 2 MHz, per the schematic; the startup check
  covers the MZ-80A keyboard layout.
- **Fixed on every machine:** the Settings change summary uses each
  machine's own key labels; mute no longer queues one more 20 ms of
  sound; started from Explorer, the first key types instead of opening
  a menu.
- **Closed:** the MZ-80A cursor blink
  ([#1](https://github.com/sgillon/MZRaku/issues/1)); the MZ-80A BASIC
  start-up pause ([#2](https://github.com/sgillon/MZRaku/issues/2)), which
  is SA-1510's authentic one-second clock-set wait; an MZ-80A Sound
  Diagnostic ([#4](https://github.com/sgillon/MZRaku/issues/4)), not
  planned — menu items a machine doesn't have are now greyed out,
  with a tooltip naming the machines that have them.
- **Found along the way:** MZ-80A BASIC `SAVE` waits for a tape, and
  Insert cassette isn't offered on the MZ-80A. Both are scheduled as
  MZ-80A tape parity, above.

## v1.6.0 — MZRakuCLI: the first cross-platform build

A second front end on the v1.4.0 core: one bare SDL window showing
the machine's screen, with keyboard, sound and joystick. No menus,
status bar or dialogs; it is driven entirely by the command line and
`settings.ini`. It ships for Windows, macOS and Linux **alongside**
the Windows WinForms build, which stays the full-featured version
until v2.0.0. Two thin front ends over one core is the accepted price
for a much better foundation for the cross-platform GUI.

- **Front end** — SDL2 (via Silk.NET or equivalent) for the window,
  input, audio and joystick. The frame's pixel buffer goes to the
  screen with integer or aspect-correct scaling. MZ key mapping uses
  SDL scancodes.
- **Driving it** — the existing CLI (`--mz700` / `--mz80a` /
  `--mz800`, `--cassette`, `--basic`, `--display`, `--scanlines`,
  `--dump`, plus `--settings=` from v1.5.0) and the self-documenting
  `settings.ini`, including `[KeyOverrides]` and `.mzkbd` import.
- **Replacing the menus** —
  - drag-and-drop a file onto the window to load it;
  - hotkeys for reset, pause, quit, machine switch, insert cassette,
    Load BASIC source, volume and mute, screenshot, and fullscreen;
  - brief on-screen messages drawn into the frame (possibly with the
    machine's own font ROM) in place of the status bar.
- **Errors** — missing ROMs and bad settings give clear console
  messages and non-zero exit codes instead of dialogs.
- **Left for the GUI** — debugger, memory viewer, Font Sheet, HID /
  sound diagnostics, keyboard matrix, settings and keyboard-editor
  dialogs. A terminal-based debugger (break, step, dump memory) is a
  possible later addition.
- **Cross-platform groundwork this flushes out early** —
  - case-insensitive ROM and BASIC file matching (Linux file
    systems are case-sensitive);
  - path separators in `[Roms]` paths written on Windows;
  - where `settings.ini` lives on macOS, where "next to the exe"
    inside an `.app` bundle may be read-only, against the
    portable-settings rule;
  - MZ key mapping across host keyboard layouts.
- **Packaging** — one file per platform with SDL bundled (an `.app`
  bundle on macOS). Expected to be a few MB.
- **Testing** — Linux via WSL on Windows 11 (WSLg runs SDL apps with
  sound); macOS needs access to a Mac. The regression suite's
  `--dump` path is front-end independent, so the core stays covered.
- **Launchers** — fits LaunchBox (Windows) and command-line front
  ends such as ES-DE on Linux; `launcher-setup.md` gains an MZRakuCLI
  section.
- **It ships** as soon as it works on all three platforms, alongside
  the existing full Windows GUI build. Its reception is the input to
  the v2.0.0 decision below.

## v2.0.0 — cross-platform GUI, at parity, with the settings designed once

The paradigm shift. Signalled by the major version bump because
the shell fundamentally changes, users will notice, and things may
look different on first launch. Gated on a separate Avalonia
learning project Steve plans to take on first.

- **Whether to build it at all** — decided after MZRakuCLI has been
  out for a while. The WinForms build is already a solid emulator for
  the three machines on Windows; the open question is how much a GUI
  matters on macOS and Linux, where emulators are often run from
  launchers and the command line. Inputs: user feedback, how
  MZRakuCLI is used (launchers such as ES-DE), and which GUI-only
  tools (debugger, Font Sheet, settings and keyboard-editor dialogs)
  people miss. If a GUI isn't needed there, the WinForms build stays
  the Windows GUI, MZRakuCLI grows instead (for example on-screen
  menus or a terminal debugger), and the settings redesign and the
  v2.x items below are re-placed.
- **Toolkit decision at kickoff** — Avalonia is the working
  assumption, and the user's learning project has gone well. Before
  committing, re-check it against **SDL2 + Dear ImGui**: the common
  emulator pattern, with SDL already planned for audio and joystick,
  ImGui docking for the v2.x panes and tabs, and a much smaller
  footprint. The cost is a weaker fit for the settings dialogs and the
  keyboard editor. The v1.6.0 MZRakuCLI is already the SDL half, so the
  check is mostly whether ImGui on top of it beats Avalonia. Decide
  with a small prototype showing a machine frame, one tool panel and
  a single-file publish.
  Compare the feel, the dialog story and the download size and file
  count. Whichever wins, packaging stays one file per platform (or an
  `.app` bundle on macOS), as MZRaku is today.
- **Cross-platform port** (Avalonia unless the kickoff check says
  otherwise) — frees MZRaku from Windows. The new front end sits on
  the core extracted in v1.4.0; every v1.x window and dialog reaches
  parity.
- **Windows-specific back ends replaced** — the GUI reuses the
  v1.6.0 MZRakuCLI's SDL audio, joystick and key mapping behind the
  v1.4.0 interfaces. Z80Core is already pure portable .NET 8 and
  needs no changes. The WinForms build retires once the GUI reaches
  parity.
- **Settings v2, designed once in the new shell** — the top-to-tail
  settings review moves here rather than being done in WinForms and
  then rebuilt. The v1.1.0 widening has had several releases of real
  use plus a third machine's config surface. Includes reconsidering
  tab structure, labelling, defaults, and the direct-jump menu
  shortcuts to individual tabs (long-parked backlog item). Folding
  in:
  - **Debug menu → Settings tab folding + in-app help for debug
    panes** — the debug surface has grown enough that its menu is
    crowded, and each pane wants a context-sensitive `?` blurb.
  - **Hotkeys for the remaining menu items** — cover what Ctrl+O /
    Ctrl+B / Ctrl+R / Ctrl+S / Ctrl+M / Ctrl+H / Ctrl+G don't.
  - **Joystick-to-key mapping for MZ-80A** — MZ-80A has no hardware
    joystick, but users with a gamepad plugged in can play cassette
    games if buttons/axes map to key presses.
  - **ROMs-missing modal polish** — clearer per-machine guidance;
    identify which of the files is actually missing.
  - **Screenshot hotkey and path configuration.**
- **Scanlines full-screen filter polish**
  ([#5](https://github.com/sgillon/MZRaku/issues/5)) — rendering
  changes in the port anyway; likely a pre-built overlay with
  intensity/thickness knobs in the new settings.

## v2.x — what the new shell makes possible

- **Dockable panes** — Debugger, Memory Viewer, HID Diagnostic,
  Font Sheet, Keyboard Matrix park as side panels around the
  emulator viewport rather than juggle overlapping windows.
- **Tabbed machine instances** — multiple machines running side by
  side in one shell.
- **Sharp MZ-1500 and Quick Disk on all three QD-capable machines**
  — research done 2026-10-16/17, and the ROMs, BASIC 5Z-001, QD
  images and test software are all in hand. The MZ-1500 is close to
  the MZ-700: new work is PCG graphics, palette and priority, a
  second SN76489 for stereo, the Japanese character set and kana
  keyboard. The MZ-1F11 drive, the MZ-1500's built-in drive and the
  interfaces for the MZ-700 (MZ-1E14) and MZ-800 (MZ-1E19) are the same
  hardware at the same ports, so one QD device serves the MZ-700,
  MZ-800 and MZ-1500. Own feasibility plan first. Quick Disk on the
  MZ-800 was a v1.5.0 stretch item until 2026-11-01; it waits for
  this, alongside the plotter and printer work below.
- **MZ-80K and MZ-80B** — inherit the `IMachine` pattern from the
  existing machines but each has its own memory map, I/O, and
  character ROM.
- **BASIC source editor in a side window** — read the live BASIC
  program out of emulated RAM, render in an editor pane, write
  edits back. Machine-aware (S-BASIC on MZ-700, SA-5510 on MZ-80A,
  1Z-016 on MZ-800). Non-trivial: BASIC tokenisation, preserving
  line-record layout, not letting edits land mid-RUN.
- **MZ-1P01 plotter emulation** — MZ-700-only. Separate window
  consuming the plotter command stream; needs command-protocol
  research. Substantial work.
- **MZ-800 printer port** — the printer side of the Z80 PIO (port B
  = 8-bit data, port A handshake lines; DIP switches pick the MZ-1P16
  plotter-printer or Centronics). Two steps: (1) an always-ready
  printer capturing text to a file next to the exe, so `LPRINT` /
  `LLIST` / `PRINT/P` work instead of waiting forever — handy for
  getting BASIC listings out of `.mzf` files; (2) MZ-1P16 graphics
  (hard copies, plots) in a plotter window, sharing design and
  rendering with the MZ-1P01 item above.

## Parked (no version yet)

Nothing at present: v1.5.0's "Clearing the decks" fixed or closed
everything parked up to v1.4.0.

## Aspirational (no version yet)

- **Z80Core longer arc** — spin-out complete; the next step is a
  family of standalone 8-bit CPU cores (6502, 6809, 8080/8085, MOS
  8502) under an umbrella "Ultimate 8-bit CPU Library." The trigger
  for taking this seriously is when a second core is actually
  started, not before. Multi-CPU emulation (e.g. Commodore 128 as a
  Z80+8502 host) is a bus-arbitration problem living in the host
  emulator, not a library-shape problem.

## Principles behind the placements

- **1.x is evolutionary, 2.x is transformational.** New features fit
  1.x. UI-paradigm changes require the major bump.
- **Refactors don't ship with features.** v1.2.0 exists so the
  audit doesn't have to elbow its way into a feature release; v1.4.0
  does the same for the core extraction.
- **Regression coverage before refactoring.** Golden images are
  captured on the code being changed, never on the result.
- **UI polish happens once, in the shell it will live in.** The
  settings redesign and the other dialog-level polish wait for
  Avalonia rather than being built in WinForms and then rebuilt.
- **Cross-platform starts small.** A menu-less MZRakuCLI proves the
  core, the SDL back ends and the platform quirks before any GUI work
  depends on them, and its reception decides whether the GUI port is
  needed at all.
- **Parked bugs don't outlive a feature release.** Bugs parked or
  recorded during one arc are cleared, or deliberately closed, in the
  next feature release (v1.5.0 for everything up to v1.4.0).
- **The platform move comes before new machines.** Three machines
  are enough to prove the core extraction and the port; MZ-1500,
  MZ-80K and MZ-80B land under the new shell. (Earlier, the MZ-800
  landed before the settings sweep so that sweep could design
  against all three machines; that reasoning still holds, and the
  sweep now happens in v2.0.0.)
- **Externally-gated work parks at the release it doesn't block.**
  Avalonia is gated on a separate learning project; v1.4.0 and
  v1.5.0 are useful whatever that project's timing.
