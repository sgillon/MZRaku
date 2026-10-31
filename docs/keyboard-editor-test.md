# Keyboard editor — development test sheet

Manual checks accumulated step by step as Phase A of the keyboard-map
editor work lands. Run the relevant section after each commit; add new
checks as new behaviour ships. Once Phase A is complete the keepers
get distilled into `docs/release-check.md`.

Conventions:
- "READY prompt" means launched with `--basic` and the BASIC `READY`
  banner is showing.
- Codes in this sheet are MZ-700 display codes (not ASCII). Position
  in the Font Sheet implies the code: `row * 16 + col` within the bank.

---

## A1 — Font Sheet diagnostic

- [ ] **Debug → Font Sheet…** opens without stealing focus from a
      typing-in-progress emulator.
- [ ] Bank 0 (top half) and Bank 1 (bottom half) both render; each is
      a 16×16 grid with column headers `0..F` and row headers `0_..F_`.
- [ ] Glyphs in bank 0 are visually identical to what the same display
      code produces on the live MZ screen during normal use.
- [ ] **Reload** button re-renders without artefacts (cache flush works).

## A2 — MzGlyphCatalog

Data-only; no direct manual check. Verified indirectly when the matrix
grid lands at A6.

## A2.5 / A2.6 — ROM key tables + click-to-input

Boot to a READY prompt before each click test.

- [ ] **Click `A`** (bank 0 row 4 col 1): status reports
      `Typed bank 0 code $41 via (4,7).`; `A` appears at the cursor.
- [ ] **Click `*`** (bank 0 row 6 col B): status reports shift-typed;
      `*` appears at the cursor.
- [ ] **Click `#`** (bank 0 row 2 col 3): status reports shift-typed;
      `#` appears at the cursor.
- [ ] **Click a digit** (e.g. `5` at bank 0 row 3 col 5): appears at
      the cursor.
- [ ] **Click a graphics glyph** (e.g. racing car at bank 0 row C col 9):
      status reads `… isn't reachable from the keyboard.`; nothing
      appears in BASIC. *(Proper graphics-glyph typing needs the
      GRAPH-mode ROM tables wired up — out of scope for Phase A.)*
- [ ] Same behaviour with the main window in **GRAPH mode** as in
      **ALPHA mode** (mode keys themselves should not be re-bindable
      from the Font Sheet — they're filtered as special-key slots).
- [ ] Status text correctly reflects shift state in the parenthesised
      slot (`shift+(row,col)` for shifted entries).

## A3 — CharMapOverrides

Data-only plumbing; no direct manual check. Verified at A4 via the INI
round-trip (write an override, restart, confirm it survives and is
honoured by `CharMap.TryLookup`).

## A4 — INI persistence and section retrofit

**File shape (run once, then inspect `settings.ini` next to the exe):**

- [ ] All five sections present in order: `[Display]`, `[Roms]`,
      `[Joystick]`, `[KeyOverrides]`, `[CharMap]`.
- [ ] Every section has a comment block immediately under its header
      describing its values, allowed ranges, and what each token means.
      A fresh reader should understand the file without opening source
      code.
- [ ] `[KeyOverrides]` shift documents `t / f / -` (pass-through is
      meaningful here); `[CharMap]` shift documents only `t / f`
      (pass-through is explicitly noted as not applicable).

**Override round-trip (manual edit, then restart):**

- [ ] Add a single line to `[CharMap]`: `0061=6,0,f   ; 'a'` (rebinds
      PC `a` to slot (6,0), which is `.`).
- [ ] Restart the emulator and bring up the BASIC `READY` prompt.
- [ ] Press `a` on the PC keyboard. A `.` (period) should appear at
      the cursor, not `A`.
- [ ] Remove the line from `[CharMap]` and restart. Pressing `a` now
      produces `A` again (default behaviour restored).

**Inline-comment safety:**

- [ ] The `; 'a'` glyph comment on the override line does not break
      parsing — the entry loads correctly.

**Upgrade-from-older-INI check:**

- [ ] Take an older `settings.ini` (e.g. from before A4) that lacks
      the `[CharMap]` section. Launch. The file is rewritten with the
      new comment blocks and `[CharMap]` present; existing values
      (display scale, joystick buttons, key overrides) are preserved.

## A5 — KeyCaptureControl

Open via **Debug → Key Capture Test…**. The capture box at the top of
the dialog highlights light-yellow when focused; the details panel
below shows the parsed `KeyData`, bare VK, modifier mask, resolved
char (if any), and the L/R-ambiguity flag.

**Printable keys (KeyDown → KeyPress path):**

- [ ] Press `a` — captured as `A` with char `'a'` (U+0061). No
      ambiguity warning.
- [ ] Press `Shift+a` — captured as `A, Shift` with char `'A'`
      (U+0041).
- [ ] Press `1`, then `Shift+1` — capture switches between `'1'` and
      `'!'` correctly.

**Non-printable keys (KeyDown-only path):**

- [ ] **Esc** — captured (would normally close a dialog; doesn't here).
      Char field reads "(none)".
- [ ] **Enter** — captured; doesn't act as the dialog's default-button.
- [ ] **Tab** — captured; does NOT navigate to the next control.
- [ ] **Arrow keys** — all four captured individually; do not move
      focus.
- [ ] **F1–F12** — each captured.
- [ ] **Insert / Delete / Home / End / PgUp / PgDn** — each captured.

**Modifier handling:**

- [ ] Press and hold **Ctrl** alone — capture box reads
      `Ctrl held — release to bind alone, or press another key`; no
      `Captured` event yet.
- [ ] **Release Ctrl** without pressing anything else (tap-to-bind) —
      captured as `Ctrl`; ambiguity note appears in orange.
- [ ] With Ctrl still held, press `g` — captured as `Ctrl+G` with a
      char value (PC Ctrl+G resolves to U+0007).
- [ ] Same tap-to-bind / modifier+letter check for **Shift** and **Alt**.
- [ ] **Alt+letter** captures without a Windows error ding — confirms
      the host form's `ProcessCmdKey` Alt-forward is wired.

**Ambiguity warning (decision #8):**

- [ ] **Tap Ctrl alone** → orange note `Left/Right Ctrl share this
      binding — both will fire.`
- [ ] **Tap Shift alone** → orange note for Shift.
- [ ] **Tap Alt alone** → orange note for Alt.
- [ ] **Ctrl+G** → orange note `Ctrl modifier bound generically — left
      and right both trigger.`
- [ ] **Shift+Ctrl+G** → orange note covers `Ctrl / Shift` together.
- [ ] Non-modifier keys alone (letters, digits, function keys,
      cursors) do NOT show the orange ambiguity note.

**Friendly names:**

- [ ] **Page Up / Page Down** show as `Page Up` / `Page Down` (not the
      WinForms `Prior` / `Next` names).
- [ ] Tapping **Left Ctrl** specifically: capture line reads `Ctrl`
      (normalised to generic); the orange ambiguity note explains why.

**Reset button:**

- [ ] After any capture, click **Reset** — capture box returns to the
      `Click here, then press a key…` prompt; details panel reads
      `Capture cleared.`; focus is returned to the capture box.

**Close behaviour:**

- [ ] **Close** button closes the dialog; emulator main window still
      responds to keystrokes afterwards.

## A6 — KeyboardMatrixGrid

Open via **Debug → Keyboard Matrix…**. A 10×8 grid (rows 0–9, cols 0–7)
appears in a non-stealing tool window so the emulator main window can
keep keyboard focus.

**Layout and labels:**

- [ ] Column headers `0..7` across the top, row headers `0..9` down the
      left.
- [ ] Each cell shows: slot coord `(r,c)` top-left, the MZ glyph(s)
      centred, the PC keystroke(s) at the bottom.
- [ ] For slots with both unshifted and shifted glyphs (e.g. row 5
      digits `1`/`!`), the unshifted is on the left in black, the
      shifted on the right in grey.
- [ ] Special slots show their friendly label centred instead of a
      glyph: `Enter`, `GRAPH`, `ALPHA`, `←`, `→`, `↓`, `↑`, `DEL`,
      `INST`, `SHIFT`, `BREAK`, `CTRL`, `F1`–`F4`.

**Sanity-check a handful of cells (consult `Core/MZ700/Mz700CharMap.cs` for
the canonical positions):**

- [ ] `(4,7)` shows `A` glyph and PC binding `A a`.
- [ ] `(1,6)` shows `Z` glyph and PC binding `Z z`.
- [ ] `(5,7)` shows `1` / `!` and PC binding `1 !`.
- [ ] `(0,2)` shows `;` / `+` and PC binding `; +`.
- [ ] `(0,1)` shows `:` / `*` and PC binding `: *`.
- [ ] `(6,4)` shows space (often blank-looking) and PC binding shows
      a space.
- [ ] `(0,0)` shows `Enter` label and PC binding `Enter`.
- [ ] `(8,7)` shows `BREAK` label and PC binding `Esc (BREAK)`.

**Last-matched highlight (boot to a READY prompt first):**

- [ ] Press `A` on the PC keyboard — slot `(4,7)` pulses pale yellow
      and reverts to white within ~100 ms after key-up.
- [ ] Press `Shift+1` — slot `(5,7)` pulses yellow (the shifted MZ
      glyph `!` is the live one).
- [ ] Press a cursor key — slot `(7,2)`/`(7,3)`/`(7,4)`/`(7,5)` pulses.
- [ ] Hold a key — the slot stays yellow for as long as the key is
      held.

**Override indicator:**

- [ ] With a `[CharMap]` entry like `0061=6,0,f   ; 'a'` in
      `settings.ini`, slot `(6,0)` (where `.` lives) has an orange
      2px border and the PC binding line includes `a` alongside `.`.
- [ ] Remove the override line and restart — the orange border at
      `(6,0)` is gone.

**Refresh:**

- [ ] Opening the form a second time (close and reopen) renders the
      same content without artefacts.
- [ ] The emulator main window remains keyboard-focused after the
      matrix opens (it doesn't steal focus on Show).

**Coverage tracking (top-bar checkbox + Reset button):**

- [ ] **Show coverage** unchecked by default; no green chyrons appear
      regardless of which keys have been pressed.
- [ ] Press `A`, `B`, `C` on the PC keyboard, then tick **Show
      coverage** — small green triangles appear in the top-right corner
      of slots `(4,7)`, `(4,6)`, `(4,5)`.
- [ ] Press more keys with **Show coverage** still ticked — new green
      chyrons appear immediately on those slots.
- [ ] Untick **Show coverage** — chyrons hide; press more keys; tick
      again — accumulated coverage is still there (tracking continues
      while the checkbox is unticked).
- [ ] Click **Reset** — all chyrons disappear; pressing further keys
      starts a fresh coverage trail.
- [ ] Hold a key while clicking Reset, then release and re-press the
      same key — the slot gains a fresh chyron on the re-press (Reset
      does not retro-mark currently-held keys).
- [ ] Close the form and reopen — coverage is empty (each open is a
      fresh tracking session).
- [ ] Chyron stacks cleanly with the orange override border (e.g.
      with a `[CharMap]` override on `(6,0)`, the slot shows both the
      orange border and a green chyron once pressed).

## A7 — Settings → Keyboard tab

Open via **File → Settings…** (Ctrl+S) and switch to the new **Keyboard**
tab.

**Layout:**

- [ ] Dialog opens at the grown size (740×920) — Display, ROMs,
      Joystick tabs still fit comfortably; Keyboard tab fits the matrix
      plus an overrides list below.
- [ ] Keyboard tab shows the same matrix grid as **Debug → Keyboard
      Matrix…** at the top (10 rows × 8 cols, coords + glyphs +
      bindings, orange border on overridden slots).
- [ ] Below the matrix: a labelled section "Active overrides
      (read-only…)" with a four-column ListView (`Layer`, `PC trigger`,
      `MZ slot`, `Shift`).

**Overrides ListView:**

- [ ] With no overrides in `settings.ini`, the ListView shows a single
      placeholder row `— (no overrides set) — —` in grey.
- [ ] Add `0061=6,0,f   ; 'a'` under `[CharMap]` in `settings.ini`,
      restart, reopen Settings → Keyboard tab. The ListView shows a
      `CharMap` row: `'a' (U+0061)` / `(6,0)` / `unshifted`.
- [ ] Add a `[KeyOverrides]` entry such as `F5=9,3,-`, restart, reopen
      Settings → Keyboard. The ListView shows a `Key` row: `F5` /
      `(9,3)` / `pass-through`.
- [ ] Both rows appear together when both overrides are present.
- [ ] CharMap rows sort ahead of Key rows; within each layer entries
      are sorted (CharMap by codepoint, Key by VK name).

**Live behaviour:**

- [ ] Switching between tabs leaves no rendering artefacts in the
      matrix.
- [ ] OK / Cancel / Apply still work for the existing tabs (Display
      scale change still applies, etc.) — no regression from the
      Keyboard tab addition.
- [ ] Closing the dialog (OK / Cancel / X) disposes the refresh timer
      cleanly — opening Settings twice in succession works without
      timer leaks or exceptions.

## A8 — KeyBindingEditorForm

Open via **File → Settings…** → **Keyboard** tab. The matrix grid in
this tab is now clickable; the standalone **Debug → Keyboard Matrix…**
window stays read-only.

**Cursor and hit-testing:**

- [ ] Hovering over a matrix cell in the Settings tab shows a hand
      cursor; hovering over the headers or padding shows the default
      cursor.
- [ ] **Debug → Keyboard Matrix…** (the diagnostic window) shows the
      default cursor over cells — no editor opens on click there.

**Opening the editor — printable slots:**

- [ ] Click slot `(4,7)` (the `A` cell). The editor opens titled
      `Bind PC key to MZ slot`, header reads
      `Target: MZ slot (4,7) producing 'A' (unshifted)`.
- [ ] Click the right half of slot `(5,7)` (the `!` half). Header reads
      `Target: MZ slot (5,7) producing '!' (shifted)`.
- [ ] Click the left half of slot `(5,7)` (the `1` half). Header reads
      `Target: MZ slot (5,7) producing '1' (unshifted)`.
- [ ] Click slot `(0,1)` left half (`:`). Header reads
      `Target: MZ slot (0,1) producing ':' (unshifted)`.

**Opening the editor — special slots:**

- [ ] Click slot `(0,0)` (Enter). Header reads
      `Target: MZ slot (0,0) — Enter (unshifted)`.
- [ ] Click slot `(7,2)` (a cursor key). Header reads
      `Target: MZ slot (7,2) — <cursor label> (unshifted)`.

**Capture + Save (printable char):**

- [ ] In the editor opened on `(0,1)` (the `:` / `*` cell), capture
      `;` on the PC keyboard. Status line reads
      `Will bind ';' (U+003B) → (0,1) — default maps to (0,2) unshifted.`
      Save button becomes enabled.
- [ ] Click **Save**. Editor closes. Matrix grid now shows slot `(0,1)`
      with an orange override border and the PC binding line including
      `;`. The overrides ListView shows a `CharMap` row:
      `';' (U+003B)` / `(0,1)` / `unshifted`.
- [ ] Bring up a BASIC `READY` prompt (Settings still open is fine —
      close the dialog first if it grabs keystrokes) and press `;` on
      the PC keyboard. The MZ-700 produces `:` (the `(0,1)` unshifted
      glyph), not `;`.

**Capture + Save (non-character VK refused):**

- [ ] Click any printable cell, then press **F5** in the capture box.
      Status line is empty; the orange Phase-B note appears explaining
      that non-character keys aren't editable in Phase A. Save button
      stays disabled.
- [ ] Same with **Tab**, **Esc**, **Enter**, **Up arrow**, **Home**.
- [ ] Tap and release **Shift** alone. Same Phase-B refusal (modifiers
      are KeyOverrides territory).

**Advanced expander:**

- [ ] The MzShift checkbox is hidden by default. Click **Advanced ▾** —
      the panel expands, button changes to **Advanced ▴**, the checkbox
      is visible with its initial state matching the half clicked.
- [ ] Click **Advanced ▴** — panel collapses.
- [ ] Open the editor by left-clicking slot `(5,7)` (the `1` half).
      Expand Advanced, tick the MzShift checkbox. Capture `?`. Save.
      The overrides ListView shows the new entry as `shifted`.

**Re-bind existing override:**

- [ ] With the `;` → `(0,1)` override from the earlier step in place,
      click slot `(0,2)` (the original home of `;`). Capture `;`.
      Status line reads
      `... currently overridden to (0,1) unshifted.`
- [ ] Click **Save**. ListView updates — the override now points to
      `(0,2)` (back to the original slot). Live re-bind worked.

**Cancel / dismiss:**

- [ ] Open the editor, capture a char, click **Cancel** — editor closes,
      matrix and ListView unchanged.
- [ ] Open the editor, capture a char, press **Esc** — editor closes
      without saving.
- [ ] Open the editor, capture a char, press **Enter** — Save fires
      (Save is the AcceptButton).

**Persistence:**

- [ ] After a Save and dialog **Apply** / **OK**, the override is
      written to `settings.ini` under `[CharMap]` (4-digit hex codepoint
      with trailing glyph comment).
- [ ] After a Save followed by dialog **Cancel**, the override is
      *live* for this session but not persisted — restart and the
      binding is gone.

**Visual hint of editability:**

- [ ] The Debug → Keyboard Matrix… diagnostic still uses the default
      cursor over cells (no subscriber → no hand cursor → no editor).

## A9 — Reset buttons

*(to be added)*

## A10 — Apply safety gate

*(to be added)*

## A11 — Import / Export `.mzkbd`

*(to be added)*

## A12 — Docs

*(to be added)*
