# HID Diagnostic

`Debug > HID Diagnostic…` (Ctrl+H) opens a live view of host input and
how it lands on the MZ keyboard matrix, on all three machines. Useful
for "why isn't this key working", verifying joystick button mappings, or watching the
GRAPH/ALPHA mode bit flip in real time. The window doesn't grab focus
on open, so you can keep typing into the emulator and watch the
diagnostic update.

Three panes, all refreshed each frame:

- **Host input (Windows side).** The last `KeyData` seen (e.g.
  `Control, ShiftKey, G`) with its numeric value, the last KeyPress
  character, the last KeyUp, and the modifier keys currently held.
  Below that, joystick state per slot: configured SW1/SW2 button
  indices, raw WinMM axes (X, Y as 0–255) and the raw button bitmask,
  plus the resolved SW1/SW2 bits.
- **Mapping (which layer matched).** Which of the three keyboard
  layers handled the last keypress — `Override` (user-editable
  physical-key map), `SpecialKey` (built-in non-character keys like
  cursors, F-keys, GRAPH/ALPHA), `Character` (the char-driven path
  through `CharMap`), or `None` if nothing matched. The resolved
  `(row, col, MzShift)` is shown alongside, with shift recorded as
  ON / OFF / pass-through.
- **MZ machine state.** The full keyboard matrix, 10 rows of 8 bits
  (`0` = pressed, since the matrix is active-low), with each row's
  hex byte alongside for at-a-glance reading. Below the grid: the last
  row the ROM scanned, whether the MZ's SHIFT key is pressed, and the
  keyboard **mode** the status bar shows (`ALPHA`, `LOCK`, `GRAPH`,
  or `—` when it can't be known), with the memory it's read from. The
  MZ-800 adds its display mode. Last, the joystick as the MZ sees it:
  each stick's position and buttons (the MZ-80A has no joystick).

The window doesn't replay history — it just shows the *most recent*
event of each kind, so press one key at a time when investigating a
specific behaviour.

**Capturing a snapshot.** Two buttons at the bottom dump the current
contents of all three panes as plain text:

- **Copy** puts the snapshot on the clipboard, ready to paste into a
  bug report or chat.
- **Save…** writes the same snapshot to a `.txt` file (default name
  `hid-diag-YYYYMMDD-HHMMSS.txt`).

Both capture whatever's on screen at the moment you click, so press the
key (or wiggle the stick) you're interested in, then hit the button
before pressing anything else.
