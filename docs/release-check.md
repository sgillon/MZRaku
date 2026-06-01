# Release readiness check

A short manual smoke test to run before tagging a preview/release. Aim
for ~5-10 minutes end-to-end. The point is to catch behaviour that
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
- [ ] MZ Ctrl via PC Left-Ctrl: known broken (WinForms VK
      normalisation). Verify still broken so the fix is obvious when
      it lands — do not silently regress.

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
- [ ] Changing Display Scale and clicking Apply takes effect without
      restart.
- [ ] `settings.ini` after first run contains `[Display]`, `[Roms]`,
      `[Joystick]`, `[KeyOverrides]` sections.

## Release packaging

- [ ] Version bumped in `MZ700Emul.csproj`.
- [ ] README planned-work section reflects what actually shipped.
- [ ] Tag created, pushed, release notes drafted via `gh release create`.
