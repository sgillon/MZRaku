# Project history

A chronological record of how MZ700Emul evolved, written for a future
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
- `MZ700Emul.csproj` switched to a conditional glob so local
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
~232 KB; the self-contained release is ~63 MB. Dev's local Sharp
ROMs / BASIC never leak into a publish. *Don't relax the
`CopyToPublishDirectory=Never` without an alternative copyright
guard.*

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

---

## Current status

The project has been at "meets the original goals" since the trek
var-bug arc on 2026-05-23. Everything since has been polish and
expansion: settings UI, layered keyboard model, diagnostics surfaces,
the in-flight keyboard editor.

Tagged releases:
- **v0.0.5-preview** (2026-05-16) — first public release.
- **v0.0.6-preview** (2026-05-23) — Z80 core extracted, NAudio
  dropped, single-file publish.
- **v0.0.7-preview** (2026-05-31) — tabbed Settings dialog, layered
  keyboard model, auto-typer rewrite, various key-mapping fixes.

Next release (v0.0.8) will likely cut once Phase A of the
keyboard-editor work is complete and Phase B has landed at least
in some form.

For the open backlog, see the `project_feature_backlog` memory.
Highlights: WndProc L/R disambiguation (fixes MZ Ctrl bug),
BASIC-aware debugger panes, `Z80Core.Tests/` automated test
project, Z80Core spin-out to its own repo.

Stretch goals (not committed): cross-platform port (Avalonia + Silk.NET
evaluation), MZ-80K and MZ-80B support on the same codebase,
full-screen mode, scanlines overlay, MZ-1P01 plotter emulation, a
broader 8-bit CPU library family.
