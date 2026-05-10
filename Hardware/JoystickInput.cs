using System;
using System.Runtime.InteropServices;

namespace MZ700Emul.Hardware;

/// <summary>
/// XInput → <see cref="Joystick"/> bridge. Polls up to two XInput
/// controllers (index 0 = MZ stick 1, index 1 = MZ stick 2) once per
/// emulated frame and writes their state into the joystick's logical
/// model. Connected controllers flip <see cref="Joystick.StickState.Active"/>
/// to true; disconnected ones flip it back to false so the
/// corresponding $E008 bits return to "idle / pulled high".
///
/// Mappings:
///   - Left thumbstick → MZ X/Y axes (linear, with deadzone snapping
///     to 128). Y is inverted: XInput's positive-Y-is-up becomes the
///     MZ convention where 0 = up and 255 = down.
///   - D-pad overrides the stick when held (snap to 0 / 128 / 255).
///   - A button → SW1, B button → SW2.
///
/// Deadzone uses Microsoft's recommended LEFT_THUMB constant (7849)
/// and is currently fixed; it'll move to settings in a follow-up.
/// </summary>
public sealed class JoystickInput
{
    private readonly Joystick _joystick;

    // Microsoft's recommended deadzones from XInput.h.
    private const short DeadzoneLeftThumb = 7849;

    private const ushort BtnDpadUp = 0x0001;
    private const ushort BtnDpadDown = 0x0002;
    private const ushort BtnDpadLeft = 0x0004;
    private const ushort BtnDpadRight = 0x0008;
    private const ushort BtnA = 0x1000;
    private const ushort BtnB = 0x2000;

    private bool _xinputAvailable = true;

    public JoystickInput(Joystick joystick) { _joystick = joystick; }

    /// <summary>
    /// Polls both XInput slots and writes results into the
    /// <see cref="Joystick"/> state. Safe to call when no DLL or
    /// controller is present — failures fall through to "no input".
    /// </summary>
    public void Poll()
    {
        if (!_xinputAvailable) return;
        for (uint i = 0; i < 2; i++)
        {
            var s = _joystick.Sticks[i];
            if (TryGetState(i, out var pad))
            {
                s.Active = true;
                bool up    = (pad.wButtons & BtnDpadUp)    != 0;
                bool down  = (pad.wButtons & BtnDpadDown)  != 0;
                bool left  = (pad.wButtons & BtnDpadLeft)  != 0;
                bool right = (pad.wButtons & BtnDpadRight) != 0;
                s.AxisX = MapWithDpad(pad.sThumbLX, left, right, invert: false);
                s.AxisY = MapWithDpad(pad.sThumbLY, up,   down,  invert: true);
                s.Sw1 = (pad.wButtons & BtnA) != 0;
                s.Sw2 = (pad.wButtons & BtnB) != 0;
            }
            else
            {
                s.Active = false;
            }
        }
    }

    private static byte MapWithDpad(short axis, bool low, bool high, bool invert)
    {
        // D-pad takes priority — gives clean 0 / 128 / 255 values for
        // the BASIC examples that quantise via INT(JOY(0)/6.5).
        if (low && !high) return invert ? (byte)255 : (byte)0;
        if (high && !low) return invert ? (byte)0 : (byte)255;
        if (Math.Abs((int)axis) < DeadzoneLeftThumb) return 128;
        // Linear -32768..32767 → 0..255. With Y-axis inversion, "stick
        // pushed up" (positive XInput Y) becomes MZ AxisY ≈ 0.
        int raw = invert ? -axis - 1 : axis;
        int mapped = (raw + 32768) >> 8;   // 0..255
        if (mapped < 0) mapped = 0;
        if (mapped > 255) mapped = 255;
        return (byte)mapped;
    }

    private bool TryGetState(uint userIndex, out XINPUT_GAMEPAD pad)
    {
        pad = default;
        try
        {
            uint result = XInputGetState(userIndex, out var state);
            if (result != 0) return false;
            pad = state.Gamepad;
            return true;
        }
        catch (DllNotFoundException)
        {
            // No XInput DLL on this box (very old Windows). Mark
            // unavailable so we don't pay the exception every frame.
            _xinputAvailable = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _xinputAvailable = false;
            return false;
        }
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
}
