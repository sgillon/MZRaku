# Scripted keys (`--keys`)

`--keys=<file>` plays a script of keystrokes into the emulator, each
at a set frame. Use it to start a game and get past its menus, to type
a demo, or to repeat the same input every time for testing. MZRaku's
own regression suite is driven this way: its scripts are in
[`tools/regress/keys/`](../../tools/regress/keys/).

```
MZRaku.exe --mz700 --basic --keys=hello.keys
```

## An example

```
# hello.keys: type a line of BASIC, list it and run it.
# --basic leaves S-BASIC at Ready by frame 420.
420  type "10 PRINT \"HELLO\""
560  key  Return
600  type "RUN"
640  key  Return
```

## The format

One command per line, as **frame**, **command**, **argument**:

| Command | What it does |
|---|---|
| `<frame> type "text"` | Types each character in turn: pressed, held 4 frames, released, then a 4-frame gap. So each character takes 8 frames. |
| `<frame> key <Key>` | Presses a key, holds it 4 frames and releases it. |
| `<frame> down <Key>` | Presses a key and holds it until a matching `up`. |
| `<frame> up <Key>` | Releases a key held with `down`. |

- **Comments** start with `#` or `;`, on their own line or after a
  command. Blank lines are ignored.
- **The frame** counts emulated frames since launch: 60 a second on
  the MZ-700 and MZ-80A, 50 on the MZ-800. The count is of real-time
  frames: at `--speed=2` each one runs two of the machine's frames, so
  the keys arrive at the same moment on your clock but later for the
  machine. Pausing doesn't stop the count, and neither does a reset
  (Ctrl+R).
- **Frames must not go backwards,** and a command can't start until
  the previous `type` or `key` has finished: its last release plus the
  4-frame gap. The error message names the frame it can start at.
- **Strings** go in double quotes. Inside them, `\"` is a quote and
  `\\` a backslash.

## Which keys

**`type`** takes the characters on a UK PC keyboard: letters, digits,
space and the usual punctuation, including `£` and `¬`. It types them
as a UK keyboard would, Shift included, whatever keyboard your PC
has, so a script gives the same result on every PC. What reaches the
MZ is the character, as when you type it yourself (see
[Keyboard](keyboard.md)).

**`key`, `down` and `up`** take a key name: the names Windows Forms
uses for its `Keys` values, such as `Return`, `Back`, `Tab`,
`Escape`, `Space`, `Up`, `Down`, `Left`, `Right`, `Insert`, `Delete`,
`Home`, `F1` to `F12`, `ShiftKey`, `ControlKey` and `Menu` (Alt).
Names are case-sensitive. Use `type` for keys that type a character:
`key A` is refused.

- **Holding a modifier.** `down ShiftKey` (or `ControlKey`, `Menu`)
  stays held, and the keys after it are pressed with it, until
  `up ShiftKey`. `LShiftKey` and `RShiftKey` count as `ShiftKey`, as
  Windows reports them. `type` can't run while a key is held down.
- **App shortcuts come first,** as with a real keyboard: `Ctrl+R`
  resets the machine, `Alt+F4` closes MZRaku, and so on.

```
# Shift+Esc: BREAK on the MZ-700 (stops a running BASIC program).
900  down ShiftKey
901  key  Escape
909  up   ShiftKey
```

## Choosing the frames

Give the machine time to be ready: a key pressed before BASIC is at
its prompt is lost. With `--basic`, BASIC is ready by about frame 420
on all three machines. After `Return`, leave BASIC time to answer
before the next line (the suite leaves about 30 frames; a command that
prints a lot needs more).

The easiest way to find a frame is to try a script and adjust it.

**On the MZ-80A**, letters typed without Shift are capitals (see
[MZ-80A](mz80a.md#keyboard)). `type "load"` types `LOAD`.

## When a script is wrong

A script with a mistake doesn't run: MZRaku shows the line number and
the problem, then exits with code 2. The same file works with the
headless runner, `MZRakuHeadless.exe`, which prints the message
instead.
