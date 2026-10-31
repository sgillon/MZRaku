# Regression suite

`regress.py` runs MZRaku on a fixed set of cases and compares each
result against an approved "golden" copy. The emulator is
frame-deterministic, so an unchanged build reproduces every golden
byte for byte. Any difference means behaviour changed.

There are 27 cases: 6 on the MZ-700, 6 on the MZ-80A and 15 on the
MZ-800. The authoritative list is the `CASES` table in `regress.py`.
This page explains each case and what it uses, so **keep it in step
with that table.**

## Running it

```
python tools/regress/regress.py              build (Debug), run every case, compare
python tools/regress/regress.py --no-build   use the existing build
python tools/regress/regress.py fist manic   only cases whose name contains a filter
python tools/regress/regress.py --approve    run and overwrite the goldens
python tools/regress/regress.py --headless   run through the headless runner instead
```

- A full run takes about 2½ minutes. Cases run in real time, four
  at once.
- Each window opens minimised and doesn't take focus. A `--dump`
  run ignores the real keyboard and gamepad.
- `--headless` runs the same cases through `tools/MZRakuHeadless`, a
  console runner built on `MZRaku.Core` alone: no window, no sound
  device, frames unpaced. It is compared against the same goldens,
  which proves the core doesn't depend on the Windows front end. A
  full run takes under half a minute, one case per CPU core. It
  builds the runner rather than the app, and `--no-build` works as
  before. Goldens are approved from the app only, so it refuses
  `--approve`.
- The last line reads `27/27 cases match golden`. A failing case
  prints `DIFF` with what differed, or `FAIL` if it produced no
  output or timed out.

## How a case runs

Each case gets a fresh `_regression/sandbox/<case>/` containing:
- the build's binaries;
- `tools/regress/settings.ini` (the shipped defaults), so your own
  settings never affect a result and are never written;
- the firmware listed below.

The harness launches
`MZRaku.exe <args> --dump=<out> --dumpframe=<N>` (or
`MZRakuHeadless.exe` with the same arguments). The emulator runs
`N` emulated frames, writes its dump files and exits. Frames count
from launch.

Before running, the harness checks that every input exists, and that
each firmware file matches its pinned SHA-1. It refuses to run
otherwise, because a different ROM would change the results.

## What is compared

| Artefact | Cases | Comparison |
|---|---|---|
| `<case>.png` | all | The screen at the dump frame, pixel-exact (decoded RGB, so PNG encoder differences don't matter) |
| `.test.png`, `.test640.png`, `.testscroll.png`, `.testsplit.png` | `mz800_ipl` | Synthetic MZ-800 patterns drawn through the real renderers: 320-mode colour bars, 640-mode bit order and banks, hardware scroll, and split-screen scroll (the tech-ref p. 10 example). Pixel-exact. |
| `.wav` | `mz800_ipl`, `mz800_basic`, `uridium`, `manic` | MZ-800 rendered audio (PSG plus PIT), byte-identical (md5) |
| `.snd.txt` | all MZ-700 and MZ-80A cases | Log of every change at the speaker's inputs (soft gate, hard gate, counter reload, input clock), compared line by line. MZ-700/80A sound is generated in real time, so the log is what can be compared exactly. |

## Inputs

Everything below comes from `_mz-information-repository/`. It is
local and git-ignored, because ROMs and software are Sharp or
publisher copyright. Only the harness, its `settings.ini` and the
key scripts are tracked.

### Firmware (copied into every sandbox, SHA-1 pinned)

| Machine | Monitor ROM | Character ROM | BASIC (`--basic`) |
|---|---|---|---|
| MZ-700 | `MZ-700/ROM/1z-013a.rom` | `MZ-700/ROM/mz700fon.int` | `MZ-700/BASIC/1Z-013B.mzf` |
| MZ-80A | `MZ-80A/ROM/SA-1510.rom` | `MZ-80A/ROM/SA-CG.rom` | `MZ-80A/BASIC/SA-5510.mzf` |
| MZ-800 | `MZ-800/ROM/MZ800.ROM` (monitor, CG, IPL and IOCS in one file) | — | `MZ-800/BASIC/1Z-016 (tape BASIC)/1Z-016.mzf` |

### Key scripts

The scripts in `keys/` feed scripted host input through the same
handlers as a real keyboard (`--keys=<file>`; the format is
documented in `KeyScript.cs`). With `--basic`, BASIC is at its Ready
prompt by frame 420 on all three machines.

## The cases

Software paths are relative to `_mz-information-repository/`.

### MZ-700

| Case | Frame | Software / input | What it covers |
|---|---|---|---|
| `mz700_monitor` | 200 | none (monitor only) | 1Z-013A monitor boot, plus its boot tone in `.snd.txt` |
| `mz700_basic` | 400 | BASIC 1Z-013B | S-BASIC boot to Ready |
| `mz700_dragon` | 900 | `MZ-700/Software/Dragon Caves (1982)(Solo Software).zip` | A large (> 25 KB) BASIC program load. Guards the cold-start Overflow fix, where long BASIC tails spilled through the VRAM window. |
| `mz700_panic` | 1200 | `MZ-700/Software/Space Panic(1983)(Solo Software).zip`; `keys/mz700_panic.keys` | A machine-code game. The script presses Space at frame 600 to leave the attract screen and start play. |
| `mz700_music` | 1400 | BASIC; `keys/mz700_music.keys` | Typed `TEMPO`/`MUSIC` with octaves, sharps, durations and a rest. The notes are checked in `.snd.txt`. |
| `mz700_typing` | 1520 | BASIC; `keys/mz700_typing.keys` | Live typing: letters, digits, shifted symbols, cursor-key editing (overtyping a stored line), and graphic characters typed in GRAPH mode |

### MZ-80A

| Case | Frame | Software / input | What it covers |
|---|---|---|---|
| `mz80a_monitor` | 200 | none (monitor only) | SA-1510 monitor boot |
| `mz80a_basic` | 400 | BASIC SA-5510 | SA-5510 boot to Ready |
| `mz80a_galactic` | 1500 | `MZ-80A/Software/GALACTIC-INVADERS-80A.mzf`; `keys/mz80a_galactic.keys` | A machine-code game. The script presses Space at frame 600 to leave the title screen, then types 1 at frame 1000 to pick NOVICE. |
| `mz80a_colditz` | 1200 | `MZ-80A/Software/COLDITZ-80A.mzf` | A BASIC cassette title through the autorun path: the emulator types `LOAD` and `RUN` into SA-5510 |
| `mz80a_music` | 1400 | BASIC; `keys/mz80a_music.keys` | As `mz700_music`, on SA-5510. Covers the pitch and note-duration fixes. |
| `mz80a_typing` | 1520 | BASIC; `keys/mz80a_typing.keys` | As `mz700_typing`, with GRPH. With the authentic inverted shift, keywords are typed in lowercase and arrive as capitals. |

### MZ-800

| Case | Frame | Software / input | What it covers |
|---|---|---|---|
| `mz800_ipl` | 200 | none (IPL only) | The IPL menu, the four synthetic test patterns, and the boot beep (`.wav`) |
| `mz800_basic` | 300 | BASIC 1Z-016 | 1Z-016 BASIC boot, with audio |
| `james` | 600 | `MZ-800/Software/Machine code games/James.mzf` | An MZ-700 title running in the MZ-800's MZ-700 mode |
| `fist` | 900 | `.../Machine code games/EXPLODING FIST.mzf` | Writes the whole scroll register block and palette slots 4+. Kept over Highway, which covers only the border. |
| `gps_turn` | 695 | `.../Machine code games/G.P.S. program.mzf` | Grand Prix Simulator II's title: palette rewritten mid-frame, line by line, synced through the CRTC status bits. The frame is chosen with the colour bars at the bottom of the screen. |
| `uridium` | 1200 | `.../Machine code games/Uridium (1990)(Hewson - Petr Patik).mzf` | 16-colour mode and a PIO PA5 (VBLANK) frame interrupt, with title music in `.wav` |
| `wheelie` | 900 | `.../Machine code games/wheelie.mzf` | Drawing with the WF PSET write mode |
| `jetpac` | 900 | `.../Machine code games/jetpac1.mzf` | The PIO VBLANK interrupt and the WF write modes. Kept over Jetman, which it overlaps. |
| `manic` | 900 | `.../Machine code games/MANIC.MZF` | Manic Miner: the RF SEARCH read mode, and the PSG title tune in `.wav` |
| `abusimbel` | 900 | `.../Machine code games/abusimbe.mzf` | Abu Simbel: the RF SEARCH read mode |
| `bruce` | 900 | `.../Machine code games/bruce-p.mzf` | Bruce Lee: general MZ-800 rendering, matched against EmuZ-800 when added |
| `chequered` | 900 | `.../Machine code games/chequered_flag.mzf` | Chequered Flag: general MZ-800 rendering, matched against EmuZ-800 when added |
| `flappy` | 900 | `.../Machine code games/flappy.mzf` | Times its game off PIT counters 1 and 2. Guards the RTC cascade fix, which had made it run at double speed. |
| `egg_basic` | 1500 | `egg.zip`, built by the harness from `MZ-800/Software/BASIC programs/EGG/EGG.BAS.mzf` and `EGG.OBJ.mzf` | A BASIC program on tape: boots BASIC and runs it with `LOAD` + `RUN`, then the program's named `LOAD` of the second file from the same tape |
| `mz800_typing` | 1520 | BASIC; `keys/mz800_typing.keys` | As `mz700_typing`, on 1Z-016 BASIC, adding ALPHA and an F-key |

## Approving goldens

Approve only after checking that a difference is intended, by
looking at the new images or listening to the audio. Then run
`--approve`, optionally with a filter for just the affected cases.
Record each approval in the log in `_regression/README.md` (date,
commit, cases and why).

- `_regression/golden/` holds the approved outputs.
- `_regression/out/` holds the latest run.

The v1.4.0 baseline is commit a80994a: all 27 cases approved on
clean `main`.

## Adding a case

1. Put any new software in `_mz-information-repository/`, under its
   machine's `Software/` folder.
2. Add a row to `CASES` in `regress.py`: name, command-line
   arguments, dump frame, and extra artefacts (`tests`, `wav` or
   `snd`). Add a key script to `keys/` if it needs input.
3. Run it a few times to confirm it is stable. Check the output by
   eye, then approve it and log the approval.
4. Add it to the tables above.
