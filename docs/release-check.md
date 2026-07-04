# Release readiness check

A short manual smoke test to run before tagging a release. Aim
for ~10-15 minutes end-to-end. The point is to catch behaviour that
compiles fine but doesn't *work* — things automated tests don't see.

If something here drifts out of date or a new escape gets through into
a release, update the checklist before fixing the bug.

## Build

- [ ] `dotnet publish` (Release, single-file) completes with no warnings.
      Output filename is `MZRaku.exe`.
- [ ] Publish output does **not** contain any Sharp firmware —
      neither MZ-700's `1z-013a.rom` / `mz700fon.int` / `1Z-013B.mzf`
      nor MZ-80A's `SA-1510.rom` / `SA-CG.rom` / `SA-5510.mzf`
      (Sharp copyright — must not be redistributed).
- [ ] Exe runs on a clean folder (no `settings.ini`) and auto-detects
      the three system files from `roms/` next to it.
- [ ] Window title bar reads "MZRaku" (not "Sharp MZ-700 Emulator" — that
      was the pre-v1 wording).
- [ ] Startup matrix validation stays silent — no "Matrix validation
      drift" MessageBox. (Means `Mz700MatrixReference`, `SpecialKeyMap`,
      `CharMap`, and `MzKeyboardLayout` all agree.)

## Keyboard — Monitor prompt

- [ ] Letters A-Z type correctly (no Shift).
- [ ] Shift + letter gives uppercase reliably — type `SHIFT+P` x10,
      expect `PPPPPPPPPP` not `PPpPPPpPPP`. (Known shift-race; called
      out in the Keyboard tab's known-limitations panel and
      `docs/usage/keyboard.md`. Cosmetic — don't block on it but do
      regression-check that the rate hasn't got worse.)
- [ ] Shift + number gives the symbol reliably (`SHIFT+8` x10 → `**********`).
- [ ] Cursor keys move the cursor.
- [ ] Backspace deletes; Insert inserts a space.
- [ ] Enter executes the line.
- [ ] Esc + Shift breaks a running monitor loop.
- [ ] **File → Reset** and **Ctrl+R** both leave the monitor at a clean
      prompt with no stuck CTRL on the matrix. (Regression fix
      `d2f3493` — before that, Ctrl+R left MZ CTRL asserted at (8, 6)
      until you released PC Ctrl, because Reset didn't release the
      matrix bits the host keydown had asserted.)

## Keyboard — BASIC

- [ ] `LOAD` 1Z-013B.mzf (or auto-load), BASIC banner appears.
- [ ] F11 toggles into GRAPH mode — status bar shows `GRAPH` on
      magenta; cursor changes.
- [ ] F12 returns to ALPHA — status bar shows `ALPHA`.
- [ ] Typing letters in GRAPH mode produces graphic chars.
- [ ] Status bar shows `—` (grey) when the emulator first starts,
      before BASIC is loaded.
- [ ] MZ Ctrl via PC Ctrl works (fixed 2026-06-12 — VK_CONTROL not
      VK_LCONTROL, and slot moved from (9, 2) to (8, 6) per the
      Owner's Manual). Verify in **Debug → HID Diagnostic** (Ctrl+H):
      press PC Ctrl on its own, expect layer=SpecialKey at slot
      (8, 6). Don't try Ctrl+letter combinations as a smoke test —
      most are intercepted by Windows / WinForms shortcuts before
      the MZ keyboard sees them.
- [ ] F5 via PC F5 works (wired up 2026-06-12). In BASIC, PC F5
      types `CHR$(` (the default S-BASIC F5 macro).

## Keyboard editor

- [ ] Settings → Keyboard tab: diagram of the MZ-700 keyboard is
      visible, each cap showing its current PC binding as a blue
      badge.
- [ ] Click a key cap → per-key editor opens with the right slot(s).
- [ ] Edit → capture a different PC key → Save → diagram redraws
      with the new badge.
- [ ] Reset on the same slot restores the built-in default badge.
- [ ] **Safety gate**: unbind PC `1` so MZ `1` becomes unreachable →
      the MZ `1` cap is outlined in crimson; clicking Apply lists it
      and prompts before saving.
- [ ] Click the **SHIFT** cap → explanatory message appears (SHIFT
      is wired via the modifier path, not slot-bound).
- [ ] On a clean `settings.ini`, the diagram's per-glyph safety check
      shows exactly two crimson-outlined keys: the POUND/↓ cap at
      `(0,5)` (neither glyph reachable) and the AT/' cap at `(1,5)`
      (the shifted reversed-apostrophe glyph at bank 0 $A4 is
      deliberately without a PC binding — see the slot comment in
      `Hardware/Mz700MatrixReference.cs`).
- [ ] Keyboard tab's **Known limitations** group box at the bottom
      lists the three parked items (bank-1 click-to-type, MZ-shift
      race on rapid input, no L/R Ctrl distinction); the
      `docs/usage/keyboard.md` link opens in the browser.
- [ ] **Advanced settings…** button opens the resizable child window
      with the live matrix grid (top), the **Unbound slots** panel
      (middle), and the overrides list (below).
- [ ] Unbound-slot panel on a clean `settings.ini` lists exactly one
      entry: POUND `(0,5)`. (The AT slot doesn't appear because `@`
      already binds the slot at the unshifted glyph; the panel works
      at slot level, not per-shift-state. POUND disappears the moment
      any override targets that slot.)
- [ ] **Export…** writes a `.mzkbd` file containing only the user's
      overrides (open in a text editor to verify the two sections).
- [ ] **Import…** offers Merge / Replace; importing the file you
      just exported produces no change.
- [ ] OK / Apply persists; quit and relaunch — overrides survive.

## Font Sheet

- [ ] **View → Font Sheet…** (Ctrl+G) opens; all 512 glyphs render.
- [ ] Cells reachable from the keyboard are outlined in green
      (both banks).
- [ ] In ALPHA mode, click a bank-0 (top) cell → status bar reports
      the typed code and the glyph appears at the cursor.
- [ ] Click a bank-1 (bottom) cell → status bar shows the
      known-limitation message; nothing types. (Documented in
      docs/usage/keyboard.md; do not silently regress to mistyping.)

## HID Diagnostic

- [ ] **Debug → HID Diagnostic…** (Ctrl+H) opens.
- [ ] Pressing a PC key updates `LastKeyDown` / `LastKeyChar`; mode
      shown matches the layer that resolved (Override / SpecialKey /
      CharMap).
- [ ] Joystick axes / buttons update live when the controller is
      moved.

## BASIC programs

- [ ] `PRINT 1.5` outputs `1.5` (Z80 indexed INC/DEC regression
      canary — fixed 2026-05-23).
- [ ] `10 FOR I=1 TO 5: PRINT I: NEXT` then `RUN` outputs 1..5.
- [ ] Load `trek.mzf` from cassette; SR command produces a sensor
      readout without "var parse" errors.

## Sound

- [ ] Boot tone: silence at the monitor prompt (real hardware doesn't
      sustain a tone here; the ROM opens then closes the speaker NAND
      within one frame — see [[project-v1-plan]] / `Mz700SoundReference`).
- [ ] BASIC `MUSIC "CDEFGAB"` plays seven discrete notes — not one
      continuous re-pitched tone. (Regression canary for the $E008 D0
      hard-gate latch fix in `66d83b0`.)
- [ ] In a game with sound effects (Space Panic, Star Trek): audible
      events fire when expected. Game-specific noises that were missing
      pre-`66d83b0` should now play.
- [ ] **Debug → Sound Diagnostic…** opens. With BASIC `MUSIC` running,
      the event log shows interleaved `C0 <- $XX` reload writes,
      `$E008 ← $01` / `$E008 ← $00` hard-gate toggles, and PC3 soft-gate
      transitions. State pane shows soft gate / hard gate / audible AND
      updating live.

## Display

- [ ] **View → Full-screen** (Alt+Enter) switches to borderless
      full-screen on the same monitor; pressing again returns to the
      previous windowed size and position.
- [ ] `--display=full` (or `--display=fs`) on the command line launches
      directly into full-screen for that run only; `settings.ini`
      `Display.Scale` is unchanged.
- [ ] **View → Scanlines** (Ctrl+L) toggles the CRT-style overlay; the
      menu checkmark tracks state and the setting persists across
      restart.
- [ ] `--scanlines=on` / `--scanlines=off` overrides the persisted
      setting for that run only.
- [ ] Main window geometry is restored on relaunch (size + position).

## Joystick

- [ ] Settings → Joystick tab shows connected gamepad and current
      Left (SW1) / Right (SW2) bindings.
- [ ] Click `Left button (SW1)` → press a button on the pad → mapping
      updates and persists across restart.
- [ ] In a joystick-aware game, both stick slots respond.

## Tape

- [ ] Save a short BASIC program to a new `.mzf`, restart the emulator,
      load it back, RUN succeeds.

## Debugger

- [ ] Open debugger, set breakpoint at a known address, run; emulator
      pauses at the breakpoint.
- [ ] Step (F10/F11) advances PC one instruction.
- [ ] Memory viewer Snap → press a few keys → Diff shows changed bytes.
- [ ] Debugger and Memory Viewer window geometry (size + position)
      survives close-and-reopen and across restart.
- [ ] Breakpoint list survives close-and-reopen of the debugger and
      across a relaunch of the emulator.

## Settings

- [ ] **File → Settings → ROMs…** (Ctrl+S) opens the dialog on the
      ROMs tab.
- [ ] **File → Settings → Display…** (Ctrl+Shift+D) opens on Display.
- [ ] **File → Settings → Keyboard…** (Ctrl+Shift+K) opens on Keyboard.
- [ ] **File → Settings → Joystick…** (Ctrl+Shift+J) opens on Joystick.
- [ ] Tab order is ROMs / Display / Keyboard / Joystick.
- [ ] Changing Display Scale and clicking Apply takes effect without
      restart.
- [ ] `settings.ini` after first run contains `[Display]`, `[Roms]`,
      `[Joystick]`, `[KeyOverrides]`, `[CharMap]`, `[Window]`,
      `[Debugger]`, `[MemoryViewer]`, and `[Breakpoints]` sections —
      each with its own inline self-documenting comment.

## Help

- [ ] **Help → About…** opens the AboutForm (not a MessageBox);
      title says "About MZRaku", header label says "MZRaku", version
      matches `<Version>` in the csproj, a build date is shown, an
      `Emulating: Sharp MZ-XXX` line matches the currently-active
      machine, both project + launcher-setup GitHub links open in
      the browser when clicked and resolve to `sgillon/MZRaku`, and
      Sharp / Claude acknowledgements are present.

## Machine selection (MZ-700 ↔ MZ-80A)

- [ ] `MZRaku.exe` (no flag) launches into MZ-700 and shows the blue
      Sharp screen with `Ready` — as it always has.
- [ ] `MZRaku.exe --mz80a` launches into MZ-80A. Screen is black,
      shows `** MONITOR SA-1510 **` on the top row, and after a
      moment the `BASIC interpreter SA-5510` / `Copyright 1981 by
      SHARP Corp.` / `32492 Bytes` / `Ready` sequence renders — the
      SA-1510 monitor and SA-5510 BASIC both boot cleanly.
- [ ] `File → Machine → MZ-80A` while running on MZ-700 shows a
      restart prompt; Yes restarts into MZ-80A. Same in reverse.
      `settings.ini` `[Machine] Type=` reflects the choice after
      restart.
- [ ] `settings.ini` contains both `[Roms.MZ700]` and `[Roms.MZ80A]`
      sub-sections with self-documenting comments; ROM path
      auto-detection populated MZ-80A's `Monitor` / `Font` / `Basic`
      keys from `roms/SA-1510.rom`, `roms/SA-CG.rom`,
      `basic/SA-5510.mzf` respectively.
- [ ] Diagnostic menu items that are MZ-700-only (Sound Diagnostic,
      Font Sheet, HID Diagnostic, Keyboard Matrix) each show a
      friendly "MZ-700 only for now" MessageBox when opened while
      MZ-80A is active — they do NOT NRE.
- [ ] Debugger (Ctrl+D) and Memory Viewer (Ctrl+M) both work on
      MZ-80A. Set a breakpoint inside SA-1510 code, hit it,
      single-step off, resume.

## MZ-80A regression canaries

- [ ] `MZRaku.exe --mz80a --basic` boots to the `Ready` prompt.
      Type `PRINT 1.5` + Enter (may need to Shift for uppercase —
      known keyboard polish item). Result: `1.5` on the next line
      (regression canary for the Z80 indexed INC/DEC bug — same as
      MZ-700's).
- [ ] `MZRaku.exe --mz80a NEW-INVADERS-80A.mzf` boots the game.
      Title screen with `SCORE :00000  HI-SCORE :` line, invader
      sprite grid, and ship formation visible.
- [ ] `MZRaku.exe --mz80a cricket.mzf` (BASIC game) — currently
      loads via DirectInject but needs the L-command / auto-type
      path to fully RUN. Verify no crash on load.

## Release packaging

- [ ] Version bumped in `MZRaku.csproj` (`<Version>` element). For a
      stable release this is a bare semver string (e.g. `1.0.0`); for
      a preview it carries the `-preview` suffix.
- [ ] About dialog shows the bumped version (sanity check — reads
      from the assembly's InformationalVersion).
- [ ] README planned-work / known-limitations sections reflect what
      actually shipped.
- [ ] Framework-dependent zip built: `MZRaku-<version>-dotnet8.zip`
      (assumes .NET 8 Desktop Runtime on target).
- [ ] Self-contained zip built: `MZRaku-<version>-standalone.zip`
      (no .NET runtime required on target).
- [ ] Both zips extract cleanly to an empty folder and run.
- [ ] Tag created, pushed, release notes drafted via `gh release create`.
