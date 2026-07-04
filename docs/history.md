# Project history

A chronological record of how MZRaku evolved, written for a future
maintainer (most likely the project owner one year on). Focuses on
the *what* and *why* of significant changes — what shipped, what
decisions were made, what rationale drove them — at the level of
detail you'd want when re-orienting in the codebase after a long
gap.

For a more conversational account of the journey including dead ends
and reflections, see the personal journal at
`_journal/journey.md` (local-only, not in the repo).

Dates and commit hashes come from `git log`. Rationale comes from
the contemporaneous backlog memory the AI assistant maintains.
Authoritative status: the codebase itself.

---

## Origins (2026-05-03)

The project began with two stated goals:

1. Play the MZ-700 games the owner remembers from childhood.
2. Be launchable from Launchbox / Playnite with auto-loading of
   cassette images and pre-loading of BASIC where required.

Implicit choices:
- "Well enough" rather than cycle-accurate.
- Command-line operability for launcher integration.

Stack selected: **C# / .NET 8 WinForms**. C# was a familiar language;
WinForms was the path of least resistance for "open a window, blit a
framebuffer, accept keystrokes" on Windows. The Windows-only tradeoff
was accepted up front.

First "Initial commit: Sharp MZ-700 emulator (C#/.NET WinForms)" was
`83ddc1b`, 2026-05-03 21:57.

---

## Timeline of significant changes

### 2026-05-03 to 2026-05-09 — Foundation

- Audio wired through the 8253 PIT. `CyclesPerTempoToggle = 35469`
  (≈50 Hz) calibrated empirically against a Nightmare Park tune that
  Steve had recorded against real hardware.
- "Detect state, don't delay" established: a 180-frame "wait for
  monitor ready" delay replaced with VRAM-banner detection
  (`MONITOR 1Z*`). Now a project principle (see *Principles* below).
- **Per-VK keymap replaced with char-driven keyboard input**
  (`545f985`, 2026-05-09). The OS resolves keystrokes to Unicode
  characters; the emulator maps those characters to MZ matrix
  positions by glyph. Foundational decision — still in place,
  underpins the layered keyboard model added much later.

### 2026-05-10 — Quality-of-life and joystick

- Display scaling 1×/2×/3× with INI-backed preferences (`c312e4e`).
- Zipped cassette images accepted (`7f3ca88`).
- MZF type-byte inspection for auto-dispatch (BASIC vs machine code)
  (`316a77b`).
- ROM paths moved into a `[Roms]` section of `settings.ini`
  (`ca201f2`) — first time the INI grew structure.
- **MZ-1X03 joystick emulation** added (`67aeb1c` XInput, then
  `66a32cf` switched to WinMM `joyGetPosEx` to cover non-Xbox
  controllers).
- Pulse width matched to real MZ-1X03 timing (`6e9737f`) so
  panic.mzf detects direction inputs reliably.

### 2026-05-14 — Joystick calibration; Phase 1 debugger

- `CyclesPerCount = 33` calibrated against panic.mzf's sampling
  offsets (~1490 and ~7390 cycles after VBLK fall) (`22cfd2c`).
- **Debugger Phase 1** (`2c1f540`): execution control
  (pause/resume/step/step-frame via Ctrl+D, F5/F10/F11), live Z80
  register view, address-based breakpoints. Non-blocking pause —
  `RunFrame` early-returns when paused, keeping the screen and
  debugger panes live.

### 2026-05-15 — Phase 2 debugger; memory viewer

- **Z80 disassembler** (`1057f6a`): algorithmic x/y/z decoder for all
  prefix families. Disassembly pane with PC + breakpoint highlighting,
  double-click-to-toggle-breakpoint, Goto $, Follow PC,
  kb/mouse-wheel navigation.
- **Memory viewer** (`44bf363`) brought forward from a later phase
  because the trek-bug investigation would need it. Hex / ASCII with
  PC + SP row shading, byte underline, Goto, quick-jump buttons.
  $E000-$E00F shown as `--` to avoid I/O side-effect reads.
- `SmoothControls.cs` (SmoothLabel / SmoothListBox /
  SmoothTableLayoutPanel) introduced to mitigate WinForms flicker on
  dense per-frame redraws. Swallows `WM_ERASEBKGND` and uses
  `TextRenderer.DrawText` (GDI) instead of `Graphics.DrawString`
  (GDI+).

### 2026-05-16 — Public release prep (v0.0.5-preview)

- README split into a front door + topic pages under `docs/usage/`
  (debugger, memory viewer, keyboard, joystick, hardware notes).
  Quickstart explicitly instructs sourcing user-supplied ROMs.
- BASIC-missing modal fires consistently across all entry points
  (CLI, BASIC-cassette auto-load, Load BASIC source, menu).
- `MZRaku.csproj` switched to a conditional glob so local
  copyrighted files don't break a fresh-clone build and never leak
  into a publish.
- `.gitignore` patterns added to guard user-supplied ROM / BASIC /
  cassette files.
- Sharp's ROMs, BASIC interpreter, and copyrighted manuals scrubbed
  from all git history via `git filter-repo` and force-pushed.
- Backup bundle saved at
  `D:\Development\VSCode projects\mz700emul-pre-scrub-backup.bundle`.
- Tagged `v0.0.5-preview` and published as a GitHub pre-release.

### 2026-05-22 — Repo flipped public

No commit marks this — an upstream visibility change.

### 2026-05-23 — Trek var-bug arc + major architecture shifts

The most consequential single day in the project's history. All in
one evening:

- **Z80 indexed INC/DEC fix** (`45bd7a2`): `INC/DEC (IX+d)` /
  `(IY+d)` were double-fetching the displacement byte (once via
  `GetR`, again via `SetR`), so each instruction consumed two stream
  bytes — the real `d` and the next opcode reused as a phantom `d` —
  read at one address and wrote at a different (corrupt) one, leaving
  PC off-by-one. `INC (HL)` was fine because `GetHLorIdxWithDisp`
  doesn't fetch when `_idx == 0`. Fix: special-case
  `y == 6 && _idx != 0` to fetch `d` once. S-BASIC's float-to-string
  display routine uses indexed INC/DEC, which is why `PRINT 1.5`
  showed `1` and `trek.mzf` mis-formatted float game state. Found via
  ZEXDOC.
- **Z80 test harness** (same commit) — CP/M-style runner in
  `Z80TestRunner.cs` + `Z80TestForm.cs`: loads `.com` at `$0100`,
  traps BDOS at `$0005` (fn 2 putchar, fn 9 print$-string), exits on
  `PC=$0000`. Permanent infrastructure; reused for any future Z80-
  level investigation. Default location `tools/CPM/`.
- **Cassette SAVE** (same commit) — empirically-discovered S-BASIC
  internals: outgoing tape header at `$0FFC` (not `$10F0` as the
  monitor uses), trap point `$0D47` with ROM banked out, exit via
  setting CY=1 from `$02C8 BreakWait`. Tape SAVE bypasses the monitor
  jump-table entirely; the trap captures the header + bytes and
  writes a `.mzf`.
- **Z80 core extracted to its own csproj** (`db8e9ed`):
  `Z80Core/Z80Core.csproj` → `Z80Core.dll`. Pure net8.0, no WinForms,
  no MZ-700 specifics. The disassembler's `$E000-$E00F` quirk became
  a `Func<ushort, bool>?` predicate the host passes in. Decision
  rationale: enable reuse for other Z80-based machines (Spectrum,
  Amstrad, MSX, CP/M, eventually MZ-80K and MZ-80B). Eventual goal —
  spin out to its own repo. *See "Clean-room Z80 core" principle.*
- **NAudio dropped** (`2738210`): direct WinMM `waveOut*` P/Invoke
  via `Hardware/WinmmWaveOut.cs`. Zero third-party runtime
  dependencies; the same DLL the joystick code already uses.
- **Single-file release publish** (`194306f`): wired into the csproj
  via `<PublishSingleFile>`, `<DebugType>embedded</DebugType>`, and
  `<CopyToPublishDirectory>Never</CopyToPublishDirectory>` on the
  conditional ROM/BASIC include. v0.0.6-preview tagged the same
  evening. Two assets: `…-dotnet8.zip` (~150 KB,
  framework-dependent), `…-standalone.zip` (~63 MB, self-contained).

### 2026-05-24 — Launcher setup docs

- `docs/usage/launcher-setup.md` (`b9c35dd`, `67d6bde`): Launchbox
  setup as the first step-by-step launcher integration guide.
- Quickstart linked to launcher setup (`d83c075`).
- README acknowledgments (`4aeff18`).

### 2026-05-30 — Polish phase (run-up to v0.0.7-preview)

- **Window focus on drag-drop** (`513a9ec`): added `Form.Activate()`
  to the cassette drop handler.
- **Auto-typer rewrite — scan detection** (`245e830`): the previous
  fixed 12-frame hold replaced with a state machine that advances
  when the keyboard's row scans are observed.
  `Keyboard.ReadRow` sets a per-row scan-tracker bit; the typer
  cycles Idle → AwaitShiftScan (shifted only) → AwaitKeyScan →
  AwaitRelease → EnterCooldown (Enter only) → Idle. Throughput
  ~6-8 chars/sec (was ~3-4), shifted-char drops eliminated. Enter
  keeps a 30-frame empirical cooldown for BASIC's line-parse pause.
  Application of the "detect, don't delay" principle.
- **`*` keymap fix + brackets/braces** (`ae6a883`): `*` had been
  mapped to slot (5,0) shifted (which is `(`); the correct slot is
  (0,1) shifted (verified against ROM shifted table at `$0C30`,
  display code `$6B` = `*`). Brackets `[` `]` and braces `{` `}`
  added at slots (1,3) / (1,4) ± shift in the same pass.
- **BREAK key mapping fix** (`ba21f78`): Esc had been bound to slot
  (8,5) but BASIC's break poll at `$04A9` does
  `LD A,($E001); AND $81; RET Z` — masking bits 0 (SHIFT) and 7. So
  BREAK is at slot (8,7), paired with shift. Updated SpecialKeyMap
  and `Cassette.IsBreakHeld`. Discovered via per-frame row-8 scan
  diagnostic.
- **MZ-1X03 button bindings configurable** (`1057f16`):
  `[Joystick]` section gained `Button1=N` / `Button2=N`.
  `JoystickInput.SetButtonIndices` applies at startup;
  `Settings.Load` flushes the file when expected sections are missing
  so existing INIs auto-acquire the new block.

### 2026-05-31 — Settings UI, layered keyboard model (v0.0.7-preview)

- **Tabbed Settings dialog** (`5c29476`): `SettingsForm.cs` with
  Display / ROMs / Joystick tabs, opened via File → Settings… or
  **Ctrl+S** (chosen over Ctrl+, because nothing else in the menu
  uses Ctrl+S). OK / Apply / Cancel pattern; `Applied` event for
  pushing live changes (display scale, joystick button bindings).
  ROM path changes wait for next launch (monitor/font) or next Load
  BASIC.
- **Joystick Capture flow** (`JoystickCaptureForm.cs`): modal that
  asks the user to press a controller button rather than guess the
  index. Already-held buttons are masked out to avoid insta-fire.
- **MZ-1X03 reference image** embedded as an `EmbeddedResource` so it
  ships inside the single-file publish. Shrunk from 1051×1048 / 384 KB
  to 300×299 / 72 KB (`9cf9cb7`) before tagging.
- v0.0.7-preview tagged and published.
- **Layered keyboard model** (`68ce873`, same evening) — the big
  one. Three layers consulted in order:
  1. **Override** (`Hardware/KeyOverride.cs`) — user-editable, keyed
     by PC virtual key with optional modifier combinations.
  2. **SpecialKeyMap** (existing, formalised) — built-in
     non-character defaults: Enter, cursors, BREAK, GRAPH (F11),
     ALPHA (F12), MZ Ctrl, F1-F4.
  3. **CharMap** (existing) — printable glyphs via KeyPress
     character resolution.

  GRAPH/ALPHA mode toggling support landed in the same commit
  (BASIC's mode flag at `$0060` decoded: bit `0x10` set = GRAPH).
  Override layer persists to `[KeyOverrides]` in `settings.ini`.
- **Known issue discovered:** WinForms collapses `LControlKey` /
  `RControlKey` to generic `Keys.ControlKey` in `KeyEventArgs.KeyCode`,
  so SpecialKeyMap entries for the L/R variants never matched. GRAPH
  / ALPHA had to move from AltGr/RCtrl to F11/F12 as unambiguous
  defaults. **Proper fix:** WndProc lParam-bit-24 extended-key
  detection (tracked as a future cross-cutting commit — will also
  fix MZ Ctrl via PC Ctrl).

### 2026-06-01 — Release-readiness checklist, local-dir convention

- **`docs/release-check.md`** (`7de5801`): manual pre-release smoke
  test, grouped by area. Trigger: a pre-existing Shift+letter
  regression (Shift+P 10× yielding `PPpPPPpPPP`) had been hiding
  since v0.0.5 because nothing exercised lowercase letters
  pre-release. Includes "verify still broken" entries for known
  issues so they don't quietly start passing. Regression canaries:
  `PRINT 1.5`, `trek.mzf`, Shift+P x10, Shift+8 x10.
- **`_*/` gitignore pattern** (`4e6b3f1`): folders prefixed with `_`
  at the repo root are local-only working dirs (scratch dumps,
  session artefacts, downloaded reference material). Never
  committed.

### 2026-06-02 — HID Diagnostic window

- **`HidDiagnosticForm.cs`** (`70043b5`): Ctrl+H opens a live view
  of host input + mapping + MZ matrix state. Three panes refreshed
  per frame; non-focus-stealing on open
  (`ShowWithoutActivation = true`, no `BringToFront()`).
- **`docs/usage/hid-diagnostic.md`** (`1b4c3e4`): user-facing
  documentation in the same style as debugger.md / memory-viewer.md.

### 2026-06-04 — Keyboard editor in flight

Two principles established in this session and saved as durable rules:
- **Portable settings**: all user config persists *next to the
  executable* (`settings.ini`); never `%APPDATA%` / registry / per-user
  paths. Aligns with the emulator's portable-binary stance. "Survives
  reinstalls" use case is met by Import/Export of portable files, not
  by moving the live config.
- **Self-documenting INI**: every section must explain its format
  inline. `[KeyOverrides]` was the bar; everything else now matches.

Phase A of the keyboard-map editor is mid-build:
- **A1** (`e8a96fb`): `VideoRenderer.GetGlyph` + Font Sheet diagnostic.
- **A2** (`5527e9f`): `MzGlyphCatalog` — aggregates `CharMap.Defaults`
  + `SpecialKeyMap.SlotLabels` for the editor.
- **A2.5 / A2.6** (`cbf5a20`): `RomKeyTables` reverses the monitor
  ROM's key-translation tables (`$0BEA` unshifted, `$0C2A` shifted)
  into a display-code → slot inverse map. Click-to-input on the Font
  Sheet enqueues the resulting press through the existing auto-typer.
  Filters out slots whose codes are scan-side markers for mode keys
  (ALPHA, GRAPH, cursors) since those codes never reach VRAM. Graphics
  glyphs requiring GRAPH-mode ROM tables report as "not reachable from
  the keyboard" (proper support is a later enhancement).
- **A3** (`05003cc`): `CharMapOverrides` — sparse delta layer over
  `CharMap.Defaults`. `CharMap.TryLookup` consults Overrides first.
- **A4** (`328852a`): `[CharMap]` persisted; `[Display]` / `[Roms]` /
  `[Joystick]` / `[KeyOverrides]` retrofitted to the self-documenting
  standard. INI parser extended to strip inline `;` comments.

A5–A12 still pending. Phase B (Override editing via the editor UI) to
follow. Step-by-step plan and decisions captured in the
`project_keyboard_editor_plan` memory.

### 2026-06-05 to 2026-06-07 — Keyboard editor: diagnostics and diagram-first UI

- **`KeyCaptureControl` + Debug → Key Capture Test** (`d572f7f`):
  isolated key-capture widget for the editor flow.
- **`KeyboardMatrixGrid` + Debug → Keyboard Matrix…** (`25601eb`):
  live 10×8 matrix grid showing asserted bits per frame. Pairs
  naturally with the HID Diagnostic.
- **Settings → Keyboard tab (read-only)** (`d48642b`): matrix view +
  overrides list. First Settings tab past the original Display / ROMs
  / Joystick three.
- **`KeyBindingEditorForm`** (`814ae02`): live edits to the CharMap
  layer.
- **Phase 2 — diagram-first MZ key binding UI** (`0ad3375`,
  2026-06-07): the Settings → Keyboard tab redrawn as a clickable
  rendering of the actual MZ-700 keyboard, each cap badged with its
  current PC binding. Click a cap → per-key editor (unshifted /
  shifted cards) → capture PC key → Save. Safety gate outlines
  unreachable MZ keys in crimson and prompts before Apply lets them
  through. Export / Import `.mzkbd`.
- **GRAPH-mode ROM key tables found; bank-1 click-to-type parked**
  (`222b22c`): Font Sheet bank-1 cells type a byte but the attribute
  ends up wrong, so glyphs come out coloured but in the wrong
  palette. Tables at `$0BEA` / `$0C2A` / `$0C6A` / `$0CAA`. Marked
  as a known limitation rather than rushed — see
  `project_graph_clicktotype_parked` memory.

### 2026-06-12 — MZ Ctrl reachability fix, F5, About dialog

- **MZ Ctrl + F5 slot fix** (`42229c8`): two stacked bugs. (1) VK
  normalisation — `SpecialKeyMap` bound `Keys.LControlKey` /
  `Keys.RControlKey`, but WinForms collapses both to generic
  `Keys.ControlKey` so neither entry ever fired. (2) Wrong matrix
  slot — codebase had MZ CTRL at `(9, 2)`, but pressing `(9, 2)`
  in S-BASIC produced `CHR$(`, revealing it as a shifted-F5 macro
  alias. Owner's Manual confirms CTRL is at `(8, 6)`. Same
  investigation pinned F5 at `(9, 3)` and wired it through PC F5
  (BASIC's default F5 macro types `CHR$(`).
- **`AboutForm`** (`7749da4`): icon, version (read from
  `AssemblyInformationalVersion` — bump `<Version>` in the csproj
  and the About dialog refreshes), build date, GitHub + launcher-
  setup links, Sharp + Claude acknowledgements. Replaces the
  one-liner MessageBox.
- **Application icon** (added 2026-06-04, `47d6454`): a Sharp `mz`
  wordmark, single source `MZRaku.ico` used as the Explorer / taskbar
  / alt-tab / title-bar icon and embedded for AboutForm's runtime
  pickup.

### 2026-06-13 — Canonical matrix reference

The keyboard matrix `(row, col)` coordinates were independently
encoded in `SpecialKeyMap`, `CharMap`, `MzKeyboardLayout`, and
`tools/RomAnalyse`. Drift between them had let the CTRL = (8,6)
bug above hide for weeks. Fix is structural:

- **`Mz700MatrixReference.cs`** (`683002e`) encodes the matrix as
  data — every slot's expected glyph(s) and special-key role.
- **Reconciliation pass** (`d8de85f`) reduced three further drifts
  to bring CharMap and MzKeyboardLayout into agreement with the
  reference.
- **Startup validation** (`03691ef`) — consumer files validate
  against the reference at boot; a drift surfaces as a "Matrix
  validation drift" `MessageBox`, not weeks later as a typing bug.

This established the **canonical-reference pattern**: when data is
encoded across multiple parallel files, build one reference + start-
up validators rather than chasing drifts individually. Re-applied
later for the sound subsystem (`Mz700SoundReference`).

### 2026-06-14 — v0.0.8-preview ships, UI/ reorganisation

- **Release-host-held matrix bits on Reset** (`d2f3493`): Ctrl+R
  previously left MZ CTRL asserted on the matrix (PC Ctrl down had
  asserted (8,6); Reset didn't clear it) until the host released PC
  Ctrl. `Keyboard.ReleaseAll` now runs as part of Reset.
- **Unbound-slot panel** (`f821382`) in the advanced keyboard view:
  reference cells that nothing currently reaches surface here, so
  omissions like the original F5 gap can't lurk silently again.
- **Known-limitations panel** in Settings → Keyboard (`5bc1331`):
  the three parked items (bank-1 click-to-type, MZ-shift assertion
  race, no L/R Ctrl distinction) listed at the bottom of the tab.
- **AboutForm build-date under single-file** (`7e7de16`):
  `Assembly.Location` returns `""` under `PublishSingleFile`, so the
  About dialog's "Built …" line read epoch. Fixed by reading the
  exe's mtime via `AppContext.BaseDirectory`. Caught by the release
  checklist walk.
- **`UI/` reorganisation** (`139f7bc` `3019332` `50bbb67` `4ae320c`
  `3cabd09`): WinForms surfaces grouped into `UI/Keyboard/`,
  `UI/Debugger/`, `UI/Diagnostics/`, `UI/Settings/`, plus AboutForm
  and SmoothControls at `UI/` root. Pure file moves; no behaviour
  change.
- **v0.0.8-preview tagged and released.**

### 2026-06-18 — Keyboard tightening, debugger persistence

- **Default-suppression layer in CharMapOverrides** (`d683864`) —
  per-slot deletes overlay alongside the existing per-slot
  overrides.
- **Slot-replace + case-pair in the slot editor** (`6808daf`) — when
  rebinding a slot that's already in use elsewhere, the editor offers
  to clear the prior binding too, so it never has to be hunted down
  by hand.
- **Change summary before save** (`79e5662`) — Settings → Apply now
  pops a short diff of what's about to change before persisting, so
  it's clear what's about to be applied.
- **MZ-only glyph safety-gate exemption** (`0cac555`) — the
  unreachable-key check exempts slots whose glyphs are MZ-only by
  design (POUND/↓, the AT-slot shifted reversed-apostrophe). Crimson
  outlines now only fire on actually-broken bindings.
- **Deep-link the four Settings tabs from File menu** (`c702bb7`) —
  File → Settings → ROMs/Display/Keyboard/Joystick with hotkeys
  Ctrl+S / Ctrl+Shift+D / Ctrl+Shift+K / Ctrl+Shift+J.
- **MZ-shift assertion race fix** (`26b765b`, `a762e36`) — moved
  shift-state ownership into `Keyboard`, and live key bits are
  staged a couple of frames after the MzShift state changes via
  `$1170`, so the ROM scan sees a consistent (shift, key) pair
  rather than the key with stale cached shift. Pre-existing race
  in all releases since v0.0.5.
- **Persisted debugger / memory-viewer geometry + breakpoints**
  (`72c883d`) + **persisted main window location** (`1e926c2`) —
  Settings.ini gained `[Window]`, `[Debugger]`, `[MemoryViewer]`,
  `[Breakpoints]` sections.
- **Full-screen + CRT scanlines** (`ddd0b2a`) — View → Full-screen
  (Alt+Enter), View → Scanlines (Ctrl+L). CLI overrides:
  `--display=full|fs`, `--scanlines=on|off`. Scale + Scanlines
  persist in `[Display]`. F11 stayed as the GRAPH binding — Alt+Enter
  for full-screen sidesteps the conflict.

### 2026-06-19 — Sound: speaker-NAND hard gate at $E008 D0

The original v0.0.5-era complaint "boot tone doesn't play" turned
out to be a symptom of a broader pipeline gap, not the actual bug.

- **Schematic re-read** of `_technicaldocs/PIT circuit.png` confirmed
  IC7E LS74 has *two* flip-flops doing *two* jobs:
  - **FF1 — hard gate.** D = bus D0, CK = IC6F LS02 NOR(MW, CSE2)
    (rising edge on every write to a CSE2 address — `$E008` is one),
    CL = system RESET, Q drives the second input of the speaker-amp
    NAND. So writing `D0=1` to `$E008` opens audible sound; `D0=0`
    silences it regardless of C0's state. ROM does *both* during
    the boot beep — open, set frequency, close — entirely within one
    frame.
  - **FF2 — soft gate.** D from PC4-derived net, CK = PC3, Q drives
    PIT `GATE0` via an IC8C 7417 buffer. The "PC3 enables sound"
    simplification was already modelled.
- **The bug.** IoBus had been dropping `$E008` writes with a
  guess-comment calling them "interrupt latch clear". Nothing in the
  pipeline reflected the hard gate, so:
  - Boot tone: the synth started the tone but never stopped it. We
    were getting *no* boot tone before this because the synth wasn't
    silenced at the close, but it was silenced indirectly via PIT
    control writes mid-boot.
  - MUSIC: produced one continuous re-pitched warble instead of
    discrete notes, because each note opened C0 and never closed it.
  - Game sounds: anything that toggled `$E008` to gate audio (most
    sound effects) was silent.
- **The fix** (`66d83b0`). `Sound.HardGate` volatile bool added;
  `Sound.FeedLoop` ANDs `Enabled && HardGate` to decide whether to
  emit samples; `IoBus.MemOut` latches `$E008 D0` into `HardGate`
  and raises an `OnE008Write` event for diagnostics. `MZ700.Reset`
  clears `HardGate` (FF1.CL is the schematic RESET line).
- **Sound Diagnostic window** (`66d83b0`): Debug → Sound Diagnostic
  surfaces a live event log of PIT control / counter writes, PC3
  transitions, and `$E008` traffic, alongside a state pane showing
  soft gate, hard gate, and the audible AND.
- **Boot tone is sub-frame.** Once both gates were modelled, the
  ROM's open-close cycle showed up in the diagnostic log as
  `$E008 ← $01` immediately followed by `$E008 ← $00` within one
  frame — confirming the "missing" boot tone is *inherently*
  inaudible on real hardware too (cross-checked against EmuZ-700,
  which also produces no boot tone). The literal beep was never the
  problem; the audio pipeline gap was.
- **Sound reference encoded** in `Hardware/Mz700SoundReference.cs`
  using the same canonical-reference pattern: counter spec,
  programmed mode, gate source, speaker-NAND gate, expected events
  (boot tone, MUSIC). Cited from the service manual where possible
  and marked Empirical where derived from in-emulator observation.

### 2026-06-20 — Project rename, MIT license, v1.0.0

- **Project renamed MZ700Emul → MZRaku** (`977e745`, `c0b94e3`).
  Portmanteau of MZ + Japanese 楽 (*raku*, "easy / comfortable /
  relaxed"). The old name was a working title that read fine as
  a directory but always sat awkwardly as a brand. Sweep: namespace,
  `MZRaku.csproj` / `AssemblyName` / `RootNamespace` /
  `ApplicationIcon`, output exe, README + docs, embedded UI strings
  (title bar, About header, usage text, HID Diagnostic header). Class
  names that model the *hardware* (`MZ700`, `MZ700Memory`,
  `Mz700SoundReference`) deliberately unchanged. GitHub repo renamed
  to `sgillon/MZRaku` via `gh repo rename`; the old
  `sgillon/Mz700Emul` URL continues to redirect, so existing
  bookmarks keep working.
- **MIT license** (`d408b51`) — first explicit license. `LICENSE`
  at repo root; `NOTICES.md` acknowledges the GPL-v2 ZEXDOC/ZEXALL
  binaries in `tools/CPM/` as a "mere aggregation" under GPL v2 §2
  (no GPL code is linked into the MZRaku build, and the release
  zips don't redistribute the binaries — they remain in the source
  tree as guest-software test inputs only). Both release zips
  bundle the LICENSE.
- **v1.0.0 tagged and released** with `-dotnet8.zip` (~270 KB,
  framework-dependent) and `-standalone.zip` (~63 MB, self-contained,
  `EnableCompressionInSingleFile=true`).

---

## Architectural decisions worth knowing

### Char-driven keyboard input (2026-05-09)

The OS resolves keystrokes into Unicode chars; the emulator maps
those chars to MZ matrix positions by *glyph*, not by VK. This means
host keyboard layouts (QWERTY, AZERTY, JIS) work without
per-layout configuration — `'@'` lands on the MZ `@` slot regardless
of how the host produced it.

Tradeoff: non-character keys (cursors, F-keys, GRAPH/ALPHA) don't
fire WinForms `KeyPress`, so they need a separate path. This became
the **SpecialKeyMap** layer (always present) and later the
**Override** layer (user-editable) on top.

The layered model is `Override → SpecialKeyMap → CharMap` consulted in
order. See `Hardware/Keyboard.cs` `OnKeyDown`.

### Non-blocking debugger pause

When `Paused`, `MZ700.RunFrame` early-returns without stepping the
CPU but still calls `Video.Render(Mem.Vram, Mem.Aram)`. The screen
and all debugger / diagnostic panes stay live; no thread blocking.
This is essential for the debugger panes (disassembly with
PC-highlight, register view, memory viewer) to remain useful while
paused.

### Detect side-effect addresses via predicate (2026-05-23)

The disassembler used to hardcode `$E000-$E00F` (PPI/PIT I/O window)
as "show as zero, don't read through". When the Z80 core was extracted
to its own library, this MZ-specific assumption became a
`Func<ushort, bool>?` predicate the host passes in.
`Z80Disassembler.Disassemble(mem, addr, isSideEffectAddr)`. The
MZ-700 predicate `IsMzIoWindow` lives in `DebuggerForm.cs`. *Same
pattern applies if other side-effect ranges are discovered.*

### Z80 core as a standalone library (2026-05-23)

`Z80Core/` is a separate `<ProjectReference>` from the host. Pure
net8.0, no WinForms, no MZ-700-specific code. Depends only on
`IMemory` and `IIoBus` interfaces, plus an optional `PreStep` trap
hook and the disassembler's side-effect predicate. *When adding
MZ-specific behaviour, extend the host's hook into the core; never
add `if (addr == 0x_MZ_SPECIFIC_)` branches inside `Z80Core/`.*

Eventual destination: spin out to its own git repo. Pre-spin-out
tidy-up tracked in the backlog (standalone test harness + library
README).

### Two-shape INI parsing

`settings.ini` uses a simple `[Section]` + `key=value` format with
inline `;` comment stripping (as of 2026-06-04). Sections are
documented in the file itself via comment blocks above their entries —
the file is the documentation surface for hand-editors. *Adding a
new section: declare a property on `Settings`, parse in `Load`, write
in `Save` with a full self-documenting comment block. The
"missing section auto-Save" heuristic propagates new comment blocks
to existing INIs on next launch.*

### Cassette SAVE bypasses the monitor

S-BASIC implements its own tape SAVE rather than calling the
monitor's `$002A` / `$002D` jump-table entries. Header lives at
`$0FFC` (not the monitor's `$10F0`). The trap is at `$0D47`
with ROM banked out; exit is via setting CY=1 from `$02C8 BreakWait`,
which BASIC interprets as a break and bails (~30 second exit time —
ugly but reliable). See `Hardware/Cassette.cs`. *If extending tape
support — verify, alternative formats — these addresses are the
correct entry points.*

### Single-file release publish

`<PublishSingleFile>`, `<DebugType>embedded</DebugType>`, and
`<CopyToPublishDirectory>Never</CopyToPublishDirectory>` on the
conditional ROM/BASIC include. The framework-dependent release is
~270 KB; the self-contained release is ~63 MB (the latter requires
`-p:EnableCompressionInSingleFile=true` at publish time — without it,
the bundle balloons to ~160 MB). Dev's local Sharp ROMs / BASIC never
leak into a publish. *Don't relax the `CopyToPublishDirectory=Never`
without an alternative copyright guard.*

### Speaker NAND dual-gate model (2026-06-19)

MZ-700 audio is gated by two independent flip-flops on IC7E LS74,
not one. Both must be open for audible sound:

- **Soft gate (FF2)** — clocked by PPI PC3; Q drives PIT GATE0 via
  IC8C 7417. Modelled as `Sound.Enabled`.
- **Hard gate (FF1)** — D=bus D0, clocked on writes to `$E008` (the
  CSE2-decoded address), CL=system RESET; Q is the second input of
  the speaker-amp NAND. Modelled as `Sound.HardGate`. Writing
  `D0=1` to `$E008` enables audible sound; `D0=0` silences it
  regardless of the PIT counter's state.

The `FeedLoop` ANDs the two. *Don't fold either gate into the other,
even when only one program seems to use one of them — boot tone uses
hard-gate-only transitions to start and stop within a single frame,
and that's what makes the literal "boot beep" inaudible on real
hardware too.* Canonical specification in
`Hardware/Mz700SoundReference.cs`.

### Canonical references + startup validation (2026-06-13)

When the same data is encoded across several files, build one
reference file that holds it as data and have the consumer files
validate against it at startup. The keyboard matrix
(`Mz700MatrixReference`) and the sound subsystem
(`Mz700SoundReference`) both follow this shape. A drift surfaces as a
loud `MessageBox` at boot — not as a typing bug weeks later. *When
introducing a new consumer of an already-referenced dataset, derive
from or validate against the reference; don't re-encode by hand.*

### Z80Core spun out to its own repository (2026-06-28)

The Z80 CPU emulator that had been kept clean-room inside MZRaku
since the 2026-05-23 extraction now lives in its own repo at
[sgillon/Z80Core](https://github.com/sgillon/Z80Core), tagged
**v1.0.0**. MZRaku consumes it as a git submodule mounted at the
same `Z80Core/` path, so the existing `<ProjectReference>` resolves
unchanged. The clean-room rule still applies inside the standalone
repo. Fresh clones now need `git submodule update --init` (or
`--recurse-submodules`) before `dotnet build`.

The new repo carries comprehensive documentation under `docs/`:
`usage.md` (how to wire the core into a host), `architecture.md`
(internals — partial-class split, opcode decoding, prefix state
machine, WZ/MEMPTR, cycle accounting), `debugger-hooks.md`
(breakpoints, PC trace, the PreStep ROM-trap pattern with worked
examples), `disassembler.md` (side-effect-aware reads), and
`zex-validation.md`.

The ZEXDOC/ZEXALL test harness that used to live in MZRaku's Debug
menu (`UI/Debugger/Z80TestRunner.cs` + `Z80TestForm.cs`) moved to
the new repo as `samples/ZexHarness/`, a standalone console app
purely on top of the public surface. MZRaku no longer carries
`tools/CPM/` or `NOTICES.md` as a result. *To re-validate the
emulator's CPU after a Z80Core change, clone Z80Core, `dotnet run
--project samples/ZexHarness` against `zexdoc.com` / `zexall.com`,
and check every line ends in `OK`.*

Distribution model is git-submodule for now, not NuGet — the user
wanted reuse across personal projects without a publish/version
cycle yet. NuGet remains an option for later.

### Sharp MZ-80A support (2026-07-04)

MZRaku is now a two-machine emulator. `--mz80a` or the new
`File → Machine → MZ-80A` menu boots into a Sharp MZ-80A (Sharp's
1982 successor to the MZ-80K, sibling to the domestic MZ-1200)
instead of the default MZ-700. Both machines coexist in one binary
and one `settings.ini`.

Rough shape of the port:

- **Foundation.** New `MachineType` enum and `IMachine` interface
  in `Hardware/`. Both `MZ700` and `MZ80A` implement `IMachine`
  covering the minimum surface MainForm and the debugger panes
  need (Cpu, Mem, Paused, RunFrame, Reset, LoadRoms, AutoLoad*,
  step controls, Kind). MZ-700-specific hardware fields (Ppi,
  Pit, Sound, Joystick, Video, Cassette, Keyboard, KeyTables)
  stay off the interface; panes that need them cast to the
  concrete class or gate their menu items on `Kind`. Settings
  gained a `[Machine] Type=` section and split `[Roms]` into
  `[Roms.MZ700]` / `[Roms.MZ80A]` sub-sections with legacy
  migration on save.

- **Memory + I/O.** `Hardware/MZ80AMemory.cs` implements the
  MZ-80A layout (Owner's Manual Fig 3.2): 4 KiB ROM at `$0000`,
  48 KiB RAM `$1000-$CFFF`, 4 KiB VRAM window `$D000-$DFFF`, MMIO
  `$E000-$EFFF`, floppy stub `$F000-$FFFF`. The memory-swap
  mechanism (ROM ↔ `$C000`) toggles on **reads** to `$E00C` /
  `$E010` rather than MZ-700's port-OUT bank switch — the Read
  path handles the toggles before dispatching to `Mz80aIoBus`.
  `Hardware/Mz80aIoBus.cs` wires PPI + PIT at the same
  `$E000-$E007` block but with the MZ-80A-specific bit
  assignments from Owner's Manual Table 3.1 (INTMSK semantic
  is *inverted* from MZ-700 — the manual says "Masking of timer
  interrupt", so D2=1 masks). $E008 read-bit-0 gets a toggle
  hack for H-Blank so SA-1510's early polling loop at $02DB
  exits within one iteration.

- **Video.** `Hardware/Mz80aVideo.cs` renders monochrome 40×25
  from a 2 KiB single-bank font (SA-CG.rom). No attribute plane
  at `$D800` (that's the hardware-scroll buffer instead).
  Hardware scroll — VRAM window walks in 8-character units set
  by reads to `$E200-$E2FF` — and reverse-video mode
  (`$E014` / `$E015`) both wired through IoBus flags that
  `RunFrame` copies to the renderer before drawing. **SA-CG.rom
  stores glyphs MSB-first, opposite of MZ-700's `mz700fon.int`
  which is LSB-first.** First render came out horizontally
  mirrored; flipping the bit order in the decode was the fix.

- **Keyboard.** New `IKeyboardMatrix` interface — the minimum
  surface Ppi8255 needs — lets either MZ-700's rich `Keyboard`
  or MZ-80A's leaner `Mz80aKeyboard` feed row bits into Port B.
  `Mz80aKeyboard` uses Fig 3.6 (Owner's Manual p.167) to map
  a UK PC layout to MZ-80A slots for letters, digits, Enter,
  Space, Shift, Ctrl, and cursor keys. Staged key releases (a
  4-frame minimum hold after PC KeyDown) means single-frame PC
  KeyDown+KeyUp pairs stay visible to the ROM's scan across at
  least one full pass — same shape as MZ-700's staged shift
  bits but applied uniformly. Mapping-quality anomalies on
  punctuation keys are known and parked for the Post-v1
  "Phase 6.5" usability pass.

- **Cassette + BASIC.** `Hardware/Mz80aCassette.cs` implements
  the two read traps documented at SA-1510's jump table
  (`$0027` RDINF, `$002A` RDDAT — Owner's Manual §2.1.2,
  printed p.128). The header buffer at `$10F0-$1163` uses the
  **same** layout as MZ-700 (type / name / size / load addr /
  exec addr fields byte-identical), so `MzfImage` from the
  MZ-700 Cassette parser is reused directly. `DirectInject`
  writes the header + body straight to memory and jumps to the
  exec address — bypasses the L command for now, which needs
  the auto-typer path that MZ-80A doesn't have yet.
  `--mz80a --basic` autoloads SA-5510 to its `Ready` prompt;
  `--mz80a NEW-INVADERS-80A.mzf` boots straight into the game.

- **Sound.** MZ-80A's audio path is simpler than MZ-700's — no
  PC3 soft gate, no dual-NAND, just a single hard gate at
  `$E008 D0` in front of the audio amp with PIT counter 1's OUT
  as the tone source. Reuses `Hardware/Sound.cs` with
  `Enabled` pinned true and `InputClockHz = 31_500`. PIT
  counter 1 clocked at 15.72 kHz relative to the 2 MHz CPU
  (best-guess from Fig 3.1 block diagram; refine when a
  MUSIC-tempo reference is available).

- **Boot-ready detection.** MZ-80A has its own `MonitorReady`
  analogue — watches VRAM at `$D028` (row 1 col 0) for the
  SA-1510 boot cursor glyph, which the ROM writes once its
  main-loop cursor blink starts. Same shape as MZ-700's
  banner-text sniff, simpler byte match. Autoload fires the
  instant this trips, not on a fixed timeout, so `--mz80a`
  boot-to-BASIC-Ready feels as snappy as MZ-700's.

- **Diagnostic panes.** SoundDiag, FontSheet, HidDiag,
  KeyboardMatrix stay MZ-700-typed and show a friendly
  "MZ-700 only for now" MessageBox when opened while MZ-80A
  is active. DebuggerForm and MemoryViewerForm take
  `IMachine` so both work on either machine. About dialog
  now shows `Emulating: Sharp MZ-XXX` under the version so
  the current mode is glanceable.

The MZ-80A support shipped in six focused commits over the day
(Phase 0 → Phase 6). Total lines added: ~1500. Reuse from the
MZ-700 codebase was high — Z80Core, Ppi8255, Pit8253,
WinmmWaveOut, Sound, CassetteFile, MzfImage, most `UI/`, all
reused unchanged.

---

## Principles

These rules emerged from specific incidents and now shape the work.
Captured in the AI assistant's persistent memory so they're applied
automatically; documented here for human reference.

| Principle | When it applies | Origin |
|---|---|---|
| **Detect state, don't delay** | When tempted to write `if (frame == N)` or `Thread.Sleep(N)`. Ask what hardware-observable state you're actually waiting for. | 2026-05-03, banner-detection for monitor ready. Reapplied 2026-05-30 in auto-typer rewrite. |
| **Iterative on-device diagnostic** | Hardware-emulation bugs where the symptom is vague. Add a temporary status-bar / log diagnostic, run, observe, iterate. Faster than disassembling cold. | Throughout the project. BREAK key fix, mode-flag discovery, indexed INC/DEC narrowing — all this loop. |
| **Clean-room Z80 core** | Anything touching `Z80Core/`. Keep it pure net8.0 with no host-specific knowledge. Extend hooks rather than embed MZ-isms. | 2026-05-23, the extraction commit. |
| **Local working dirs `_*/`** | Any folder used for scratch / session / downloaded-reference material. Prefix with `_`. Auto-ignored. | 2026-06-01. |
| **Portable settings** | All user-facing config persists next to the executable. Never `%APPDATA%` / registry / per-user. "Survives reinstalls" need is met by Import/Export, not by moving live config. | 2026-06-04, during keyboard-editor planning. |
| **Self-documenting INI** | Every `settings.ini` section must explain its format inline. A user opening the file understands every line without reading code. | 2026-06-04, when adding `[CharMap]`. `[KeyOverrides]` was the bar; older sections retrofitted in the same commit. |
| **Canonical reference + startup validation** | Data encoded across multiple parallel files (keyboard matrix, sound subsystem). Build one reference + validators rather than chasing drifts one at a time. | 2026-06-13, after the MZ CTRL slot drift hid for weeks across four files. Re-applied 2026-06-19 for the sound topology. |

---

## Current status

The project shipped **v1.0.0** as `sgillon/MZRaku` on 2026-06-20 —
its first stable release, and the moment the project name moved from
the working-title `MZ700Emul` to its brand `MZRaku` (portmanteau of
MZ + Japanese 楽 *raku*, "easy / comfortable / relaxed"). The MZ-700
hardware model has been at "meets the original goals" since the trek
var-bug arc on 2026-05-23; everything since has been polish, expansion
and structural tidy-up: settings UI, layered keyboard model, diagram-
first keyboard editor, diagnostics surfaces, the canonical-reference
pattern (matrix + sound), debugger / window-geometry persistence,
full-screen + scanlines, and the speaker-NAND dual-gate audio fix.

Tagged releases:
- **v0.0.5-preview** (2026-05-16) — first public release.
- **v0.0.6-preview** (2026-05-23) — Z80 core extracted, NAudio
  dropped, single-file publish.
- **v0.0.7-preview** (2026-05-31) — tabbed Settings dialog, layered
  keyboard model, auto-typer rewrite, various key-mapping fixes.
- **v0.0.8-preview** (2026-06-14) — canonical matrix reference, MZ
  Ctrl reachability fix, F5 wired, AboutForm, diagram-first keyboard
  editor (Phase 2), `UI/` reorganisation.
- **v1.0.0** (2026-06-20) — first stable release; project renamed
  MZ700Emul → MZRaku; MIT license applied; speaker-NAND dual-gate
  sound fix; full-screen + scanlines; persisted window geometry +
  breakpoints; MZ-shift assertion race fix.
- **Z80Core v1.0.0** (2026-06-28, [sgillon/Z80Core](https://github.com/sgillon/Z80Core))
  — Z80 core split into its own repo, consumed back as a git
  submodule. Comprehensive `docs/`, MIT licensed, ZexHarness sample.

For the open backlog, see the `project_feature_backlog` memory.
Highlights queued for post-v1 polish: re-validating the empirical
`CyclesPerTempoToggle = 35469` against real hardware now that
discrete MUSIC notes make stopwatching meaningful; a proper
scanlines filter (with intensity / line-size controls) that holds
up at full-screen scale; bank-1 click-to-type completion in the
Font Sheet; auto-typer speed-up; L/R Ctrl distinction.

Stretch goals (not committed): cross-platform port (Avalonia +
Silk.NET evaluation), MZ-80K and MZ-80B support on the same
codebase, NuGet distribution for Z80Core, MZ-1P01 plotter emulation,
BASIC source editor + BASIC-aware debugger panes.
