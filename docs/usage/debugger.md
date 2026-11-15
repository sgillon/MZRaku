# Debugger

`Debug > Debugger…` (Ctrl+D) opens a debugger window. It provides CPU
execution control and inspection alongside the live emulator.

- **Pause / Resume** (F5), **Step** one instruction (F10), **Step
  Frame** (F11), and **Reset**. Pausing freezes the CPU between
  instructions while the screen keeps refreshing — nothing blocks, so
  the emulator and debugger stay responsive.
- A live **Z80 register view**: `PC`, `SP`, the main and alternate
  register pairs, `IX`/`IY`, `I`/`R`/`IM`, the interrupt flip-flops and
  halt state, decoded flags, and the total cycle count.
- A **disassembly pane** with PC highlighting (yellow, `>` marker) and
  breakpoint highlighting (pink, `*` marker). **Double-click a line to
  toggle a breakpoint** at that address. *Goto $* jumps anywhere in the
  64K space; *Follow PC* (on by default) keeps the current instruction
  on screen while paused. Up/Down, PgUp/PgDn and the mouse wheel
  navigate; Home re-centres on PC. Manual scroll switches Follow PC
  off.
- An address-based **breakpoint manager**: enter a hex address to add a
  breakpoint, or just use the double-click in the disassembly pane.
  Execution stops with `PC` parked on the breakpointed instruction.

BASIC-aware panes (program listing, current line, variable table) are
planned for later — see [Planned future work](../../README.md#planned-future-work).

See also: [Memory viewer](memory-viewer.md) — the natural companion to
the debugger.
