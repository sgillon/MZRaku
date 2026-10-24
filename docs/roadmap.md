# Project roadmap

A forward-looking plan for MZRaku's next several releases. Baseline
agreed 2026-07-25; revised 2026-08-27 to bring MZ-800 forward ahead
of the settings v2 sweep; v1.3.0 shipped 2026-10-10; revised
2026-10-17 to put the cross-platform move ahead of the UI polish
sweeps and any further machines; revised 2026-10-18 to clear the
parked bugs in v1.5.0 and to make the v2.0.0 GUI depend on how the
cross-platform CLI build is received. Every placement is expected to move
as reality lands — this document is the shape, not the contract.

For what has already shipped, see `history.md`. For the current
release-in-progress detail, see the version-specific plan memories
maintained by the AI assistant.

## The shape

- **v1.x** — MZRaku grows as a Sharp emulator. v1.4.0 separates the
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

## v1.4.0 — core extraction (refactor)

Make the emulation core independent of WinForms, so that the
Avalonia port is a new front end rather than surgery across the
codebase. No user-facing change, so it is a **tag-only** close
like v1.2.0; the next packaged release is v1.5.0, built on the
refactored core. It can proceed alongside the Avalonia learning
project that gates v2.0.0.

Today the core is nearly portable already: `Hardware/` and the
machine classes are about 15,000 lines against about 8,800 lines of
WinForms UI. WinForms leaks into the core in three narrow places:

- **Video** — `IMachine.VideoFrame` and the per-machine video
  renderers produce a `System.Drawing.Bitmap`.
- **Keyboard** — the keyboard matrices, special-key maps and
  diagnostics work in WinForms `Keys` (and `Control.ModifierKeys`).
- **Sound and joystick** — `WinmmWaveOut` and `JoystickInput` call
  winmm.dll directly.

**Step 1 — regression coverage for every machine, captured before
any refactor.** The current golden-image suite (18 cases) is almost
all MZ-800. Each machine gets around five or six cases, approved on
the pre-refactor code. The refactor is then checked against them.

- A scripted host-key test hook (for example `--keys=`, giving a list
  of keys and the frame to press each). The auto-typer writes
  straight into the keyboard matrix, so it bypasses the host-key
  mapping that this refactor replaces. Each machine gets a typing
  case through the hook.
- **MZ-700** — monitor boot, S-BASIC boot, Dragon Caves (large BASIC
  load, the cold-start Overflow case), two machine-code games (one
  compared as audio), a `MUSIC` program compared as audio, scripted
  typing.
- **MZ-80A** — monitor boot, BASIC boot, a machine-code game, a typed
  LOAD + RUN cassette game (the autorun path), a `MUSIC` program
  compared as audio, scripted typing.
- **MZ-800** — keep the existing cases (each covers a distinct
  rendering path) and add scripted typing.
- **Home for the suite** — the harness script is tracked in the
  repo. The golden images are screenshots of ROM and game content,
  so they stay in a git-ignored folder. Test software is read from
  the local information repository. Target: a full run in about two
  minutes.

**Step 2 — the extraction itself.**

- Video frames become a plain pixel buffer; the WinForms front end
  converts to `Bitmap` at the edge.
- Host keys become MZRaku's own key codes; the WinForms front end
  maps `Keys` onto them.
- Audio output and joystick input sit behind interfaces, with the
  winmm implementations as the Windows back ends.
- Optionally the core moves into its own class library, with the
  WinForms app as one front end.

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
- **MZ-800 Font Sheet click-to-type** — clicking a glyph types it,
  as on the MZ-700's bank 0. Deferred from v1.3.0 Phase 8.5 once the
  auto-typer was machine-neutral; the MZ-800 Font Sheet is view-only
  until then.
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
- **CPU speed multiplier (1× / 2× / 3× …)** — authentic emulation
  speed: the frame's Z80 cycle budget is scaled, so the guest
  experiences actual turbo (game action accelerates, sound rises in
  pitch, like real hardware over-clocked). Design considerations at
  implementation time: whether display refresh rate scales too,
  whether audio pitch tracks or resamples, and what the sane
  multiplier ceiling is.
- **Screenshot capture** — hot-key, save as PNG next to the exe,
  timestamped filename (the `--dump` path already writes PNGs).
  Hotkey and path configuration arrive with the v2.0.0 settings
  redesign.
- **Stretch: Quick Disk on the MZ-800** — mostly a device model (Z80
  SIO at $F4–$F7 plus a `.mzq` disk image) with one "mount disk" menu
  item; the MZ-800's own IPL ROM already boots from QD. Can slip to
  v2.x with the MZ-1500, which shares the same QD hardware.

### Clearing the decks — the parked bugs

v1.5.0 also fixes, or deliberately closes, the bugs that have been
parked since earlier releases, so the cross-platform work in v1.6.0
starts from a clean list. Where a dump can show the fix, it gains a
regression case (`--keys=` makes keyboard bugs scriptable).

- **Keyboard dead after Settings Apply (MZ-700)** — after editing a
  key mapping in Settings and pressing Apply or OK, no keys type until
  Ctrl+R. Present since v1.0.0; two bisects in 2026-08 found no
  mechanism. Approach: log the keyboard matrix and the held-key state
  around the Settings close rather than bisecting again. **Didn't
  reproduce on 2026-10-24:** the recorded steps (Settings → Keyboard
  → Advanced settings → click a matrix cell → capture a PC key → Save
  → close Advanced → Apply/OK) typed normally straight away in v1.1.0,
  v1.3.0 and the v1.4.0 work in progress, each run from fresh default
  settings. No commit claims a fix, so the trigger needs something
  those runs lacked — existing key overrides in `settings.ini` are the
  first suspect. Find the missing condition and fix it, or close it.
- **Font Sheet bank-1 (GRAPH) click-to-type on the MZ-700**
  ([#3](https://github.com/sgillon/MZRaku/issues/3)) — the click lands
  the character byte but not the attribute, so the wrong glyph
  appears. Pairs with the MZ-800 Font Sheet click-to-type item above.
- **Invisible-LOAD path for MZ-80A cassette autorun** — skip the
  typed LOAD + RUN; blocked on Error 16 from SA-5510's RUN. Find which
  state RUN needs that the direct load doesn't set, or close it and
  keep the typed path.
- **MZ-700 sound loose ends** — the boot tone and other imperfect
  timings. Phase 0.3 of v1.4.0 recorded one concrete case: the boot
  tone's 138-cycle hard-gate pulse comes before the counter reload,
  which only takes effect at frame end.
- **MZ-80A cursor blink rate**
  ([#1](https://github.com/sgillon/MZRaku/issues/1)) and **BASIC load
  pause** ([#2](https://github.com/sgillon/MZRaku/issues/2)) — both
  rechecked 2026-10-04 and largely cured by the v1.3.0 frame-pacing
  fix. Confirm and close the issues, or record why they stay open.
- **Anything v1.4.0 records** — the core extraction records bugs
  rather than fixing them; they land here.

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
- **Usage docs restructure for three machines** — `docs/usage/`
  grew MZ-700-first: the topic files (keyboard, joystick, HID
  diagnostic, hardware notes) describe the MZ-700, the MZ-80A has
  almost no usage coverage, and v1.3.0 shipped the MZ-800 as its own
  page (`mz800.md`) to get it complete in time. Target shape: topic
  files cover all three machines, plus a short page per machine for
  what is genuinely machine-specific (files needed, starting it,
  loading behaviour, display options, limitations). Done once the
  new UI has settled, so the docs describe the new dialogs once.

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
  MZ-800 and MZ-1500. Own feasibility plan first.
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

Nothing at present: the items parked at v1.3.0 moved into v1.5.0's
"Clearing the decks". Background for the two MZ-80A issues, from the
2026-10-04 recheck:

- **Cursor blink rate** ([#1](https://github.com/sgillon/MZRaku/issues/1))
  — rechecked against EmuZ-80A and now blinks at about the same rate.
  The "~2× slower" report predates the v1.3.0 Phase 6.1 frame-pacing
  fix (every machine ran at ~2/3 speed); the blink is frame-driven
  (PPI toggle every 20 frames), independent of C1/C2. The real source
  is a separate cursor timer reset via $E000 D7, not modelled.
- **BASIC load pause** ([#2](https://github.com/sgillon/MZRaku/issues/2))
  — the pause is now much shorter (user rechecked); it was inflated
  by the pre-Phase-6.1 ~2/3-speed frame pacing. SA-5510 still has an
  authentic up-to-1 s post-tone wait for the RTC to tick; switching
  MZ-80A `AutoLoadBasic` to the typed-LOAD path would hide it.

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
