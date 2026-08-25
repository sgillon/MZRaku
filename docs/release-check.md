# Release readiness check

A manual smoke test to run before tagging a release. Two tiers:

- **Critical** (~5 min) — must all pass before tagging. Catches
  show-stoppers.
- **Extended** (~15 min) — regression canaries and edge cases.
  Recommended for major releases; skip under time pressure.

Release packaging steps at the end.

If something here drifts stale or a new escape gets through into a
release, update the checklist before fixing the bug.

---

## Critical smoke (~5 min)

### Build

- [ ] `dotnet publish` (Release, single-file) — 0 warnings, 0 errors.
      Output: `MZRaku.exe`.
- [ ] Publish output contains **no Sharp firmware** — no
      `1z-013a.rom`, `mz700fon.int`, `1Z-013B.mzf`, `SA-1510.rom`,
      `SA-CG.rom`, or `SA-5510.mzf` (Sharp copyright — must not
      redistribute).
- [ ] Exe launches from a clean folder (no `settings.ini`) and
      auto-detects both machines' ROMs from `roms/`. No "Matrix
      validation drift" MessageBox at startup.
- [ ] Window title bar reads `MZRaku`.

### Boot on both machines

- [ ] `MZRaku.exe` (no flag) → MZ-700 boots to blue Sharp `Ready`.
- [ ] `MZRaku.exe --mz80a` → MZ-80A boots to `** MONITOR SA-1510 **`,
      then `BASIC interpreter SA-5510` / `32492 Bytes` / `Ready`.

### Core keyboard (both machines)

- [ ] Letters A-Z echo unshifted.
- [ ] `SHIFT+P` × 20 → 20 × `P` (shift-race regression canary).
- [ ] Enter, cursor keys, Backspace all work.
- [ ] Ctrl+R (System → Reset) returns to a clean prompt with no
      stuck CTRL on the matrix.

### Menus + settings

- [ ] Menu bar order: `File / System / View / Debug / Help`.
- [ ] `System → Settings → Startup…` (Ctrl+S) opens on Startup tab.
      Ctrl+Shift+{R,D,K,J} each deep-link to ROMs / Display /
      Keyboard / Joystick.
- [ ] Tab order: Startup / ROMs / Display / Keyboard / Joystick.
      All five tabs render.
- [ ] `System → Machine → other` prompts to restart, restart works.
- [ ] `settings.ini` after first run contains expected sections:
      `[Startup]`, `[Display]`, `[Display.MZ80A]`, `[Roms.MZ700]`,
      `[Roms.MZ80A]`, `[Joystick]`, `[Keyboard.MZ80A]`,
      `[KeyOverrides.MZ700]`, `[KeyOverrides.MZ80A]`, `[CharMap]`,
      `[CharMap.MZ80A]`, `[DebugPanes]`, `[Window]`, `[Debugger]`,
      `[MemoryViewer]`, `[Breakpoints]`, `[Machine]`.

### Diagnostic surfaces (both machines unless noted)

- [ ] `Debug → Debugger…` (Ctrl+D), `Memory Viewer…` (Ctrl+M),
      `HID Diagnostic…` (Ctrl+H) all open without NRE.
- [ ] `View → Font Sheet…` (Ctrl+G) opens.
- [ ] On MZ-80A: `Debug → Sound Diagnostic…` shows friendly
      "MZ-700 only for now" MessageBox — no NRE.
- [ ] On MZ-80A: `Debug → Keyboard Matrix…` opens the shared
      matrix grid against MZ-80A data — no MZ-700-only refusal
      (v1.2 F-034 unlocked the pane).
- [ ] `Help → About…` opens; version matches `<Version>` in the
      csproj; logo visible in header; `Emulating: Sharp MZ-XXX`
      line names the active machine.

### Z80 regression canary

- [ ] Both machines at `Ready`: `PRINT 1.5` outputs `1.5` (Z80
      indexed INC/DEC — regression canary from 2026-05-23).

### Known workaround verification

- [ ] Apply-keyboard regression workaround
      ([[project-v1-1-apply-keyboard-regression]]): Settings →
      Keyboard → Advanced settings → click a matrix cell → capture
      any PC key → Save → close Advanced → OK on Settings. Confirm
      no keys type on the machine. Press Ctrl+R. Confirm typing
      resumes and the remap is in effect.

---

## Extended checks (~15 min)

### MZ-700 keyboard details

- [ ] `SHIFT+8` × 10 → `**********`.
- [ ] HID Diagnostic (Ctrl+H): press PC Ctrl on its own → resolves
      to layer=SpecialKey at slot (8, 6). (Regression canary for
      the CTRL slot correction 2026-06-12.)
- [ ] PC F5 at BASIC prompt types `CHR$(` (default S-BASIC F5 macro).
- [ ] Esc + Shift breaks a running monitor loop.

### MZ-80A keyboard tier audit

Regression check for the v1.1 Phase 4 audit (2026-07-30). Type
into the SA-5510 `Ready` prompt; each tier should stay 100% clean.

- [ ] Tier 1a — unshifted main-row punctuation `,./;:@[]-\^`
      all echo identically.
- [ ] Tier 1b — shifted main-row punctuation `` <>+*{}=~|` ``
      all echo identically.
- [ ] Tier 1c — `£` (UK Shift+3) → MZ `#` (deliberate fallback,
      MZ-80A has no £); `#` and `?` echo identically.
- [ ] Tier 2a — unshifted digits `0123456789` all echo.
- [ ] Tier 2b — shifted digits `!"$%&()_` all echo.
- [ ] Tier 3 — with default `InvertLetterShift = false`:
      `zsgjm` → MZ `ZSGJM` (unshifted = UPPERCASE);
      `ZSGJM` with Shift → MZ `zsgjm` (shifted = lowercase).
- [ ] Tier 4 — cursor keys (up/down/left/right — down/left via
      force-shift on up/right). Enter, Delete, Backspace, Insert,
      Home. F11 toggles GRPH; Shift+Esc = BREAK.
- [ ] Tier 5 — GRAPH mode: F11 → letters/digits/punct produce
      graphic glyphs. F11 again → ALPHA restores.

### Keyboard editor

- [ ] MZ-700: Settings → Keyboard shows MZ-700 diagram with
      PC-binding badges on caps. Red outline on unreachable-
      essential keys — on clean INI, exactly POUND (0,5) and the
      AT (1,5) shifted-glyph (backtick, deliberately parked).
- [ ] MZ-700: click a cap → editor opens with correct slot. Rebind
      + Save → badge updates. Reset → default restores.
- [ ] MZ-700: click SHIFT cap → "MZ Shift is permanently bound"
      MessageBox.
- [ ] MZ-700: Advanced settings child window shows matrix grid +
      Unbound slots panel + overrides list. Unbound list on clean
      INI: exactly POUND (0,5).
- [ ] MZ-700: Export → `.mzkbd` file. Import Merge with the same
      file → no change.
- [ ] MZ-80A: Settings → Keyboard shows MZ-80A diagram — numeric
      keypad on right, dual-label BREAK/CTRL + INST/DEL + CLR/HOME
      caps, `InvertLetterShift` group visible.
- [ ] MZ-80A: click any diagram cap → editor opens. Rebind + Save
      → override persists to `[CharMap.MZ80A]`.
- [ ] MZ-80A: click either SHIFT cap → same "MZ Shift is permanently
      bound" MessageBox as MZ-700.
- [ ] MZ-80A: Export → `.mzkbd` file (v2 format with `[Meta]
      machine=MZ-80A`); Import Merge with the same file → no
      change. Import a MZ-700 `.mzkbd` on MZ-80A → refused with
      a "machine mismatch" warning (v1.2 F-037).
- [ ] Both machines: overrides survive close-and-relaunch.

### Font Sheet

- [ ] MZ-700: all 512 glyphs render. Cells the keyboard can produce
      outlined green in both banks. In ALPHA mode, click a bank-0
      cell → status bar reports the typed code and the glyph
      appears at the cursor. Click a bank-1 cell → status bar shows
      known-limitation message; nothing types.
- [ ] MZ-80A: view-only. Two sections labelled Text ($00-$7F) and
      Graphics ($80-$FF) render. Click any cell → status bar shows
      `{section} code $XX`; nothing types (documented view-only).

### Sound

- [ ] Both machines: silence at monitor prompt (no sustained tone).
- [ ] MZ-700 `MUSIC "CDEFGAB"` in BASIC — seven discrete notes.
- [ ] MZ-80A `MUSIC "CDEFGAB"` — seven discrete notes at recognisable
      pitches and durations (matches EmuZ-80A within measurement
      precision). Regression canary for `de08e40`, `11f4a04`,
      `7e4cc46`.
- [ ] Debug → Sound Diagnostic (MZ-700) with MUSIC running: event
      log shows interleaved `C0 <- $XX` reload writes,
      `$E008 ← $01`/`$00` hard-gate toggles, PC3 soft-gate
      transitions. State pane's Audible line updates live.

### Display

- [ ] View → Full-screen (Alt+Enter) toggles borderless full-screen
      on the same monitor. Toggling again restores previous
      windowed size/position.
- [ ] `--display=full` on the command line launches directly into
      full-screen for that run only; `settings.ini` unchanged.
- [ ] View → Scanlines (Ctrl+L) toggles CRT overlay. Menu checkmark
      tracks state; persists across restart.
- [ ] `--scanlines=on`/`off` overrides persisted setting for that
      run only.
- [ ] Main window geometry (size + position) restored on relaunch.
- [ ] MZ-80A: Settings → Display → MZ-80A → "Green screen (P1
      phosphor)" toggles the monochrome renderer between white and
      pure `#00FF00`. Live-applies on Apply (no restart); persists
      to `[Display.MZ80A] GreenScreen=`.

### Cassette

- [ ] MZ-700: save a short BASIC program to a new `.mzf`, restart,
      load it back, RUN succeeds (round-trip).
- [ ] `MZRaku.exe --mz80a cricket.mzf` — autoloads via typed LOAD,
      waits for tape read, types RUN. Reaches title / first
      playable state without operator intervention.
- [ ] Drag-drop `cricket.mzf` onto a running `--mz80a` window →
      same typed-LOAD + auto-RUN flow (drop-handler reset canary).
- [ ] `MZRaku.exe --mz80a NEW-INVADERS-80A.mzf` boots the game
      (DirectInject, machine-code). Title screen with SCORE line
      and invader grid visible.

### Debugger

- [ ] Both machines: set breakpoint at a known address → run pauses
      there. Step (F10/F11) advances PC one instruction.
- [ ] Memory viewer Snap → press a few keys → Diff shows changed
      bytes.
- [ ] Debugger and Memory Viewer window geometry survives
      close-and-reopen and across restart.
- [ ] Breakpoint list persists across a relaunch.

### Joystick

- [ ] Settings → Joystick shows connected pad + current SW1/SW2
      bindings.
- [ ] Rebind SW1 by clicking Left button (SW1) then pressing a pad
      button → persists across restart.
- [ ] In a joystick-aware game, both stick slots respond.

### Status bar

- [ ] Both machines: left pane shows machine name (`MZ-700` /
      `MZ-80A`); centre pane displays transient status messages
      (auto-clear ~5 s); right pane shows `ALPHA` at boot.
- [ ] F11 toggles right pane `ALPHA ↔ GRAPH`. MZ-700 also toggles
      via F12; MZ-80A is F11-only.
- [ ] TAPE chip greys / pales / flashes per cassette state.

### Startup preferences

- [ ] Settings → Startup: DefaultMachine radio pair persists to
      `[Machine] DefaultMachine=`. Six DebugPanes checkboxes persist
      to `[DebugPanes]`. MZ-700-only panes (Sound Diagnostic,
      Keyboard Matrix) grey out when DefaultMachine=MZ-80A; stored
      values survive the disable.

### Known backlog items

Not blockers — surface in the release notes so what's still open
stays honest.

- **Apply-keyboard regression**
  ([[project-v1-1-apply-keyboard-regression]]): documented
  workaround verified in Critical smoke.
- **BASIC cold-start Overflow**
  ([[project-basic-cold-start-overflow]]): some .mzf BASIC
  programs (reproducer: Dragon Caves 1982) show screen
  corruption + Overflow Error on cold-start LOAD+RUN. Clears
  after any prior MC run in the session. Target v1.3.0.

---

## Release packaging

- [ ] Version bumped in `MZRaku.csproj` (`<Version>`). Bare semver
      for stable (`1.1.0`), `-preview.N` suffix for preview.
- [ ] About dialog shows the bumped version (sanity check — reads
      `AssemblyInformationalVersion` at runtime).
- [ ] README planned-work / known-limitations sections reflect
      what actually shipped.
- [ ] Framework-dependent zip built:
      `MZRaku-<version>-dotnet8.zip` (assumes .NET 8 Desktop
      Runtime on target).
- [ ] Self-contained zip built:
      `MZRaku-<version>-standalone.zip` (no runtime required).
- [ ] Both zips extract cleanly to an empty folder and run.
- [ ] Tag created, pushed, release notes drafted via
      `gh release create`.
