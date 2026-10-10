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
      `SA-CG.rom`, `SA-5510.mzf`, `MZ800.ROM` or `1Z-016.mzf`
      (Sharp copyright — must not redistribute).
- [ ] Exe launches from a clean folder (no `settings.ini`) and
      auto-detects all three machines' ROMs from `roms/` and
      `basic/` — `[Roms.MZ800]` gets `MZ800.ROM` and `1Z-016.mzf`
      even while MZ-700 is the active machine. No "Matrix
      validation drift" MessageBox at startup (now also covers the
      MZ-800 layout).
- [ ] Window title bar reads `MZRaku`.

### Boot on all three machines

- [ ] `MZRaku.exe` (no flag) → MZ-700 boots to blue Sharp `Ready`.
- [ ] `MZRaku.exe --mz80a` → MZ-80A boots to `** MONITOR SA-1510 **`,
      then `BASIC interpreter SA-5510` / `32492 Bytes` / `Ready`.
- [ ] `MZRaku.exe --mz800` → MZ-800 boots to the IPL boot menu
      (`Make ready CMT` / `Please push key` / C and M options). IPL
      boot beep plays once and stops.
- [ ] `MZRaku.exe --mz800 --basic` → 1Z-016 banner and `Ready`; no
      `Dev. name error`.
- [ ] Remove `MZ800.ROM` and run `--mz800` → missing-ROM message
      names `MZ800.ROM` and `[Roms.MZ800]`. Put it back.

### Core keyboard (all three machines)

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
- [ ] `System → Machine` lists MZ-700 / MZ-80A / MZ-800; picking
      another prompts to restart, restart works.
- [ ] Settings → Startup with Default machine = MZ-800 → Apply →
      reopen: still MZ-800 (`[Machine] DefaultMachine=MZ800`, not
      rewritten to MZ700).
- [ ] `settings.ini` after first run contains expected sections:
      `[Machine]`, `[Display]`, `[Display.MZ80A]`,
      `[Hardware.MZ800]`, `[Roms.MZ700]`, `[Roms.MZ80A]`,
      `[Roms.MZ800]`, `[Joystick]`, `[Keyboard.MZ80A]`,
      `[KeyOverrides.MZ700]`, `[KeyOverrides.MZ80A]`,
      `[KeyOverrides.MZ800]`, `[CharMap]`, `[CharMap.MZ80A]`,
      `[CharMap.MZ800]`, `[DebugPanes]`, `[MainWindow]`,
      `[DebuggerWindow]`, `[MemoryViewerWindow]`,
      `[DebuggerBreakpoints]`. Every section has its explanatory
      comment block.

### Diagnostic surfaces (all three machines unless noted)

- [ ] `Debug → Debugger…` (Ctrl+D), `Memory Viewer…` (Ctrl+M),
      `HID Diagnostic…` (Ctrl+H), `Keyboard Matrix…` all open
      without NRE.
- [ ] `View → Font Sheet…` (Ctrl+G) opens.
- [ ] `Debug → Sound Diagnostic…` opens on MZ-700 and MZ-800 (MZ-800
      shows the PSG pane). On MZ-80A it shows the friendly "currently
      available on the MZ-700 and MZ-800 only" MessageBox — no NRE.
- [ ] MZ-800 Memory Viewer: bank line shows bank state and display
      mode. At the IPL menu, $E000-$E00F reads as memory (not hidden
      as I/O).
- [ ] `Help → About…` opens; version matches `<Version>` in the
      csproj; logo visible in header; `Emulating: Sharp MZ-XXX`
      line names the active machine.

### Z80 regression canary

- [ ] All three machines at BASIC `Ready`: `PRINT 1.5` outputs
      `1.5` (Z80 indexed INC/DEC — regression canary from
      2026-05-23).

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

### MZ-800 keyboard

At the 1Z-016 `Ready` prompt:

- [ ] Letters, digits and main-row punctuation echo as typed,
      shifted and unshifted.
- [ ] Tab, Insert, Delete/Backspace, cursor keys, F1-F5 behave as
      the MZ-800 keys of the same name.
- [ ] F11 → GRAPH (Font Sheet opens), F12 → ALPHA, Shift+F12 →
      shift lock; the keyboard-mode pane follows each one.
- [ ] Shift+Esc breaks a running BASIC program.

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
- [ ] MZ-800: Settings → Keyboard shows the MZ-800 diagram (TAB on
      the QWERTY row, ALPHA beside a narrower left SHIFT, F1-F5 along
      the top), not the MZ-700 one. Click a cap → editor opens;
      rebind + Save + OK → override persists to `[CharMap.MZ800]` /
      `[KeyOverrides.MZ800]` and takes effect.
- [ ] MZ-800: Export → `.mzkbd` with `[Meta] machine=MZ-800`;
      Import Merge of the same file → no change. Import an MZ-700
      `.mzkbd` → refused with "Machine mismatch".
- [ ] MZ-800: Apply summary lists the MZ-800 binding changes.
- [ ] All three machines: overrides survive close-and-relaunch.

### Font Sheet

- [ ] MZ-700: all 512 glyphs render. Cells the keyboard can produce
      outlined green in both banks. In ALPHA mode, click a bank-0
      cell → status bar reports the typed code and the glyph
      appears at the cursor. Click a bank-1 cell → status bar shows
      known-limitation message; nothing types.
- [ ] MZ-80A: view-only. Two sections labelled Text ($00-$7F) and
      Graphics ($80-$FF) render. Click any cell → status bar shows
      `{section} code $XX`; nothing types (documented view-only).
- [ ] MZ-800: header reads "MZ-800 character ROM … View-only for
      now"; all glyphs render; clicks don't type. Pressing GRAPH (F11)
      in 1Z-016 opens it.

### Sound

- [ ] All three machines: silence at monitor prompt / IPL menu (no
      sustained tone).
- [ ] MZ-800 `MUSIC "CDEFGAB"` in 1Z-016 — seven discrete notes; no
      crackle or split notes. Sound Diagnostic's PSG pane shows tone
      periods and attenuation changing.
- [ ] MZ-800 game with PSG music (e.g. Manic Miner title tune) plays
      at the right speed and pitch.
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
- [ ] MZ-800: Uridium in-game playfield shows in full colour with
      MZ-1R25 fitted. Settings → Display → MZ-800 → untick MZ-1R25 →
      Apply → planes III/IV vanish live; tick again → restored.
      Persists to `[Hardware.MZ800] MZ1R25=`.
- [ ] MZ-800 BASIC: a program printing more than 25 lines scrolls
      cleanly; `LIST` then `RUN` again shows no lost lines or banner
      wrapping in at the bottom (hardware-scroll canary from 8.5).

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
- [ ] `MZRaku.exe --mz800 <MZ-800 machine-code game>` waits for the
      IPL menu, then starts the game (e.g. Bruce Lee, Jetpac).
- [ ] MZ-800: Load cassette of an MZ-800 BASIC program (type 05)
      → BASIC loads, then `LOAD` / `RUN` are typed and the program
      runs. A BASIC loader that loads a second file by name (e.g.
      EGG.BAS → EGG.OBJ) finds it.
- [ ] MZ-800: Load cassette of an MZ-700 S-BASIC program → status
      bar says to run it on the MZ-700.
- [ ] MZ-800: Load cassette over a running MZ-800 game → new title
      starts cleanly (no carried-over palette or display mode).
- [ ] MZ-800: File → Insert cassette for LOAD… at the 1Z-016
      prompt, then `LOAD` → program loads from the inserted tape.
- [ ] Load BASIC source… (Ctrl+Shift+B) on each machine, both from
      BASIC `Ready` and from a cold monitor / IPL menu → listing typed
      with no dropped characters; `RUN` works.

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
- [ ] MZ-800: in a joystick game (e.g. Bruce Lee), stick directions
      and both triggers respond; HID Diagnostic shows the IN $F0 /
      $F1 port values changing.
- [ ] With no gamepad connected, nothing is held on either machine
      (no stuck "up").

### Status bar

- [ ] All three machines: left pane shows machine name (`MZ-700` /
      `MZ-80A` / `MZ-800`); centre pane displays transient status
      messages (auto-clear ~5 s). MZ-700 / MZ-80A: right pane shows
      `ALPHA` at boot.
- [ ] F11 toggles right pane `ALPHA ↔ GRAPH`. MZ-700 also toggles
      via F12; MZ-80A is F11-only.
- [ ] MZ-800: display-mode pane shows `320×200` in 1Z-016 BASIC and
      `MZ-700` in an MZ-700 title (e.g. James), with a tooltip. Keyboard-mode pane shows `—` at the IPL menu and in
      games, `ALPHA` / `LOCK` / `GRAPH` in 1Z-016. After a game
      loaded over BASIC it goes back to `—`.
- [ ] TAPE chip greys / pales / flashes per cassette state.

### Startup preferences

- [ ] Settings → Startup: DefaultMachine radios (three) persist to
      `[Machine] DefaultMachine=`. Six DebugPanes checkboxes persist
      to `[DebugPanes]`. Sound Diagnostic greys out when
      DefaultMachine=MZ-80A only; stored value survives the disable.
- [ ] Settings → ROMs shows an MZ-800 group (`MZ800.ROM`,
      `1Z-016.mzf`); browsing a new path persists to `[Roms.MZ800]`.

### Known backlog items

Not blockers — surface in the release notes so what's still open
stays honest.

- **Apply-keyboard regression**
  ([[project-v1-1-apply-keyboard-regression]]): documented
  workaround verified in Critical smoke.
- **MZ-800 BASIC SAVE** waits for a tape forever (no tape-write
  trap yet). Target v1.4.0.
- **MZ-800 no floppy / Quick Disk / printer**; border not drawn.
- **Font Sheet click-to-type** is view-only on MZ-80A and MZ-800;
  MZ-700 bank-1 still parked. MZ-800 click-to-type targeted at
  v1.4.0.
- **Upgrading from v1.0.x straight to v1.3+** loses custom
  `[KeyOverrides]` and explicit `[Roms]` paths — run 1.1 (or later)
  once first to migrate.

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
