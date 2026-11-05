"""
MZRaku golden-image regression harness.

Runs each case through `MZRaku.exe --dump=<out> --dumpframe=<N>`, then
compares the frame PNG (pixel-exact) and, where listed, the test-pattern
PNGs and the rendered-audio .wav against the approved copies.

    python tools/regress/regress.py              build, run all cases, compare
    python tools/regress/regress.py --approve    run all cases, overwrite goldens
    python tools/regress/regress.py --no-build   skip `dotnet build`
    python tools/regress/regress.py jetpac uri   only cases whose name contains a filter
    python tools/regress/regress.py --headless   run the cases through tools/MZRakuHeadless
                                                 instead of the app (same goldens)

Only this script and settings.ini are tracked. Everything it reads or
writes is local and git-ignored, because ROMs, software and screenshots
of them are Sharp / publisher copyright:

    _mz-information-repository/   ROMs, BASICs and test software (read-only)
    _regression/golden/           approved outputs
    _regression/out/              this run's outputs
    _regression/sandbox/<case>/   per-case copy of the build output, plus
                                  tools/regress/settings.ini and the ROMs

Every case runs in a fresh sandbox, so results don't depend on the
developer's bin\\Debug settings.ini, and that file is never written.

Emulation is frame-deterministic (--dump counts emulated frames), so an
unchanged build reproduces the goldens byte-for-byte. App runs are paced
in real time, hence the parallel worker pool. --headless runs use the
console runner on the core alone, unpaced; matching the app's goldens
proves the core doesn't depend on the front end. Goldens are approved
from the app only.
"""
import hashlib, os, shutil, struct, subprocess, sys, zipfile, zlib
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
BUILD = os.path.join(REPO, "bin", "Debug", "net8.0-windows")
EXE = "MZRaku.exe"
PROJECT = None   # None = the app (repo root); --headless sets the runner's project
HEADLESS = False
# --headless: the console runner (tools/MZRakuHeadless), same arguments.
HEADLESS_PROJECT = os.path.join(REPO, "tools", "MZRakuHeadless", "MZRakuHeadless.csproj")
HEADLESS_BUILD = os.path.join(REPO, "tools", "MZRakuHeadless", "bin", "Debug", "net8.0")
INFO = os.path.join(REPO, "_mz-information-repository")
REG = os.path.join(REPO, "_regression")
OUT = os.path.join(REG, "out")
GOLDEN = os.path.join(REG, "golden")
SANDBOX = os.path.join(REG, "sandbox")
MEDIA = os.path.join(SANDBOX, "_media")   # test media assembled from the repository
SETTINGS = os.path.join(HERE, "settings.ini")
KEYS = os.path.join(HERE, "keys")   # --keys= scripts (tracked)
PRESETS = os.path.join(HERE, "presets")   # --settings= INIs (tracked)
WORKERS = 4

# Sandbox path -> (repository path, SHA-1). The hashes pin the exact
# firmware the goldens were approved against; a mismatch means the
# repository changed, not the emulator.
FIRMWARE = {
    r"roms\1z-013a.rom":  ("MZ-700/ROM/1z-013a.rom",   "ef8f7399e86c1dc638a5cb83efdb73369c2b5735"),
    r"roms\mz700fon.int": ("MZ-700/ROM/mz700fon.int",  "5128ad179a702f8e0bd9910a58bad8fbe4c20167"),
    r"basic\1Z-013B.mzf": ("MZ-700/BASIC/1Z-013B.mzf", "254ab9d3b7edc921d3f02136c2c6855e79e3b4fd"),
    r"roms\SA-1510.rom":  ("MZ-80A/ROM/SA-1510.rom",   "ee63d4424db9124f564a7e90b973837f6badfc64"),
    r"roms\SA-CG.rom":    ("MZ-80A/ROM/SA-CG.rom",     "e8aefbdb48a63e5f96692af868c353ca7e1bfcd2"),
    r"basic\SA-5510.mzf": ("MZ-80A/BASIC/SA-5510.mzf", "94c507d18967c7e00228a71c712b1a61e02a6976"),
    r"roms\MZ800.ROM":    ("MZ-800/ROM/MZ800.ROM",     "950ce4b51429916f8036e41ba6130fac149b36e4"),
    r"basic\1Z-016.mzf":  ("MZ-800/BASIC/1Z-016 (tape BASIC)/1Z-016.mzf", "22fe1b6073971c2f51b6373a3ff1e5a032e5652f"),
}

# The MZ-800 ROM as EmuZ-800's three files. Cases in SPLIT_ROM_CASES get
# these in place of roms\MZ800.ROM (joined, they are MZ800.ROM).
SPLIT = "MZ-800/ROM/Split (EmuZ-800 layout)/"
MZ800_SPLIT = {
    r"roms\MZ700IPL.ROM": (SPLIT + "MZ700IPL.ROM", "2920cdaf1fda0b54b1e0e53736f936b853716988"),
    r"roms\FONT.ROM":     (SPLIT + "FONT.ROM",     "4ea887241a0b60948a86c5acab664dcfdf51101c"),
    r"roms\MZ800IPL.ROM": (SPLIT + "MZ800IPL.ROM", "07a9896c33d48c6f5e72740697874b5e60ec6f77"),
}
SPLIT_ROM_CASES = {"mz800_split_roms"}

# Cases compared against another case's golden, never approved on their
# own: the same run by a different route must give the same output.
SAME_GOLDEN = {"mz800_split_roms": "mz800_ipl"}

MZ700_SW = "MZ-700/Software/"
MZ80A_SW = "MZ-80A/Software/"
MZ800_GAMES = "MZ-800/Software/Machine code games/"
EGG = "MZ-800/Software/BASIC programs/EGG/"

# Media built from repository files. The emulator reads a zip's image
# entries in name order as one tape, so the archive layout doesn't matter.
ZIPS = {
    "egg.zip": [EGG + "EGG.BAS.mzf", EGG + "EGG.OBJ.mzf"],
}

# name, CLI args, dump frame, extra artefacts to compare.
# README.md (next to this script) describes every case: keep it in step.
#   "tests" = the four MZ-800 test-pattern PNGs, "wav" = rendered audio
#   (MZ-800), "snd" = speaker-input event log (MZ-700 / MZ-80A),
#   "save" = the .mzf a scripted SAVE wrote (compared byte for byte)
#   A path after --cassette is relative to the repository (or names a
#   ZIPS entry), as is one after --insert= (File -> Insert cassette);
#   --keys=<file> names a scripted-input file in keys/
#   (format: KeyScript.cs); --settings=<file> names an INI in presets/,
#   copied into the sandbox and run read-only (the case fails if it or
#   the sandbox's settings.ini is written).
CASES = [
    # --- MZ-700 ---
    ("mz700_monitor",  ["--mz700"],                                   200, ["snd"]),
    ("mz700_basic",    ["--mz700", "--basic"],                        400, ["snd"]),
    ("mz700_dragon",   ["--mz700", "--cassette", MZ700_SW + "Dragon Caves (1982)(Solo Software).zip"], 900, ["snd"]),   # large BASIC load (cold-start Overflow)
    ("mz700_panic",    ["--mz700", "--cassette", MZ700_SW + "Space Panic(1983)(Solo Software).zip", "--keys=mz700_panic.keys"], 1200, ["snd"]),   # Space starts a game
    ("mz700_music",    ["--mz700", "--basic", "--keys=mz700_music.keys"], 1400, ["snd"]),
    ("mz700_typing",   ["--mz700", "--basic", "--keys=mz700_typing.keys"], 1520, ["snd"]),
    ("mz700_insert",   ["--mz700", "--insert=" + MZ700_SW + "Space Panic(1983)(Solo Software).zip", "--keys=mz700_insert.keys"], 600, ["snd"]),   # Insert cassette + monitor L
    ("mz700_save",     ["--mz700", "--basic", "--keys=mz700_save.keys"], 1100, ["snd", "save"]),   # S-BASIC SAVE: .mzf written, back at Ready
    ("mz700_insert_basic", ["--mz700", "--basic", "--insert=" + MZ700_SW + "Dragon Caves (1982)(Solo Software).zip", "--keys=mz700_insert_basic.keys"], 900, ["snd"]),   # Insert cassette + S-BASIC LOAD / RUN
    # --- MZ-80A ---
    ("mz80a_monitor",  ["--mz80a"],                                   200, ["snd"]),
    ("mz80a_basic",    ["--mz80a", "--basic"],                        400, ["snd"]),
    ("mz80a_galactic", ["--mz80a", "--cassette", MZ80A_SW + "GALACTIC-INVADERS-80A.mzf", "--keys=mz80a_galactic.keys"], 1500, ["snd"]),   # Space starts a game
    ("mz80a_colditz",  ["--mz80a", "--cassette", MZ80A_SW + "COLDITZ-80A.mzf"], 1200, ["snd"]),   # BASIC autorun: inject + typed RUN
    ("mz80a_cricket",  ["--mz80a", "--cassette", MZ80A_SW + "cricket.mzf"], 600, ["snd"]),   # line 1 GOSUB: relinked program lines
    ("mz80a_music",    ["--mz80a", "--basic", "--keys=mz80a_music.keys"], 1400, ["snd"]),
    ("mz80a_typing",   ["--mz80a", "--basic", "--keys=mz80a_typing.keys"], 1520, ["snd"]),
    ("mz80a_settings", ["--mz80a", "--settings=mz80a_white.ini"],    200, []),   # --settings: white screen, nothing written
    # --- MZ-800 ---
    ("mz800_ipl",      ["--mz800"],                                   200, ["tests", "wav"]),
    ("mz800_split_roms", ["--mz800"],                                 200, ["tests", "wav"]),   # EmuZ-800's three ROM files; mz800_ipl's golden
    ("mz800_basic",    ["--mz800", "--basic"],                        300, ["wav"]),
    ("james",          ["--mz800", "--cassette", MZ800_GAMES + "James.mzf"],        600, []),
    ("fist",           ["--mz800", "--cassette", MZ800_GAMES + "EXPLODING FIST.mzf"], 900, []),   # over highway: whole scroll block + palette 4+
    ("gps_turn",       ["--mz800", "--cassette", MZ800_GAMES + "G.P.S. program.mzf"], 695, []),   # raster bars at bottom
    ("uridium",        ["--mz800", "--cassette", MZ800_GAMES + "Uridium (1990)(Hewson - Petr Patik).mzf"], 1200, ["wav"]),
    ("wheelie",        ["--mz800", "--cassette", MZ800_GAMES + "wheelie.mzf"],      900, []),
    ("jetpac",         ["--mz800", "--cassette", MZ800_GAMES + "jetpac1.mzf"],      900, []),   # over jetman: PIO VBLANK interrupt + WF modes
    ("manic",          ["--mz800", "--cassette", MZ800_GAMES + "MANIC.MZF"],        900, ["wav"]),
    ("abusimbel",      ["--mz800", "--cassette", MZ800_GAMES + "abusimbe.mzf"],     900, []),
    ("bruce",          ["--mz800", "--cassette", MZ800_GAMES + "bruce-p.mzf"],      900, []),
    ("chequered",      ["--mz800", "--cassette", MZ800_GAMES + "chequered_flag.mzf"], 900, []),
    ("flappy",         ["--mz800", "--cassette", MZ800_GAMES + "flappy.mzf"],       900, []),
    ("egg_basic",      ["--mz800", "--cassette", "egg.zip"],          1500, []),   # BASIC LOAD + named LOAD of 2nd file
    ("mz800_typing",   ["--mz800", "--basic", "--keys=mz800_typing.keys"], 1520, []),
    ("mz800_save",     ["--mz800", "--basic", "--keys=mz800_save.keys"], 1100, ["save"]),   # 1Z-016 SAVE: .mzf written, back at Ready
]
TEST_SUFFIXES = [".test.png", ".test640.png", ".testscroll.png", ".testsplit.png"]


def sha1(p):
    return hashlib.sha1(open(p, "rb").read()).hexdigest()


def check_sources():
    """Fail early, naming the file, if the build or repository isn't as expected."""
    problems = []
    if not os.path.exists(os.path.join(BUILD, EXE)):
        problems.append(f"no build at {BUILD}")
    for dst, (src, want) in list(FIRMWARE.items()) + list(MZ800_SPLIT.items()):
        p = os.path.join(INFO, src)
        if not os.path.exists(p):
            problems.append(f"missing {p}")
        elif sha1(p) != want:
            problems.append(f"SHA-1 mismatch {p} (expected {want})")
    for _, args, _, _ in CASES:
        for i, a in enumerate(args):
            if i > 0 and args[i - 1] == "--cassette" and a not in ZIPS \
                    and not os.path.exists(os.path.join(INFO, a)):
                problems.append(f"missing {os.path.join(INFO, a)}")
            elif a.startswith("--insert=") and not os.path.exists(os.path.join(INFO, a[len("--insert="):])):
                problems.append(f"missing {os.path.join(INFO, a[len('--insert='):])}")
            elif a.startswith("--keys=") and not os.path.exists(os.path.join(KEYS, a[len("--keys="):])):
                problems.append(f"missing {os.path.join(KEYS, a[len('--keys='):])}")
            elif a.startswith("--settings=") and not os.path.exists(os.path.join(PRESETS, a[len("--settings="):])):
                problems.append(f"missing {os.path.join(PRESETS, a[len('--settings='):])}")
    for parts in ZIPS.values():
        problems += [f"missing {os.path.join(INFO, p)}" for p in parts
                     if not os.path.exists(os.path.join(INFO, p))]
    if problems:
        sys.exit("regress: cannot run\n  " + "\n  ".join(problems))


def build_media():
    os.makedirs(MEDIA, exist_ok=True)
    for name, parts in ZIPS.items():
        with zipfile.ZipFile(os.path.join(MEDIA, name), "w") as z:
            for p in parts:
                z.write(os.path.join(INFO, p), os.path.basename(p))


def make_sandbox(name):
    """A fresh run directory: the build's binaries, fixed settings, ROMs."""
    box = os.path.join(SANDBOX, name)
    shutil.rmtree(box, ignore_errors=True)
    os.makedirs(box)
    # Binaries only; the developer's settings.ini, ROMs and games stay behind.
    for f in os.listdir(BUILD):
        if os.path.splitext(f)[1].lower() in (".exe", ".dll", ".json", ".pdb"):
            shutil.copy2(os.path.join(BUILD, f), box)
    firmware = dict(FIRMWARE)
    if name in SPLIT_ROM_CASES:
        del firmware[r"roms\MZ800.ROM"]
        firmware.update(MZ800_SPLIT)
    for dst, (src, _) in firmware.items():
        target = os.path.join(box, dst)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copyfile(os.path.join(INFO, src), target)
    shutil.copyfile(SETTINGS, os.path.join(box, "settings.ini"))
    return box


def resolve(args, box):
    out = []
    for i, a in enumerate(args):
        if i > 0 and args[i - 1] == "--cassette":
            a = os.path.join(MEDIA, a) if a in ZIPS else os.path.join(INFO, a)
        elif a.startswith("--insert="):
            a = "--insert=" + os.path.join(INFO, a[len("--insert="):])
        elif a.startswith("--keys="):
            a = "--keys=" + os.path.join(KEYS, a[len("--keys="):])
        elif a.startswith("--settings="):
            # A copy in the sandbox, so a write would show (settings_written).
            preset = a[len("--settings="):]
            shutil.copyfile(os.path.join(PRESETS, preset), os.path.join(box, preset))
            a = "--settings=" + os.path.join(box, preset)
        out.append(a)
    return out


def settings_written(box, args):
    """--settings= is read-only: name any INI the run changed, else None."""
    pairs = [(os.path.join(box, "settings.ini"), SETTINGS)]
    pairs += [(os.path.join(box, a[len("--settings="):]), os.path.join(PRESETS, a[len("--settings="):]))
              for a in args if a.startswith("--settings=")]
    if len(pairs) == 1:
        return None
    changed = [os.path.basename(run) for run, src in pairs
               if open(run, "rb").read() != open(src, "rb").read()]
    return f"SETTINGS WRITTEN: {', '.join(changed)}" if changed else None


def collect_save(box, base):
    """A scripted SAVE writes saves/<name>.mzf in the sandbox; keep the first."""
    saves = os.path.join(box, "saves")
    if os.path.isdir(saves):
        files = sorted(f for f in os.listdir(saves) if f.lower().endswith(".mzf"))
        if files:
            shutil.copyfile(os.path.join(saves, files[0]), base + ".save.mzf")


def run_case(case):
    name, args, frame, _ = case
    base = os.path.join(OUT, name)
    for f in os.listdir(OUT):
        if f.startswith(name + "."):
            os.remove(os.path.join(OUT, f))
    box = make_sandbox(name)
    cmd = [os.path.join(box, EXE)] + resolve(args, box) + [f"--dump={base}", f"--dumpframe={frame}"]
    if HEADLESS:
        # Unpaced, so allow the real-time length plus start-up. Its
        # status lines are captured; errors are shown if the case fails.
        try:
            r = subprocess.run(cmd, timeout=frame / 50 + 30, cwd=box,
                               capture_output=True, text=True)
        except subprocess.TimeoutExpired:
            return name, "TIMEOUT"
        collect_save(box, base)
        if os.path.exists(base + ".png"):
            return name, settings_written(box, args) or "ok"
        err = r.stderr.strip().replace("\n", " ")
        return name, f"NO OUTPUT (exit {r.returncode}{': ' + err[:200] if err else ''})"
    # Open minimised without taking focus, so a run doesn't cover the
    # screen or steal keystrokes typed elsewhere (dump runs ignore the
    # real keyboard anyway).
    si = subprocess.STARTUPINFO()
    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 7   # SW_SHOWMINNOACTIVE
    try:
        subprocess.run(cmd, timeout=frame / 50 * 3 + 60, cwd=box, startupinfo=si)
    except subprocess.TimeoutExpired:
        return name, "TIMEOUT"
    collect_save(box, base)
    if not os.path.exists(base + ".png"):
        return name, "NO OUTPUT"
    return name, settings_written(box, args) or "ok"


def png_pixels(path):
    """Decode an 8-bit RGB/RGBA PNG to (w, h, bytes-per-pixel, rows)."""
    d = open(path, "rb").read()
    i, idat = 8, b""
    while i < len(d):
        ln = struct.unpack(">I", d[i:i + 4])[0]
        typ, chunk = d[i + 4:i + 8], d[i + 8:i + 8 + ln]
        if typ == b"IHDR":
            w, h, _, ct = struct.unpack(">IIBB", chunk[:10])
        elif typ == b"IDAT":
            idat += chunk
        i += 12 + ln
    raw = zlib.decompress(idat)
    bpp = 4 if ct == 6 else 3
    stride = w * bpp + 1
    rows, prev = [], bytearray(w * bpp)
    for y in range(h):
        f = raw[y * stride]
        line = bytearray(raw[y * stride + 1:(y + 1) * stride])
        for x in range(len(line)):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, bpp, rows


def compare_png(cur, gold):
    if not os.path.exists(gold):
        return "NO GOLDEN"
    if not os.path.exists(cur):
        return "MISSING"
    if open(cur, "rb").read() == open(gold, "rb").read():
        return None
    w1, h1, bpp, r1 = png_pixels(cur)
    w2, h2, _, r2 = png_pixels(gold)
    if (w1, h1) != (w2, h2):
        return f"SIZE {w1}x{h1} vs {w2}x{h2}"
    diffs = [(x, y) for y in range(h1) for x in range(w1)
             if r1[y][x * bpp:x * bpp + 3] != r2[y][x * bpp:x * bpp + 3]]
    if not diffs:
        return None
    xs = [p[0] for p in diffs]; ys = [p[1] for p in diffs]
    return f"{len(diffs)} px differ, box x{min(xs)}-{max(xs)} y{min(ys)}-{max(ys)}"


def compare_text(cur, gold):
    if not os.path.exists(gold):
        return "NO GOLDEN"
    if not os.path.exists(cur):
        return "MISSING"
    a = open(cur, encoding="utf-8").read().splitlines()
    b = open(gold, encoding="utf-8").read().splitlines()
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            return f"line {i + 1}: {x!r} vs golden {y!r}"
    if len(a) != len(b):
        return f"{len(a)} lines vs golden {len(b)}"
    return None


def md5(p):
    return hashlib.md5(open(p, "rb").read()).hexdigest() if os.path.exists(p) else None


def main():
    global BUILD, EXE, PROJECT, HEADLESS
    argv = sys.argv[1:]
    approve = "--approve" in argv
    filters = [a for a in argv if not a.startswith("--")]
    cases = [c for c in CASES if not filters or any(f in c[0] for f in filters)]
    workers = WORKERS
    if "--headless" in argv:
        if approve:
            sys.exit("regress: goldens are approved from the app; drop --headless")
        HEADLESS, BUILD, EXE, PROJECT = True, HEADLESS_BUILD, "MZRakuHeadless.exe", HEADLESS_PROJECT
        workers = os.cpu_count() or WORKERS   # not paced, so one per core

    if "--no-build" not in argv:
        r = subprocess.run(["dotnet", "build"] + ([PROJECT] if PROJECT else [])
                           + ["-c", "Debug", "--nologo", "-v", "q"], cwd=REPO,
                           capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout[-3000:]); sys.exit("build failed")

    check_sources()
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(GOLDEN, exist_ok=True)
    build_media()

    with ThreadPoolExecutor(workers) as pool:
        status = dict(pool.map(run_case, cases))

    failed = 0
    for name, _, frame, extras in cases:
        base_out, base_gold = os.path.join(OUT, name), os.path.join(GOLDEN, SAME_GOLDEN.get(name, name))
        artefacts = ([".png"] + (TEST_SUFFIXES if "tests" in extras else [])
                     + ([".wav"] if "wav" in extras else []) + ([".snd.txt"] if "snd" in extras else [])
                     + ([".save.mzf"] if "save" in extras else []))
        if status[name] != "ok":
            print(f"FAIL  {name:12} {status[name]}"); failed += 1; continue
        if approve and name in SAME_GOLDEN:
            print(f"SKIPPED {name} (compared against {SAME_GOLDEN[name]}'s golden)")
            continue
        if approve:
            for s in artefacts:
                shutil.copyfile(base_out + s, base_gold + s)
            print(f"APPROVED {name}")
            continue
        problems = []
        for s in artefacts:
            if s == ".wav":
                if md5(base_out + s) != md5(base_gold + s):
                    problems.append(f"{s}: audio differs")
            elif s == ".save.mzf":
                if not os.path.exists(base_gold + s):
                    problems.append(f"{s}: NO GOLDEN")
                elif md5(base_out + s) != md5(base_gold + s):
                    problems.append(f"{s}: " + ("MISSING" if not os.path.exists(base_out + s) else "saved file differs"))
            elif s == ".snd.txt":
                r = compare_text(base_out + s, base_gold + s)
                if r: problems.append(f"{s}: {r}")
            else:
                r = compare_png(base_out + s, base_gold + s)
                if r: problems.append(f"{s}: {r}")
        if problems:
            failed += 1
            print(f"DIFF  {name:12} " + "; ".join(problems))
        else:
            print(f"ok    {name:12}")
    if not approve:
        print(f"\n{len(cases) - failed}/{len(cases)} cases match golden")
    sys.exit(1 if failed and not approve else 0)


if __name__ == "__main__":
    main()
