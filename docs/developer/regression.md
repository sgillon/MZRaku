# Regression testing

How to check that a change hasn't broken anything, and the
command-line tools for looking inside a run. This page is for someone
changing the code; the suite's own reference, with every case, is
[`tools/regress/README.md`](../../tools/regress/README.md).

It describes v1.5.0 (45 cases).

## The suite

`tools/regress/regress.py` runs MZRaku on a fixed set of cases (a
machine, some software, maybe a key script) and compares what each
produces at a set frame against an approved "golden" copy: the
screen, and the sound (MZ-800 audio, or the MZ-700 and MZ-80A
speaker log), plus any file a SAVE wrote. MZRaku is deterministic
frame by frame, so an unchanged build reproduces every golden byte
for byte, and any difference means behaviour changed.

```
python tools/regress/regress.py                 build, run every case in the app, compare
python tools/regress/regress.py --headless      the same through the headless runner
python tools/regress/regress.py --no-build      use the existing build
python tools/regress/regress.py keys typing     only cases whose name contains a filter
python tools/regress/regress.py --approve mz80a run and overwrite those goldens
```

Run it before committing anything that touches emulation, the
keyboard, tape handling, loading or the session. `--headless` takes
under half a minute and is the one to run while working; run the app
version (about 2½ minutes, four windows at a time, minimised) before
a commit, since only it goes through `MainForm`. The last line reads
`45/45 cases match golden`.

The firmware and software come from the local, git-ignored
`_mz-information-repository/`, with each firmware file pinned by
SHA-1; the goldens live in `_regression/golden/`, also local. A fresh
clone has the harness and key scripts but none of these, so the suite
runs only where they have been set up.

### When a case differs

A case prints `DIFF` and what differed (pixels, sound log lines,
audio or saved-file checksums), or `FAIL` if it produced nothing or
timed out. Its output is in `_regression/out/`, beside the golden in
`_regression/golden/`.

1. Look at the two: open both PNGs, diff the `.snd.txt` files, listen
   to the `.wav`.
2. If the change is a bug, fix it.
3. If it is intended (a fix that changes the screen, a new behaviour),
   approve just those cases, `--approve <filter>`, and add a line to
   the log in `_regression/README.md`: date, commit, cases and why.
   Goldens are approved from the app only; `--headless` refuses
   `--approve`.

### Keeping runs deterministic

The suite only works while a run is a pure function of its inputs.
What keeps it so:

- **Frames, not time.** The core counts emulated frames and CPU
  cycles. Nothing in a machine reads the clock; real time only paces
  the app's frame timer, which the headless runner doesn't have.
- **No real input in a `--dump` run.** The session ignores the real
  keyboard (`EmulatorSession.IgnoresHostKeys`) and doesn't poll the
  gamepad, so a key pressed while a suite window has focus changes
  nothing. Scripted keys still get through.
- **Sound that can be compared.** The MZ-700 and MZ-80A make their
  square wave in real time, on the sound thread, so a dump records
  the speaker's *inputs* instead (`.snd.txt`: every gate, reload and
  clock change, stamped with the CPU cycle). The MZ-800 renders its
  audio in emulated time, so its `.wav` is exact.
- **A sandbox per case.** Each case runs in
  `_regression/sandbox/<case>/` with the shipped default
  `settings.ini`, so your own settings never change a result.

A change that brings in anything else (the clock, randomness, thread
timing, the host's keyboard layout) will show up as cases that differ
from run to run. `type` in a key script maps characters through a
fixed UK layout for this reason.

### Adding a case

The steps are in [`tools/regress/README.md`](../../tools/regress/README.md#adding-a-case):
a row in `CASES` in `regress.py`, a key script in `keys/` or a
settings file in `presets/` if needed, a few runs to check it is
stable, then approve, log, and add it to the README's tables. Some
things learned writing the existing ones:

- With `--basic`, BASIC is at its Ready prompt by frame 420 on all
  three machines; start typing there.
- Space presses at uneven gaps (the `*_keys` cases use 0, 3, 7, 1, 5,
  2, 6, 4, 9, 11 frames), so they land at different points in the
  ROM's key scan. Even spacing can hide a timing bug.
- Make the result visible on the screen at the dump frame (a `LIST`, a
  line that reads one way only if every press landed), and say in the
  script's comments what the screen should show.
- For a long script, write a small generator in Python in the
  scratchpad, outside the repository, and commit only the `.keys` it
  writes.
- To compare two routes to the same result (split ROM files, a speed
  multiplier), point the new case at the other case's golden in
  `SAME_GOLDEN`.

## Tools for looking inside a run

The app and the headless runner take the same command line, so any of
these can be tried on one case outside the suite. The headless runner
(`tools/MZRakuHeadless`, built with `dotnet build`) is quicker and
prints the session's status messages, stamped with the frame, to the
console.

| Option | What it does |
|---|---|
| `--dump=<path>` | Records a trace while running, writes the files below at the dump frame, then exits |
| `--dumpframe=<N>` | The dump frame (default 120). Frames count from launch, or from the last reset |
| `--keys=<file>` | Plays a key script (below). A bad script exits with code 2 before the machine starts |
| `--insert=<cassette>` | Inserts a cassette for the machine's own LOAD, as File → Insert cassette does; the script types the LOAD |
| `--settings=<ini>` | Runs with another settings file, read-only. The suite checks nothing was written |
| `--speed=<N>` | Runs the machine at 1–4× speed. Frame counts (`--dumpframe`, `--keys`) stay in real-time frames |
| `--mz700`, `--mz80a`, `--mz800`, `--basic`, a cassette path | As for a normal run |

### What a dump writes

| File | Machines | What |
|---|---|---|
| `<path>` | all | The state at the dump frame: CPU registers and, on the MZ-700 and MZ-800, the PPI, memory map, PIT and tape trap hits; on the MZ-800 also the CRTC registers and which planes were drawn on |
| `<path>.trace` | all | A state line for each of the first 10 frames and every 20th after; the last 256 PC values; the PIT and bank-switch write logs (MZ-700, MZ-800); on the MZ-800 also video memory, CRTC, mode, PIO, PSG and PPI write logs |
| `<path>.png` | all | The screen at its native size |
| `<path>.test.png`, `.test640.png`, `.testscroll.png`, `.testsplit.png` | MZ-800 | Synthetic patterns drawn through the real renderers |
| `<path>.ram` | MZ-800 | The 64 KB of DRAM, for disassembling code a program moved at run time |
| `<path>.wav` | MZ-800 | The audio since boot |
| `<path>.snd.txt` | MZ-700, MZ-80A | Every change at the speaker's inputs since boot |

The trace logs only exist in a `--dump` run (`EmulatorSession.Start`
turns them on), so normal runs pay nothing for them. A SAVE in any
run writes its `.mzf` to `saves\` next to the executable.

### Key scripts

One command per line, at a frame count since launch:

```
# frame  action
420  type "PRINT 1+2"   ; each character pressed and released in turn
460  key  Return        ; press, hold, release
470  down ShiftKey      ; held until the matching up
475  key  F1
480  up   ShiftKey
```

Key names are `HostKey` names (the WinForms `Keys` names). `type`
sends each character with the key and Shift a UK keyboard would use;
`key` and `down` send a character only for Return, Back, Tab, Escape
and Space. Frames must not go backwards, and a command can't start
before the previous `type` or `key` has let go of its last key. The
full rules are in `Core/Session/KeyScript.cs`; the user reference,
`docs/usage/keys-script.md`, comes with v1.5.0's user docs.

Scripted keys go through the app's hotkeys first, as real keys do, so
`Ctrl+R` resets and `Alt+F4` ends the run. The headless runner keeps a
copy of that hotkey list (`Hotkey` in its `Program.cs`), which must
stay in step with `MainForm`.

### Other checks

- **At startup**, `MatrixValidation` checks each machine's key maps
  against its matrix reference (a dialog in the app, stderr in the
  headless runner), and the app's `HostKeyParity` checks `HostKey`
  still matches WinForms `Keys`.
- **Doc comments.** To check the XML doc comments for broken
  references and bad markup, build with documentation on into a
  scratch folder (outside the repository), which must show 0
  warnings, then build normally again. Missing comments (CS1591) are
  not counted:

  ```
  dotnet build MZRaku.csproj -c Debug --no-incremental -p:GenerateDocumentationFile=true -p:NoWarn=CS1591 -o <scratch>/docchk
  ```
- **`tools/RomAnalyse`** dumps the MZ-700 monitor's four key tables
  and disassembles its key scan, for "where does the ROM do X?"
  questions. `Z80Core`'s `Z80Disassembler` does the same for any
  address.
- **The release check**, [`docs/release-check.md`](../release-check.md),
  is the by-hand pass before a release: the things the suite can't
  see, such as real keys, sound by ear, the menus and dialogs.
