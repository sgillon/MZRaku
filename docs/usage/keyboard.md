# Keyboard

This page covers all three machines. Each machine's guide lists its
non-character keys (Enter, cursors, BREAK, GRAPH and so on):
[MZ-700](mz700.md#keyboard), [MZ-80A](mz80a.md#keyboard),
[MZ-800](mz800.md#keyboard).

By default, the emulator drives the MZ keyboard from the **character**
your PC keystroke produces (after Windows has applied your keyboard
layout). Type `;` and the MZ sees `;`; type `+` and it sees `+`. Out of
the box there is nothing to configure — Windows handles layout, AltGr
and dead keys for you, and the emulator presses the MZ key (with or
without SHIFT) that types that character. `*`, for example, is SHIFT
plus the colon key on the MZ-700, so typing `*` on the PC produces `*`
on the MZ.

The MZ-80A's letters work the other way round from a PC: a letter on
its own is a capital, and Shift gives a small letter. See
[MZ-80A](mz80a.md#keyboard) to change that.

## Editing the mapping

Open **System → Settings → Keyboard** (Ctrl+Shift+K). The primary
view is a clickable diagram of the current machine's keyboard. Each cap shows
the PC key(s) currently bound to it as a small blue badge in the top-
right corner.

**Click any key** to open the per-key editor. Character keys (1, A, `;`,
…) get a two-card view — one for the unshifted glyph and one for the
shifted glyph — each with its current PC binding and the built-in
default it would revert to. Fixed-label keys (CR, GRAPH, ALPHA, CTRL,
SHIFT, BREAK, INST, DEL, cursors) get a single card.

Each slot card has two actions:
- **Edit…** opens a capture dialog. Click the capture box, press the PC
  key (or modifier combo, e.g. Ctrl+G) you want to bind. The dialog
  confirms what it captured and notes any conflict — if your captured
  key already produces a different MZ slot, you get a warning and a
  confirmation prompt before Save replaces the old binding.
- **Reset** removes any override you've added for the slot and reverts
  it to the built-in default shown beneath the current binding.

Edits are live the moment you Save — the diagram redraws to reflect the
new binding, and the emulator picks it up on the next keystroke.
Persistence to `settings.ini` waits for the dialog's **Apply** or
**OK**, matching the rest of Settings.

## Safety gate

If you rebind PC keys so that some essential MZ key ends up with **no**
PC binding at all (e.g. moving PC `1` to a different slot without
restoring a binding for MZ `1` first), the affected MZ key is outlined
in crimson on the diagram. A character key needs both its unshifted and
shifted glyphs covered to count as reachable.

When you click **Apply** or **OK**, the dialog lists the unreachable
keys and asks whether to save anyway. Pick **No** to keep editing.

## Font Sheet

The MZ machines' graphic characters have no PC key. The **Font Sheet**
(**View → Font Sheet…**, Ctrl+G) shows every character in the
character ROM, and in BASIC you can click one to type it. On the
MZ-700 and MZ-800 it also opens by itself when BASIC enters GRAPH mode
(the GRAPH key, F11).

- **MZ-700 and MZ-800:** two banks of 256 characters. The keyboard
  types only the top bank; programs show the bottom one by setting
  bit 7 of the character's colour.
- **MZ-80A:** 256 characters, in a Text half and a Graphics half. The
  keyboard reaches both.

**Click-to-type works in BASIC** (S-BASIC on the MZ-700, 1Z-016 on the
MZ-800, SA-5510 on the MZ-80A). While BASIC is running, the characters
its keyboard can type are outlined: **green** for normal (ALPHA) mode,
**blue** for GRAPH mode only. Click one to type it. If it needs the
other mode, the click switches mode, types it and switches back, so
BASIC stays in the mode it was in. Outside BASIC, a click just names
the character's code.

**Characters no key types (MZ-700).** Click one and the line below the
sheet shows the BASIC that puts it on screen: `PRINT CHR$(n)` where
S-BASIC has a code for it, otherwise two `POKE`s that write it and its
colour straight into screen memory (for example
`POKE 53248,n:POKE 55296,113`). **Type** types that line for you,
without CR, so you can check it or add to it before pressing Enter.

## Import / Export

The Export… and Import… buttons on the Keyboard tab read and write
small `.mzkbd` files — useful for sharing a mapping or switching
between layouts.

The file is a tiny INI; only the user's overrides are stored (defaults
are still applied at runtime). Two sections, the same per-line shapes
used inside `settings.ini`:

```ini
; MZ-700 Keyboard mapping file
[CharMap]
; HHHH=Row,Col,Shift   (HHHH = 4-digit hex Unicode codepoint;
;                       Shift = t | f)
0031=0,1,f   ; '1'
0021=0,1,t   ; '!'

[KeyOverrides]
; KeyName=Row,Col,Shift   (KeyName from System.Windows.Forms.Keys;
;                          Shift = t | f | -)
F5=8,7,t
Control, G=0,6,-
```

Import offers **Merge** (your imported entries win where they overlap)
or **Replace** (clear current overrides first). Changes go live
immediately; Apply / OK still gates persistence.

## Advanced — matrix grid

The **Advanced settings…** button under the diagram opens the machine's
raw keyboard matrix (10 rows of 8 keys), with the keys currently
pressed highlighted live. Click a
matrix cell to edit the character binding for that specific
`(row, col, shift)` slot. It's the same character editor the diagram
uses; the matrix view is just an alternative entry point for slot-by-
slot work.

## Known limitations

The Keyboard tab in Settings lists the first of these.

- **Left and Right PC Ctrl aren't told apart.** Both press MZ CTRL,
  so the keyboard editor can't bind them to different MZ keys.
- **MZ CTRL plus a letter can't be typed.** PC Ctrl+letter combinations
  are MZRaku's own shortcuts (Ctrl+R resets, Ctrl+B loads BASIC, and
  so on).

## Where your changes are kept

Your changes are saved in `settings.ini`, separately from the built-in
mapping, and always win over it. **Reset** on a key removes your
change and brings the built-in one back.

| Machine | Character keys | Other keys |
|---|---|---|
| MZ-700 | `[CharMap]` | `[KeyOverrides.MZ700]` |
| MZ-80A | `[CharMap.MZ80A]` | `[KeyOverrides.MZ80A]` |
| MZ-800 | `[CharMap.MZ800]` | `[KeyOverrides.MZ800]` |

An exported `.mzkbd` file holds just your changes (see
[Import / Export](#import--export)).

## Loading BASIC source

"Load BASIC source…" (Ctrl+Shift+B) reads a plain-text `.bas` file and
types each non-blank, non-comment line into the running BASIC
interpreter. It works on all three machines. Lines starting with `;` or `'` are stripped on the host
side. If BASIC isn't loaded yet the emulator resets, auto-loads BASIC,
then types the source once the READY prompt is up. End the file with
`RUN` to auto-start the program.

Per-character throughput is around 6–8 chars/sec — the auto-typer
waits for the OS's keyboard scan to actually observe each press before
moving on, rather than hardcoding a hold duration. Still not instant
for long listings, but reliable across shifted characters.
