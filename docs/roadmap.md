# Project roadmap

A forward-looking plan for MZRaku's next several releases. Baseline
agreed 2026-07-25; revised 2026-08-27 to bring MZ-800 forward ahead
of the settings v2 sweep. Every placement is expected to move as
reality lands — this document is the shape, not the contract.

For what has already shipped, see `history.md`. For the current
release-in-progress detail, see the version-specific plan memories
maintained by the AI assistant.

## The shape

- **v1.x** — MZRaku grows as a Sharp emulator. Each release adds
  capability, polish, or a new machine, without changing the shell
  paradigm.
- **v2.x** — cross-platform shell, dockable panes, tabbed machine
  instances. A deliberate paradigm shift, signalled by the major
  version bump.

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

## v1.3.0 — MZ-800 support + cold-start Overflow kickoff

Third Sharp machine under the existing WinForms shell, preceded by
a short bug-fix kickoff. Brought forward from v1.4.0 so all three
machines are in scope before the settings v2 sweep runs — the sweep
then covers the full gamut rather than being retrofitted after a
two-machine design lands.

**Kickoff:**

- **Cold-start Overflow fix** — long-standing pre-v1.2 bug: certain
  BASIC .mzf programs cold-start with screen corruption + Overflow
  Error on LOAD+RUN, clean after any prior MC run in the session.
  Known reproducer: Dragon Caves (1982). Instrument the two paths
  and diff RAM state at the moment RUN is issued rather than
  bisecting — no regression, mechanism is likely RAM-state-sensitive.

**Main body — MZ-800 support.** Feasibility review already complete —
see `_mz800info/MZ800-FEASIBILITY.md`. Same Phase 0-6 shape as the
MZ-80A landing:

- Machine-selection foundation extended for a third target
- MZ800 boot spike (Z80 running SA-1510-equivalent from `MZ800.ROM`)
- Video (three-way mode split: native bitmap + palette, MZ-700
  compat, hardware scroll)
- Keyboard (matrix already documented in the feasibility doc)
- Cassette + BASIC (`1Z-016.mzf` typed autoload)
- Sound (SN76489 PSG; Z80 PIO)
- UI polish

Approximately 6-8 focused sessions. The `_mz800info/` folder already
holds ROM, BASIC MZF, three machine-code test cassettes, and the
Sharp tech-ref + service manuals.

**Independent polish that doesn't want MZ-800 or settings v2 in
scope:**

- **`--settings=<path>` CLI flag** — read-only alternate INI for
  presets and reproducible bug reports. Doubles as a way to test
  MZ-800 with preset INIs during landing.
- **MZ-80A cursor blink rate** ([#1](https://github.com/sgillon/MZRaku/issues/1))
  — currently ~2× slower than real hardware / EmuZ-80A. The v1.1.0
  Phase 2 C1 tick-rate fix didn't affect it, so derivation is
  likely independent of C1/C2 and needs its own investigation.
- **MZ-80A BASIC load UX-parity** ([#2](https://github.com/sgillon/MZRaku/issues/2))
  — SA-5510 has an authentic ~1s post-tone wait for the RTC to
  tick, exposed as a raw pause by our DirectInject. Switching
  `AutoLoadBasic` for MZ-80A to the typed-LOAD path (already used
  for MC cassettes) would hide it in perceived loading time.
- **Invisible-LOAD path for MZ-80A cassette autorun** — dormant
  follow-up to skip the typed-LOAD ceremony; blocked on identifying
  which SA-5510 state RUN needs that LOAD doesn't currently set.
- **MZ-700 sound loose ends** — boot tone / other imperfect timings,
  unless already resolved as a side effect of v1.1.0 Phase 2.

**Release-note reminder from v1.2** — users upgrading straight from
v1.0.x → v1.3+ without launching v1.1 or v1.2 lose custom
`[KeyOverrides]` and explicit `[Roms]` paths (v1.1's auto-migration
is now assumed complete). Surface in the v1.3.0 release notes as
"run 1.1 (or later) once first to migrate."

## v1.4.0 — settings v2 sweep + settings-adjacent polish

Everything that either folds into or benefits from the completed
three-machine surface. Deferred from v1.3.0 so the settings sweep
designs against the full gamut of settings — MZ-700, MZ-80A, and
MZ-800 — in one coherent pass, rather than being retrofitted after
MZ-800 lands under a two-machine design.

**Main sweep:**

- **Top-to-tail settings review** — the settings dialog was
  substantially widened in v1.1.0 Phase 5; two more releases of
  real use plus a new machine's config surface should tell us what
  the UX wants in one coherent second pass rather than incremental
  patches. Includes reconsidering tab structure, labelling,
  defaults, and the direct-jump menu shortcuts to individual tabs
  (long-parked backlog item).
- **Debug menu → Settings tab folding + in-app help for debug
  panes** — the debug surface has grown enough that its menu is
  crowded, and each pane wants a context-sensitive `?` blurb. Folds
  into the sweep.
- **Joystick-to-key mapping for MZ-80A** — MZ-80A has no hardware
  joystick, but users with a gamepad plugged in can play cassette
  games if buttons/axes map to key presses. Folds into settings v2.
- **Hotkeys for the remaining menu items** — cover what Ctrl+O /
  Ctrl+B / Ctrl+R / Ctrl+S / Ctrl+M / Ctrl+H / Ctrl+G don't. Folds
  into settings v2.
- **ROMs-missing modal polish** — clearer per-machine guidance;
  identify which of the three files is actually missing. Now covers
  all three machines coherently.

**Cross-machine polish that benefits from all three machines being
in place:**

- **.mzf machine auto-detect** — sniff MZ-700 vs MZ-80A vs MZ-800
  from the cassette header; corpus study first.
- **CPU speed multiplier (1× / 2× / 3× …)** — authentic emulation
  speed: the frame's Z80 cycle budget is scaled, so the guest
  experiences actual turbo (game action accelerates, sound rises in
  pitch, like real hardware over-clocked). F-066's shared
  AccumulatePit helper is already queued for the MZ-800 landing so
  the frame-timing loop will be freshly touched. Design
  considerations at implementation time: whether display refresh
  rate scales too, whether audio pitch tracks or resamples, and
  what the sane multiplier ceiling is.
- **Screenshot capture** — hot-key, save as PNG next to the exe,
  timestamped filename. Copy-to-clipboard variant TBD at design
  time. Hotkey/path config lands via settings v2.
- **Scanlines full-screen filter polish**
  ([#5](https://github.com/sgillon/MZRaku/issues/5)) — the per-row
  FillRectangle approach doesn't scale well to full-screen; likely
  wants a pre-built overlay bitmap and intensity/thickness knobs
  exposed via settings v2.

## v1.5.0 — cross-machine polish features

Larger features that benefit from all three machines being in place.

- **BASIC source editor in a side window** — read the live BASIC
  program out of emulated RAM, render in an editor pane, write
  edits back. Machine-aware (S-BASIC on MZ-700, SA-5510 on MZ-80A,
  1Z-016 on MZ-800). Non-trivial: BASIC tokenisation, preserving
  line-record layout, not letting edits land mid-RUN.
- **MZ-1P01 plotter emulation** — MZ-700-only. Separate window
  consuming the plotter command stream; needs command-protocol
  research. Substantial work.

## v2.0.0 — Avalonia + shell redesign

The paradigm shift. Signalled by the major version bump because
the shell fundamentally changes, users will notice, and things may
look different on first launch.

- **Cross-platform port to Avalonia UI** — frees MZRaku from
  Windows. Gated externally on a separate Avalonia learning project
  Steve plans to take on first.
- **Dockable panes** — Debugger, Memory Viewer, HID Diagnostic,
  Font Sheet, Keyboard Matrix park as side panels around the
  emulator viewport rather than juggle overlapping windows.
- **Tabbed machine instances** — multiple machines running side by
  side in one shell.
- Windows-specific subsystems that need replacing alongside the UI
  swap: `WinmmWaveOut` (winmm.dll P/Invoke → Silk.NET wrapping SDL2
  + OpenAL, or equivalent) and `JoystickInput` (same treatment).
  Z80Core is already pure portable .NET 8 and needs no changes.

**MZ-80K and MZ-80B** are eligible for landing under the new shell
as v2.x follow-ups. They inherit the `IMachine` pattern from the
existing three machines but each has its own memory map, I/O, and
character ROM.

## Aspirational (no version yet)

- **Z80Core longer arc** — spin-out complete; the next step is a
  family of standalone 8-bit CPU cores (6502, 6809, 8080/8085, MOS
  8502) under an umbrella "Ultimate 8-bit CPU Library." The trigger
  for taking this seriously is when a second core is actually
  started, not before. Multi-CPU emulation (e.g. Commodore 128 as a
  Z80+8502 host) is a bus-arbitration problem living in the host
  emulator, not a library-shape problem.

## Principles behind the placements

- **1.x is evolutionary, 2.x is transformational.** New machines
  and features fit 1.x. UI-paradigm changes require the major bump.
- **Refactors don't ship with features.** v1.2.0 exists so the
  audit doesn't have to elbow its way into a feature release.
- **New machines land before major polish sweeps.** MZ-800 lands
  at v1.3.0 so the settings v2 sweep at v1.4.0 covers the full
  three-machine gamut. This also gives the settings sweep even more
  time in anger and avoids retrofitting MZ-800 items into a
  settings design that was drawn against two machines.
- **Polish comes after real use.** v1.4.0's settings v2 review is
  scheduled after the v1.1.0 widening has had two full releases
  worth of use; the second pass reflects how the first pass
  actually landed.
- **Externally-gated work parks at the release it doesn't block.**
  Avalonia is gated on a separate learning project; putting it at
  v2.0.0 respects the gate without leaving intermediate releases
  waiting.
