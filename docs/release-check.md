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
      names `MZ800.ROM`, the three-file form and `[Roms.MZ800]`.
- [ ] With `MZ700IPL.ROM`, `FONT.ROM` and `MZ800IPL.ROM` (EmuZ-800's
      layout) in `roms\` instead, `--mz800` boots to the same IPL menu;
      `[Roms.MZ800]` gets `Monitor=`, `Font=` and `Ipl800=` and
      Settings → ROMs shows all three found. Remove `FONT.ROM` → the
      message names it and `Font=`. Put `MZ800.ROM` back → the Font
      and MZ-800 IPL rows read "(not used)". New in v1.5.0.

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
      shows the PSG pane). On MZ-80A it's greyed out, with a "MZ-700
      and MZ-800 only" tooltip. `File → Insert cassette for LOAD…`
      is enabled on all three machines (MZ-80A new in v1.5.0).
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

### Keyboard after Settings (fixed in v1.5.0)

- [ ] Open Settings with **Ctrl+Shift+K** (not the menu), change
      nothing, press OK. Type a few letters straight away, with no
      Ctrl+R: they appear as letters, not control codes. Until v1.5.0
      the shortcut left MZ CTRL held.

---

## Extended checks (~15 min)

### MZ-700 keyboard details

- [ ] `SHIFT+8` × 10 → `**********`.
- [ ] HID Diagnostic (Ctrl+H): press PC Ctrl on its own → resolves
      to layer=SpecialKey at slot (8, 6). (Regression canary for
      the CTRL slot correction 2026-06-12.)
- [ ] PC F1-F5 at the BASIC prompt type S-BASIC's defaults: `RUN`,
      `LIST`, `AUTO`, `RENUM`, `COLOR`; with Shift, `CHR$(`,
      `DEF KEY(`, `CONT`, `SAVE`, `LOAD`.
- [ ] Esc + Shift breaks a running monitor loop.
- [ ] At the 1Z-013A monitor prompt (no BASIC), shifted symbols
      (`!"#$%&'()`) type as themselves, and F11 (GRAPH) stays on for
      every key until F12. Same at the MZ-800's 9Z-504M (M at the IPL
      menu). New in v1.5.0: SHIFT typed GRAPH glyphs and GRAPH dropped
      after one key (B15).
- [ ] In S-BASIC, Shift+F12 ten times → shift lock turns on and off
      every time (letters type in capitals, then lower case), and the
      mode pane follows (`LOCK` / `ALPHA`). New in v1.5.0: one press
      in three was missed before, and the pane didn't show `LOCK`.

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

- [ ] All three machines, in BASIC (S-BASIC, SA-5510, 1Z-016): cells
      the keyboard can type are outlined green (normal/ALPHA) or blue
      (GRAPH). In each mode (ALPHA, GRAPH, and shift lock on the MZ-700
      and MZ-800), click a mix of green and blue cells, quickly → each
      glyph appears at the cursor in order, the status line says when
      a click switched mode ("…, back to ALPHA"), and the mode pane
      ends where it started; typing on the PC keyboard afterwards is
      in that mode. Outside BASIC (monitor prompt, IPL menu, a game):
      no outlines, and a click says where it types. Switching and the
      MZ-80A are new in v1.5.0; outside BASIC the MZ-700 and MZ-80A
      no longer type.
- [ ] MZ-700 and MZ-800: all 512 glyphs render; bank 1 has no
      outlines. Pressing GRAPH (F11) opens the sheet. MZ-800: a bank-1
      click says it can't be typed.
- [ ] MZ-700, in S-BASIC: click a bank-0 cell with no outline (e.g.
      $C7) → the line below the sheet shows `PRINT CHR$(96)`; a
      bank-0 cell like $C1, or any bank-1 cell → two `POKE`s (53248 /
      55296, attribute 113, or 241 for bank 1). Type → the statement
      appears at the cursor, without Enter; CR runs it and the glyph
      appears (PRINT: on the next line; POKE: top-left, white on blue).
      Repeat from GRAPH and from shift lock → it still types in
      capitals and the mode pane ends where it started. A green or
      blue click clears the line. New in v1.5.0.
- [ ] MZ-80A: two sections labelled Text ($00-$7F) and Graphics
      ($80-$FF) render, outlined in both.

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
- [ ] System → Sound on the MZ-700 and MZ-800, with `MUSIC` playing:
      Mute (Ctrl+U) silences at once and shows the MUTED chip; pause
      and resume keep it muted; Ctrl+U again restores sound. The
      volume slider changes the level as it moves (status bar shows
      it); 0 is silent. Both survive Reset and a restart (`[Sound]`
      in `settings.ini`). New in v1.5.0.

### Speed (new in v1.5.0)

- [ ] Each machine: System → Speed → 2×, 3×, 4× → the machine runs
      that much faster (a game, or BASIC's `MUSIC` tempo, shows it),
      the status bar shows `N× NO SOUND`, sound stops at once and the
      menu ticks the speed. 1× → real speed, the pane goes, sound
      returns.
- [ ] Mutes stay separate: Ctrl+U at 2×, then 1× → still muted
      (MUTED chip); pause and resume at 3× → still silent until 1×.
- [ ] At 4×, typing at BASIC's prompt (and a Font Sheet click) still
      lands every character; the display stays smooth.
- [ ] `--speed=3` starts at 3×; a launch without it starts at 1×;
      `settings.ini` has no speed entry. `--speed=9` stops with a
      message.

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
- [ ] View → Save screenshot (Ctrl+P) on each machine: the status
      bar names `screenshots\<machine>-<date>-<time>.png`, a PNG of
      the bare frame at native size (no scanlines). Ctrl+P works
      while paused (no pause wash in the file) and in full screen;
      MZ-800 640-wide mode saves 640×200. New in v1.5.0.
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
- [ ] MZ-800: the same with 1Z-016 BASIC (`SAVE "TEST"` shows
      `Writing "TEST"`, no RECORD.PLAY wait, then `Ready`; the file
      lands in `saves\` and the TAPE chip flashes). New in v1.5.0.
- [ ] MZ-80A: the same with SA-5510 BASIC (`SAVE "TEST"` shows
      `Writing TEST`, no RECORD.PLAY wait, then `Ready`; the file
      lands in `saves\` and the TAPE chip flashes). New in v1.5.0.
- [ ] `MZRaku.exe --mz80a cricket.mzf` — BASIC boots, the program
      goes in without `LOAD` being typed, `RUN` is typed, and it
      reaches its first screen without operator intervention.
      Repeat with `COLDITZ-80A.mzf` (the `R` of `RUN` must arrive).
- [ ] Drag-drop `cricket.mzf` onto a running `--mz80a` window →
      same autorun flow (drop-handler reset canary).
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
- [ ] MZ-80A: File → Insert cassette for LOAD… at the SA-1510
      prompt, then `L` → a machine-code game loads and runs; at
      SA-5510's `Ready`, `LOAD` → `Found` / `Loading`, then `RUN`
      works. The status bar says "Loaded via tape trap". New in
      v1.5.0.
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
- [ ] MZ-700: keyboard-mode pane shows `—` until S-BASIC runs, then
      `ALPHA` / `LOCK` (Shift+F12) / `GRAPH`. `LOCK` is new in v1.5.0.
- [ ] MZ-80A: keyboard-mode pane reads SA-1510's GRAPH flag (new in
      v1.5.0; it counted F11 presses before). At the monitor and in
      SA-5510 it follows F11 and stays right after a reset or a BASIC
      load; while a program has SA-1510 swapped out ($E00C) it shows
      `—`. The HID Diagnostic's Mode line agrees with it.
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
- [ ] `--settings=<ini>` with a copy of `settings.ini` (green screen
      off for `--mz80a` makes it visible): the title bar names the
      file and says read-only; Settings → OK says the change is for
      this run only; a machine switch keeps the flag. After closing,
      neither that file nor `settings.ini` has changed. A missing
      file stops with a message. New in v1.5.0.

### Known backlog items

Not blockers — surface in the release notes so what's still open
stays honest.

- **MZ-800 no floppy / Quick Disk / printer**; border not drawn.
- **Font Sheet click-to-type** types in BASIC only, on all three
  machines (by design: graphics are typed at BASIC's prompt).
- **MZ-800 auto-typer** types nothing at the IPL menu or in the
  9Z-504M monitor (real keys and `--keys` scripts work there), so
  Load BASIC source and Font Sheet clicks don't reach them. Found in
  v1.5 step 4.8; not yet looked into.
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
      `MZRaku-<version>-dotnet10.zip` (assumes .NET 10 Desktop
      Runtime on target; it was `-dotnet8.zip` up to v1.5.0).
- [ ] Self-contained zip built:
      `MZRaku-<version>-standalone.zip` (no runtime required).
- [ ] Both zips extract cleanly to an empty folder and run.
- [ ] Tag created, pushed, release notes drafted via
      `gh release create`.
