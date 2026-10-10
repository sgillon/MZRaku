# Joystick

The emulator emulates the **Sharp MZ-1X03** dual-joystick interface,
fed from any Windows-recognised game controller. Up to two controllers
are supported (slot 0 → MZ stick 1, slot 1 → MZ stick 2).

The input bridge uses the WinMM `joyGetPosEx` API rather than XInput,
so non-XInput pads (older PC gamepads, USB SNES adapters, bare PS3/PS4
pads, etc.) are picked up too. When a controller is connected the
status bar shows e.g. `Joy: 1[X128 Y128]`; without a controller, $E008
returns "idle / not pressed" so games like Space Panic boot normally.

- Stick axes drive the 555-monostable pulses on $E008 bits 1-4 during
  the visible portion of the frame. Pulse-low duration is calibrated
  against Space Panic's read routine — full-deflection reads as 0/255,
  centred reads as 128.
- The POV hat (D-pad on most pads) overrides the analog axes when
  held, giving clean 0 / 128 / 255 quantisation for BASIC `JOY()`-style
  reads.
- Buttons 1 and 2 map to SW1 and SW2 on each stick (active-low during
  vertical blanking). The PC gamepad button index that drives each is
  configurable via the `[Joystick]` section of `settings.ini`:

  ```ini
  [Joystick]
  Button1=0   ; PC gamepad button index for MZ SW1 (defaults to 0)
  Button2=1   ; PC gamepad button index for MZ SW2 (defaults to 1)
  ```

  Set an out-of-range index (e.g. `Button2=99`) to leave that MZ
  button permanently unpressed. Both emulated sticks share the same
  mapping; per-slot mappings will follow if needed.

The repository ships with a BASIC test program at `games/joytest.bas`
(plus a precompiled cassette at `games/joytest.mzf`) that draws a `+`
on screen tracking stick 1 — handy for confirming your controller is
plumbed through correctly.

## MZ-800

On the MZ-800 the same gamepads drive the machine's two digital
joystick ports (the MZ-1X16) instead. A direction counts once the
stick is a quarter of the way from centre, and `Button1` / `Button2`
are the two triggers. See [MZ-800](mz800.md#joystick). The MZ-80A has
no joystick.

The relevant code: `Hardware/Mz800Joystick.cs` (MZ-800 ports),
`Hardware/Joystick.cs` (MZ-side multiplexing on
$E008), `Hardware/JoystickInput.cs` (WinMM bridge).
