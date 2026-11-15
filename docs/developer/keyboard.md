# The keyboard layer

How a key gets from the host to the emulated ROM, on all three
machines. This page is for someone changing the code. The user's view
of the keyboard is in [`docs/usage/keyboard.md`](../usage/keyboard.md).

It describes v1.5.0, after step 4.11a. v1.6.0 reworks this layer
(MZRakuCLI brings a second front end, SDL). The two lists at the end,
[inconsistencies](#inconsistencies) and
[WinForms-shaped parts](#winforms-shaped-parts), are written for that
rework.

## The short version

Every machine has the same kind of keyboard: a 10 × 8 matrix. The
ROM writes a strobe number (0–9) to the 8255 PPI's port A and reads 8
column bits back on port B, and a pressed key reads as 0. MZRaku keeps
that matrix as ten bytes. A host key event sets or clears bits in it,
and the ROM's own key scan reads them as it would on the real machine.
MZRaku never puts a character into the ROM's buffer or RAM; it only
presses matrix keys.

A host key gets to the matrix in one of three ways, tried in order:

1. **A key override:** the user's own binding from a physical key
   (with modifiers) to a matrix cell.
2. **The special-key map:** built-in bindings for keys that don't type
   a character: cursor keys, Enter, Esc, Ctrl, F-keys and the mode
   keys.
3. **The char map:** for everything else, the *character* the host's
   keyboard layout produced, looked up to find a cell and whether MZ
   SHIFT must be held. This is why a UK, US or German PC keyboard all
   work without a key map of their own.

## Files

| File | What it holds |
|---|---|
| `Core/Host/HostKey.cs` | The core's key code: a copy of WinForms `Keys` (same names and values) |
| `Core/Host/IKeyboardInput.cs` | The input contract a front end calls: key down, key press (the character), key up, focus lost |
| `Core/Keyboard/KeyboardMatrixBase.cs` | Shared by all three keyboards: the matrix bytes, holds, SHIFT resolution, staging, scan observation |
| `Core/Keyboard/KeyboardAutoTyper.cs` | The MZ-700 and MZ-800 auto-typer |
| `Core/Keyboard/KeyOverride.cs` | The key-override store, one per machine |
| `Core/Keyboard/MatrixOverrides.cs` | Base for the three char-map override stores |
| `Core/Keyboard/KeyboardMapFile.cs` | The `.mzkbd` exchange file |
| `Core/Keyboard/KeyboardDiagnostics.cs` | What the HID Diagnostic shows about the last key |
| `Core/Keyboard/MatrixValidation.cs`, `MatrixCoverage.cs` | Startup checks of the maps against the matrix references; unbound cells for the editor |
| `Core/<machine>/Mz*Keyboard.cs` | Each machine's key handling (`Mz700Keyboard`, `Mz800Keyboard`, `Mz80aKeyboard`); the MZ-80A's holds its own auto-typer |
| `Core/<machine>/Mz*SpecialKeyMap.cs` | Layer 2 |
| `Core/<machine>/Mz*CharMap.cs`, `Mz*CharMapOverrides.cs` | Layer 3 and its user overrides |
| `Core/<machine>/Mz*MatrixReference.cs` | The canonical matrix: what each cell is, from the manuals. The char maps are built from it |
| `Core/<machine>/Mz*KeyboardLayout.cs` | The physical MZ keyboard, for drawing it |
| `Core/<machine>/Mz*KeyTables.cs` | The ROM's key tables read backwards: which key types a glyph. For the Font Sheet |
| `Core/Session/KeyScript.cs` | `--keys=<file>` scripts |
| `UI/Keyboard/` | The keyboard editor and its machine contexts |
| `UI/Diagnostics/HidDiagnosticForm.cs` | HID Diagnostic (Ctrl+H) |

## From the host to the machine

**Front end.** `MainForm` gets the key first, in `ProcessCmdKey`.
App hotkeys and menu shortcuts (Ctrl+R reset, Ctrl+B BASIC, Ctrl+P
screenshot, Alt+Enter full screen, Pause, and others) are used there
and go no further. Everything else arrives as WinForms `KeyDown`,
`KeyPress` (the character, if the key typed one) and `KeyUp`. The
handlers cast `Keys` to `HostKey`, modifier flags included, and call
`EmulatorSession.KeyDown` / `KeyPress` / `KeyUp`. When the window
loses focus (`Deactivate`) it calls `ReleaseHostKeys`, because the
key-ups will go to another window.

**Session.** `EmulatorSession` passes each event to the active
machine's `IKeyboardInput`, which is its keyboard object. During a
`--dump` run it ignores real keys (`IgnoresHostKeys`), so a suite
window that has focus can't change a capture; `--keys` events still
get through.

**Scripted keys.** A `--keys` script (`KeyScript`) gives a list of
host events stamped with frame numbers. The front end delivers each
the way a real key arrives: `MainForm.InjectScriptedKey` sends it
through `ProcessCmdKey` first, then key down and key press. The
headless runner does the same in `DeliverScriptedKey`, with its own
copy of the hotkey list. `type` uses a fixed UK layout to pick the key
and Shift for each character, so a script gives the same result on any
host.

**Keyboard.** Each machine's keyboard handles the three events:

- **Key down:** it records PC Shift, then tries the three layers. Layers 1 and 2
  press the matrix cell straight away and return true ("handled").
  Shift on its own does nothing yet (see [SHIFT](#shift)). Any other
  key is remembered as `_pendingDownVk` and the method returns false,
  waiting for its character.
- **Key press:** it pairs the character with `_pendingDownVk`, looks
  it up in the char map, and presses that cell. A character the map
  doesn't hold is dropped.
- **Key up:** it releases the key's cell, drops it if still staged,
  and recomputes SHIFT.

Each press is recorded as a *hold*: the cell, keyed by the host key
that caused it, plus its SHIFT requirement. A held key repeating (OS
auto-repeat) finds its hold already there and does nothing.

**ROM.** The PPI (`Ppi8255`) calls `ReadRow(strobe)` when the ROM
reads port B. `ReadRow` also notes which strobes have been read since
the last clear; the auto-typers use that to see the ROM's scan.

### Per machine

| | MZ-700 | MZ-800 | MZ-80A |
|---|---|---|---|
| SHIFT cell | (8, 0) | (8, 0) | (0, 0) |
| CTRL | (8, 6), PC Ctrl, via the special-key map | (8, 6), the same | (0, 7): set from the Ctrl flag on every key event, and also PC Ctrl and Esc in the special-key map |
| BREAK | Shift+Esc: SHIFT + (8, 7) | the same | Shift+Esc: SHIFT + (0, 7) |
| Mode keys | F11 GRAPH (0, 6), F12 ALPHA (0, 4); Shift+F12 shift lock | the same | F11 GRPH (0, 1), toggles |
| Special keys with their own SHIFT | none | none | Left and Down (SHIFT + Right / Up), Insert (SHIFT + DEL) |
| Letters | PC `a` and `A` both type the unshifted key (capital) | the same | PC `a` types the unshifted key (capital), `A` the shifted one (lowercase); `InvertLetterShift` in `[Keyboard.MZ80A]` swaps them |
| Key scan | 1Z-013A monitor (scan $0A50, tables from $0BEA); S-BASIC 1Z-013B its own (tables $121D) | 9Z-504M monitor in MZ-700 mode; 1Z-016 BASIC its own (scan $0BF6, tables $0D47) | SA-1510's GETKY ($08CA, scan $0A50), for SA-5510 too |
| Auto-typer | `KeyboardAutoTyper` | `KeyboardAutoTyper` | its own, in `Mz80aKeyboard` |

## SHIFT

SHIFT is the hard part, because a host key and its MZ key don't always
agree on it. On a UK PC, Shift+`'` types `@`, but `@` on the MZ-700 is
an unshifted key; and `'` is unshifted on the PC but shifted on the
MZ-700.

**Each hold says what it needs** (`ActiveHold.ExplicitMzShift`):
`true` (SHIFT on), `false` (SHIFT off) or `null` (follow PC Shift).
Char-map presses are always true or false. Special keys are null,
except the MZ-80A's three. Overrides can be any of the three.

**`EffectiveMzShift`** decides the SHIFT cell: the first hold with a
true or false wins; otherwise SHIFT follows PC Shift if any key is
held; with no key held, SHIFT is off. PC Shift on its own never
presses MZ SHIFT. If it did, the ROM could see SHIFT between keys and
apply it to the next one, even when that key wants SHIFT off.
`ApplyShiftState` writes the result to the SHIFT cell; every key
event calls it.

**Staging.** When a press needs SHIFT on, SHIFT is set at once but the
key's own cell waits two frames (`LiveShiftStageFrames`), so the ROM
reads SHIFT before the key. Without it the ROM can read SHIFT just
before both land and take the key as unshifted. That showed as UK `'`
typing `7` (the first shift-race fix), and as Shift+F12 missing
about one press in three (v1.5 4.8a). `TickStagedKeyBits`, called once
a frame, lands the key; a key released inside those two frames is
dropped rather than typed wrongly.

What gets staged differs by machine:

- **MZ-700 and MZ-800:** every char-map press that needs SHIFT, and
  every special or override press while SHIFT is on for any reason
  (`KeyboardMatrixBase.PressKeyBit`; the MZ-800 since 4.11a).
- **MZ-80A:** char-map presses that need SHIFT, and special keys with
  their own SHIFT. Overrides and other special keys are never staged.
  Its cases show no miss.

**Releasing.** A key's cell clears at once on key up, then SHIFT is
recomputed. (Staging the release too was tried once and opened its
own race: see the comment in `Mz80aKeyboard.OnKeyUp`.)

**Shift flag lag.** On the first Shift key-down the host's Shift flag
can be missing, so `PcShiftAfterKeyDown` also counts the Shift key
itself, and `PcShiftAfterKeyUp` stops counting a released Shift key
whose flag is still set. The MZ-700 and MZ-800 use both; the MZ-80A
reads the flag only.

## Every frame

Each machine's `RunFrame` advances the keyboard once a frame, before
running the CPU:

- MZ-700: auto-typer, then staged keys.
- MZ-800: staged keys, then auto-typer.
- MZ-80A: `Mz80aKeyboard.TickFrame`: staged keys, then its auto-typer.

A machine reset calls `ReleaseAll`, which clears the whole matrix and
every hold. Without it a reset by Ctrl+R would boot with MZ CTRL still
held. Focus loss calls `ReleaseHeldKeys`, which releases only host
holds and leaves the auto-typer's cells alone.

## The auto-typers

Both type through the matrix, as a person would. They set cells
directly, not through holds.

**`KeyboardAutoTyper` (MZ-700, MZ-800)** watches the ROM's scan. For
each press it sets SHIFT if needed and waits until the ROM has read
strobe 8, then sets the key and waits until its strobe is read, then
releases it and waits for one more read. Each wait gives up after 10
frames (`ScanTimeoutFrames`). After CR it waits a fixed 30 frames
while BASIC stores the line. Entry points: `TypeString` (CR is
`\r`) and `TypePress` (one cell).

**The MZ-80A's typer** (`Mz80aKeyboard.TypeString` / `TypePress`)
works on fixed times: SHIFT 2 frames ahead, key held 4, released 3.
After CR it waits until the ROM has read all ten strobes, which SA-5510
does only back in its key wait (at least 3 frames, at most 120). SA-1510
doesn't scan on a steady rhythm, so the scan-watching typer didn't fit.
`TypeString` turns capitals into lowercase keys, because an unshifted
key types a capital on the MZ-80A.

**Who uses them:**

- `--basic` autorun (`AutoLoadOrchestrator`): `RUN` on the MZ-700;
  `LOAD`, then `RUN` once the tape trap has delivered, on the MZ-80A.
- File → Load BASIC source (`EmulatorSession.TypeIntoActiveMachine`),
  one line at a time. On the MZ-800 it starts once 1Z-016 has scanned
  every strobe (or after 250 frames).
- The Font Sheet: a click presses one key, with mode keys around it if
  needed; the MZ-700's Type button types a BASIC statement.

**Known limit:** the MZ-800 typer types nothing at the IPL menu or in
9Z-504M; real keys and `--keys` scripts work there (release checklist,
backlog).

## Font Sheet key tables

`Mz700KeyTables`, `Mz800KeyTables` and `Mz80aKeyTables` answer
"which key, in which mode, types this glyph?" by reading the running
BASIC's own key tables backwards. Each class's doc comment gives the
addresses and the disassembly they came from. They work only while
BASIC runs (S-BASIC 1Z-013B, MZ-800 BASIC 1Z-016, SA-5510), because the
tables are BASIC's. `Mz700KeyTables.BasicStatementFor` also gives a
BASIC statement for glyphs no key reaches.

## Mode panes

The status bar's right-hand pane shows the keyboard mode. Every
machine reads it from the machine's own flag every 10 frames, so it
can't drift:

| Machine | Value | Read by |
|---|---|---|
| MZ-700 | S-BASIC's mode, $0060: ALPHA, LOCK, GRAPH; "—" outside S-BASIC (`Mz700KeyTables.BasicKeyboardMode`) | `MainForm`, on `FrameRan` |
| MZ-800 | 1Z-016's mode, $108D: the same three; "—" unless BASIC has started since reset and its code at $0A19 is still there (`MZ800.BasicKeyboardMode`) | `MainForm`, on `FrameRan` |
| MZ-80A | SA-1510's GRAPH flag, $1170: ALPHA or GRAPH; "—" while a program has SA-1510 swapped out (`Mz80aKeyTables.InGraphMode`) | `AutoLoadOrchestrator`, raising `GraphModeSampled` |

The HID Diagnostic shows the same values, with their sources.

GRAPH mode also opens the Font Sheet, on the MZ-700 and MZ-800
(`AutoLoadOrchestrator`).

## RAM

The keyboard writes no RAM. Until v1.5 4.11a the MZ-700 and MZ-800
copied SHIFT into $1170, but that byte is the monitor's GRAPH flag, so
SHIFT typed GRAPH glyphs at the monitor prompt and GRAPH dropped after
one key (B15, issue #6). Every ROM reads SHIFT from the matrix.

One loading path outside the keyboard does write keys into RAM: when
the MZ-800 autoloads a BASIC tape, `MZ800.TypeIntoBasic` puts `LOAD`
and `RUN` straight into 1Z-016's key buffer, which BASIC reads when it
first waits for a key (see [`tape-traps.md`](tape-traps.md)).

What the keyboard code reads, for the mode panes and the Font Sheet:

| Address | Machine | What it is |
|---|---|---|
| $0060 | MZ-700 | S-BASIC's keyboard mode (also its cursor glyph): $EF ALPHA, $43 shift lock, $FF GRAPH |
| $121D | MZ-700 | S-BASIC's four key tables |
| $108D | MZ-800 | 1Z-016's keyboard mode: 0, 1, 2 |
| $0D47 | MZ-800 | 1Z-016's four key tables |
| $1170 | MZ-80A | SA-1510's GRAPH flag (the MZ-700 monitors use $1170 the same way) |
| $118F | MZ-80A | SA-1510's shift-inversion flag |

## Settings, overrides and the editor

- Key overrides live in `[KeyOverrides.MZ700]`, `[KeyOverrides.MZ80A]`
  and `[KeyOverrides.MZ800]`; char-map overrides in `[CharMap]` and
  `[CharMap.MZ80A]` / `[CharMap.MZ800]`. `EmulatorSession` hands them to the
  keyboards at start-up: key overrides as a field on the keyboard
  object, char-map overrides as a static on the char-map class.
- A char-map override can also *suppress* a default, so a key the
  user moved no longer types where it did.
- The keyboard editor (`UI/Keyboard/`) edits both layers and saves
  `.mzkbd` files (`KeyboardMapFile`).
- At start-up `MatrixValidation` checks each special-key map, char map
  and layout against its matrix reference, and the app shows any
  complaint.

## Tests

The suite (`tools/regress/`) drives keys through the real path with
`--keys` scripts:

- `*_typing`: letters, shifted symbols, cursor keys, GRAPH and ALPHA.
- `*_keys` (4.11a, with `presets/keys_overrides.ini`): CLR through
  Shift+INST/HOME and through an override with its own SHIFT, at uneven
  spacing; cursor left with and without Shift; an override with SHIFT
  forced off under PC Shift; shift lock or GRPH ten times; Shift+F1;
  BREAK stopping a program.
- `*_monitor_typing` (4.11a): typing at each monitor prompt.

`tools/regress/README.md` lists every case.

## For v1.6.0

### Inconsistencies

- **Layering is written three times.** Each keyboard has its own
  `OnKeyDown` / `OnKeyPress` / `OnKeyUp` doing override → special →
  char, with small differences: the MZ-80A checks for Shift before
  overrides, and re-applies SHIFT on auto-repeat; the others don't.
- **Staging differs.** The MZ-700 and MZ-800 hold special and override
  keys back whenever SHIFT is on; the MZ-80A only for its own three
  shifted special keys, and never for overrides.
- **Two auto-typers.** Scan-watching for the MZ-700 and MZ-800, fixed
  times for the MZ-80A, with different entry points
  (`Keyboard.AutoType.TypeString` against `Keyboard.TypeString`). The
  MZ-800 calls `TypePress` with an `Mz700CharMap.Press`. The CR wait
  is fixed (30 frames) on one and detected on the other.
- **Frame order differs:** the MZ-700 ticks the auto-typer before the
  staged keys; the others after.
- **MZ-80A CTRL has two paths:** the Ctrl flag on every key event
  writes (0, 7) directly, and PC Ctrl and Esc are also holds on (0, 7).
  A key up without the Ctrl flag clears (0, 7) even while Esc is held.
- **Shift flag:** the MZ-80A ignores the lag workaround.
- **Letter case** goes opposite ways: PC Shift types the same capital
  on the MZ-700 and MZ-800, but a lowercase letter on the MZ-80A.
  Only the MZ-80A has a setting to swap.
- **MZ CTRL + letter can't be typed** on any machine. PC Ctrl+letter is
  either an app hotkey or reaches the keyboard as a control character
  no char map holds, so CTRL is held but the letter is dropped.
- **Special-key map shapes:** the MZ-700's is `(row, col)`; the
  MZ-800's and MZ-80A's carry a SHIFT requirement too (all null on the
  MZ-800).
- **Override stores:** key overrides are per keyboard object, char-map
  overrides are statics, set for the MZ-80A and MZ-800 even when
  another machine runs.
- **Mode panes come from two places:** `MainForm` polls the MZ-700 and
  MZ-800; `AutoLoadOrchestrator` raises an event for the MZ-80A.
- **Font Sheet auto-open:** the MZ-700 reads $0060 bit 4 itself, and
  only after a `--basic` load; the MZ-800 uses `BasicKeyboardMode`;
  the MZ-80A doesn't open it.
- **The auto-typer and live keys share the matrix** with nothing
  between them: a live key's `ApplyShiftState` rewrites the SHIFT cell
  the auto-typer may have set.

### WinForms-shaped parts

- **Key down, then key press.** The character arrives as a separate
  event that `_pendingDownVk` pairs with the key that caused it. SDL
  sends text (`SDL_TEXTINPUT`) without saying which key produced it.
- **`HostKey` is WinForms `Keys`:** Windows virtual-key codes with
  modifier flags in the high bits (`UI/HostKeyParity.cs` checks they
  stay equal). Left and right Ctrl, Shift and Alt arrive as one key;
  AltGr arrives as Ctrl+Alt.
- **The Shift flag lag workaround** (`PcShiftAfterKeyDown`,
  `PcShiftAfterKeyUp`).
- **Hotkeys live in the front end** (`ProcessCmdKey` and menu
  shortcuts), and the headless runner keeps a copy that has to match.
- **Focus loss** comes from the form's `Deactivate`.
- **Fixed-frame staging:** two frames for live keys, and fixed times in
  the MZ-80A typer. This goes against the project's "detect, don't
  delay" rule; the MZ-700/MZ-800 typer shows the other way, watching
  the ROM's scan.
