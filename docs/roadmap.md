# Project roadmap

A forward-looking plan for MZRaku's next several releases. Baseline
agreed 2026-07-25 during a dedicated roadmap-planning session. Every
placement is expected to move as reality lands — this document is the
shape, not the contract.

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

## v1.1.0 — status-bar polish, MUSIC pitch, keyboard hardening, settings parity

Six phases, single tag at the end. Phase 1 shipped 2026-07-19.
Remaining phases are the current work.

1. **TAPE activity chip + PAUSED overlay** — SHIPPED (`8b277bd`).
   Includes global pause hotkey (Pause / Scroll Lock) and
   mute-on-pause.
2. **MUSIC / sound pitch calibration** — MZ-80A MUSIC plays one
   octave above real hardware; fix the PIT counter 1 input clock.
   Optional bonus: MZ-700 TEMPO re-validation now that MUSIC notes
   are discrete on both machines.
3. **Shift-race fix** — long-standing bug where held Shift +
   repeated letter occasionally slips to lowercase, and where
   `'`/`@` on UK layout occasionally swaps. Both machines.
4. **MZ-80A keyboard polish, round 2** — empirical audit of
   residual anomalies (punctuation, GRAPH glyphs) with the HID
   Diagnostic pane open.
5. **Settings dialog GUI parity for MZ-80A** — the big phase. Char
   map editor extended to MZ-80A, green/amber tints promoted from
   menu to Display tab, `[CharMap.MZ80A]` split, the amber "MZ-80A
   partial coverage" banner retired.
6. **GRAPH click-to-type** (stretch) — finish the Font Sheet bank-1
   attribute bug from `graph-clicktotype-parked`. Skip if it
   rabbit-holes; the stretch label is genuine.

## v1.2.0 — codebase audit + resulting refactors

Housekeeping, hygiene, and issue resolution. **No new features unless
an opportunity from the audit is too good to pass up.**

Ethos: deliberately harsh on ourselves. YAGNI-first — the default
answer to "should this stay?" is "no". Softened by two considerations:

1. **Learning-project value** — MZRaku is also a vehicle for
   Steve's re-engagement with software development. Code that
   exists to make a concept legible earns its keep even if a
   production codebase would inline it.
2. **Documented future plans** — items with a specific placement
   in this roadmap can stay as scaffolding, provided the placement
   is real and near-term (v1.3.0 or v1.4.0), not aspirational.

Structure: **report first, fixes after.**

- **Phase A — Audit report.** Written to
  `docs/v1.2-audit-findings.md`. No code changes. Each finding
  categorised as (a) critical / v1.1.1 hotfix candidate, (b)
  v1.2.0 fix target, or (c) longer-arc parked. Categorisation is
  the load-bearing decision — it's what stops v1.2.0 sprawling.
- **Phase B — Fix passes.** Category (b) findings become v1.2.0's
  ordered phases, drafted as a fresh plan memory once the report
  lands.
- **Phase C — Automated test seed.** `Z80Core.Tests/` xUnit project
  (Layer 1: ZEXDOC/ZEXALL exerciser; Layer 2: targeted regression
  canaries for known Z80 bugs; Layer 3: disassembler golden tests
  when needed). Small `MZRaku.Tests/` for host-side round-trips
  (Settings INI, CharMap lookups). Runs as a release-check step,
  not per-commit CI (single-dev cadence).

Seed audit areas (non-exhaustive; the real audit will find more):

- Parallel machine classes with converged shape
  (`Cassette`↔`Mz80aCassette`, `CharMap`↔`Mz80aCharMap`) — extract or
  leave alone
- MainForm's `if _machine != null / else if _mz80a != null` branching
- `Ppi8255` bit-name events (`SpeakerGateChanged` etc.) — MZ-700
  semantics on a shared type
- Dormant scaffolding (invisible-LOAD path, pointer-hunt diagnostic)
- Sound Diagnostic pane gating on MZ-80A
- `Hardware/` file organisation — flat vs `MZ700/`, `MZ80A/`
  subfolders
- `internal` vs `public` surface consistency
- Stale XML doc comments post-MZ-80A landing

## v1.3.0 — polish features + settings v2

Everything that isn't a refactor and isn't a new machine.

- **Top-to-tail settings review** — the settings dialog was
  substantially widened in v1.1.0 Phase 5; a period of real use
  should surface where the UX wants a coherent second pass rather
  than incremental patches. Includes reconsidering tab structure,
  labelling, defaults, and the direct-jump menu shortcuts to
  individual tabs (long-parked backlog item).
- **CPU speed multiplier (1× / 2× / 3× …)** — authentic emulation
  speed: the frame's Z80 cycle budget is scaled, so the guest
  experiences actual turbo (game action accelerates, sound rises
  in pitch, like real hardware over-clocked). Needs a v1.2.0 audit
  touchpoint on the frame-timing loop first. Implementation
  considerations to settle at design time: whether display refresh
  rate scales too, whether audio pitch tracks or resamples, and
  what the sane multiplier ceiling is.
- **Screenshot capture** — hot-key, save as PNG next to the exe,
  timestamped filename. Copy-to-clipboard variant TBD at design
  time.
- **`--settings=<path>` CLI flag** — read-only alternate INI for
  presets and reproducible bug reports.
- **.mzf machine auto-detect** — sniff MZ-700 vs MZ-80A from the
  cassette header; corpus study first.
- **Scanlines full-screen filter polish** — the per-row FillRectangle
  approach doesn't scale well to full-screen; likely wants a
  pre-built overlay bitmap and intensity/thickness knobs.
- **Hotkeys for the remaining menu items** — cover what Ctrl+O /
  Ctrl+B / Ctrl+R / Ctrl+S / Ctrl+M / Ctrl+H / Ctrl+G don't.
- **ROMs-missing modal polish** — clearer per-machine guidance;
  identify which of the three files is actually missing.
- **Joystick-to-key mapping for MZ-80A** — MZ-80A has no hardware
  joystick, but users with a gamepad plugged in can still play
  cassette games if buttons/axes map to key presses.
- **Debug menu → Settings tab folding + in-app help for debug
  panes** — the debug surface has grown enough that its menu is
  crowded, and each pane wants a context-sensitive `?` blurb.
- **Invisible-LOAD path for MZ-80A cassette autorun** — dormant
  follow-up to skip the typed-LOAD ceremony; blocked on identifying
  which SA-5510 state RUN needs that LOAD doesn't currently set.
- **MZ-700 sound loose ends** — boot tone / other imperfect timings,
  unless already resolved as a side effect of v1.1.0 Phase 2.

## v1.4.0 — MZ-800 support

Third Sharp machine under the existing WinForms shell. Feasibility
review already complete — see `_mz800info/MZ800-FEASIBILITY.md`.

Same Phase 0-6 shape as the MZ-80A landing:

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
- **Polish comes after real use.** v1.3.0's settings v2 review is
  scheduled after v1.1.0's settings widening has had time in
  anger; the second pass reflects how the first pass actually
  landed.
- **Externally-gated work parks at the release it doesn't block.**
  Avalonia is gated on a separate learning project; putting it at
  v2.0.0 respects the gate without leaving intermediate releases
  waiting.
