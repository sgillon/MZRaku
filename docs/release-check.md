# Release readiness check

A short manual smoke test to run before tagging a preview/release. Aim
for ~10-15 minutes end-to-end. The point is to catch behaviour that
compiles fine but doesn't *work* — things automated tests don't see.

If something here drifts out of date or a new escape gets through into
a release, update the checklist before fixing the bug.

## Build

- [ ] `dotnet publish` (Release, single-file) completes with no warnings.
- [ ] Publish output does **not** contain `1z-013a.rom`, `mz700fon.int`,
      or `1Z-013B.mzf` (Sharp copyright — must not be redistributed).
- [ ] Exe runs on a clean folder (no `settings.ini`) and auto-detects
      the three system files from `roms/` next to it.

## Keyboard — Monitor prompt

- [ ] Letters A-Z type correctly (no Shift).
- [ ] Shift + letter gives uppercase reliably — type `SHIFT+P` x10,
      expect `PPPPPPPPPP` not `PPpPPPpPPP`. (Known regression as of
      v0.0.7-preview — track but don't block on it.)
- [ ] Shift + number gives the symbol reliably (`SHIFT+8` x10 → `**********`).
- [ ] Cursor keys move the cursor.
- [ ] Backspace deletes; Insert inserts a space.
- [ ] Enter executes the line.
- [ ] Esc + Shift breaks a running monitor loop.

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
- [ ] **Advanced settings…** button opens the resizable child window
      with the live matrix grid (top) and overrides list (below).
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

## Settings

- [ ] Ctrl+S opens the settings dialog.
- [ ] Tab order is ROMs / Display / Keyboard / Joystick.
- [ ] Changing Display Scale and clicking Apply takes effect without
      restart.
- [ ] `settings.ini` after first run contains `[Display]`, `[Roms]`,
      `[Joystick]`, `[KeyOverrides]`, `[CharMap]` sections — each
      with its own inline self-documenting comment.

## Help

- [ ] **Help → About…** opens the AboutForm (not a MessageBox);
      shows the icon, the version (matches `<Version>` in the
      csproj), a build date, both project + launcher-setup GitHub
      links open in the browser when clicked, and Sharp / Claude
      acknowledgements.

## Release packaging

- [ ] Version bumped in `MZ700Emul.csproj` (`<Version>` element).
- [ ] About dialog shows the bumped version (sanity check — reads
      from the assembly's InformationalVersion).
- [ ] README planned-work / known-limitations sections reflect what
      actually shipped.
- [ ] Tag created, pushed, release notes drafted via `gh release create`.
