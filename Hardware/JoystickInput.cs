using System;
using System.Runtime.InteropServices;

namespace MZRaku.Hardware;

/// <summary>
/// Windows joystick → <see cref="Joystick"/> bridge. Uses the WinMM
/// <c>joyGetPosEx</c> API rather than XInput so non-XInput controllers
/// (older PC gamepads, USB SNES adapters, bare PS3/PS4 pads, etc.)
/// are also picked up — XInput is a relatively narrow subset. The
/// WinMM API has been part of Windows since the mid-90s and is the
/// universal "joystick service" on Windows.
///
/// Mapping:
///   - X / Y position → AxisX / AxisY (0..255 after scaling against the
///     calibrated range from <c>joyGetDevCaps</c>). Y is inverted to
///     the MZ convention where 0 = up and 255 = down. Most pads report
///     "stick up" as a low Y value, which already matches.
///   - POV hat (D-pad on most controllers) overrides the analog axes
///     when held — clean 0 / 128 / 255 quantisation for the BASIC test
///     program's INT(JOY(0)/6.5) style code.
///   - Buttons 1 → SW1, 2 → SW2.
///
/// Joystick slot 0 → MZ stick 1, slot 1 → MZ stick 2. Disconnected
/// slots leave the corresponding StickState.Active = false so $E008
/// returns to "idle / pulled high".
/// </summary>
public sealed class JoystickInput
{
    private readonly Joystick _joystick;
    private bool _winmmAvailable = true;
    private readonly JOYCAPS[] _caps = new JOYCAPS[2];
    private readonly bool[] _capsValid = new bool[2];

    // PC gamepad button bitmask for each MZ-1X03 button. Defaults map
    // physical button 0 → MZ SW1 and button 1 → MZ SW2 (the original
    // hardcoded behaviour). Host re-applies these from settings.ini on
    // load and whenever the user edits the mapping.
    public uint Sw1ButtonMask { get; set; } = 1u << 0;
    public uint Sw2ButtonMask { get; set; } = 1u << 1;

    public void SetButtonIndices(int sw1Index, int sw2Index)
    {
        Sw1ButtonMask = (sw1Index >= 0 && sw1Index <= 31) ? 1u << sw1Index : 0u;
        Sw2ButtonMask = (sw2Index >= 0 && sw2Index <= 31) ? 1u << sw2Index : 0u;
    }

    public JoystickInput(Joystick joystick) { _joystick = joystick; }

    /// <summary>
    /// Returns the raw button bitmask currently reported by joystick
    /// <paramref name="slot"/> (0 or 1), or 0 if the slot is unplugged
    /// or WinMM is unavailable. Used by the Settings dialog's "Capture
    /// button" flow to let the user press a controller button rather
    /// than type a numeric index.
    /// </summary>
    public uint GetCurrentButtons(uint slot)
    {
        if (slot >= 2 || !_winmmAvailable) return 0;
        return TryGetState(slot, out var info) ? info.dwButtons : 0;
    }

    public void Poll()
    {
        if (!_winmmAvailable) return;
        for (uint slot = 0; slot < 2; slot++)
        {
            var s = _joystick.Sticks[slot];
            if (TryGetState(slot, out var info))
            {
                if (!_capsValid[slot]) _capsValid[slot] = TryGetCaps(slot, out _caps[slot]);
                ApplyToStick(s, info, _capsValid[slot] ? _caps[slot] : DefaultCaps);
            }
            else
            {
                s.Active = false;
                _capsValid[slot] = false;
            }
        }
    }

    private static readonly JOYCAPS DefaultCaps = new()
    {
        wXmin = 0, wXmax = 65535,
        wYmin = 0, wYmax = 65535,
    };

    private void ApplyToStick(Joystick.StickState s, JOYINFOEX info, JOYCAPS caps)
    {
        s.Active = true;

        // Only a device that reports a hat has a meaningful dwPOV: one
        // without (JOYCAPS_HASPOV clear) leaves it at 0, which reads as
        // "hat held up" and pinned every stick to full-up.
        bool hasPov = (caps.wCaps & JOYCAPS_HASPOV) != 0;
        bool povHeld = hasPov && info.dwPOV != 0xFFFF && info.dwPOV != 0xFFFFFFFF;
        bool povUp = false, povDown = false, povLeft = false, povRight = false;
        if (povHeld)
        {
            // POV is in degrees * 100 (0 = up, 9000 = right, ...).
            uint deg = info.dwPOV;
            povUp    = deg >= 31500 || deg <=  4500;
            povRight = deg >=  4500 && deg <= 13500;
            povDown  = deg >= 13500 && deg <= 22500;
            povLeft  = deg >= 22500 && deg <= 31500;
        }

        s.AxisX = MapWithDpad(info.dwXpos, caps.wXmin, caps.wXmax, povLeft, povRight, invert: false);
        s.AxisY = MapWithDpad(info.dwYpos, caps.wYmin, caps.wYmax, povUp,   povDown,  invert: false);

        s.Sw1 = (info.dwButtons & Sw1ButtonMask) != 0;
        s.Sw2 = (info.dwButtons & Sw2ButtonMask) != 0;
    }

    private static byte MapWithDpad(uint pos, uint min, uint max, bool low, bool high, bool invert)
    {
        if (low && !high) return invert ? (byte)255 : (byte)0;
        if (high && !low) return invert ? (byte)0 : (byte)255;
        if (max <= min) return 128;
        // Tiny deadzone at centre — about 6% of the range each way — so
        // a slightly drifted analog stick reports as centred.
        long range = (long)(max - min);
        long centred = (long)pos - (long)min - range / 2;
        if (Math.Abs(centred) < range / 16) return 128;
        long mapped = ((long)(pos - min) * 255) / range;
        if (mapped < 0) mapped = 0;
        if (mapped > 255) mapped = 255;
        return (byte)mapped;
    }

    private bool TryGetState(uint joyId, out JOYINFOEX info)
    {
        info = new JOYINFOEX
        {
            dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(),
            dwFlags = JOY_RETURNALL,
        };
        try
        {
            var r = joyGetPosEx(joyId, ref info);
            return r == JOYERR_NOERROR;
        }
        catch (DllNotFoundException) { _winmmAvailable = false; return false; }
        catch (EntryPointNotFoundException) { _winmmAvailable = false; return false; }
    }

    private bool TryGetCaps(uint joyId, out JOYCAPS caps)
    {
        caps = default;
        try
        {
            var r = joyGetDevCapsW(joyId, out caps, (uint)Marshal.SizeOf<JOYCAPS>());
            return r == JOYERR_NOERROR;
        }
        catch (DllNotFoundException) { _winmmAvailable = false; return false; }
        catch (EntryPointNotFoundException) { _winmmAvailable = false; return false; }
    }

    // ---- WinMM joystick P/Invoke ----

    private const uint JOYERR_NOERROR = 0;
    private const uint JOYCAPS_HASPOV = 0x0010;
    private const uint JOY_RETURNX = 0x00000001;
    private const uint JOY_RETURNY = 0x00000002;
    private const uint JOY_RETURNZ = 0x00000004;
    private const uint JOY_RETURNR = 0x00000008;
    private const uint JOY_RETURNU = 0x00000010;
    private const uint JOY_RETURNV = 0x00000020;
    private const uint JOY_RETURNPOV = 0x00000040;
    private const uint JOY_RETURNBUTTONS = 0x00000080;
    private const uint JOY_RETURNALL = JOY_RETURNX | JOY_RETURNY | JOY_RETURNZ
                                     | JOY_RETURNR | JOY_RETURNU | JOY_RETURNV
                                     | JOY_RETURNPOV | JOY_RETURNBUTTONS;

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCapsW(uint uJoyID, out JOYCAPS pjc, uint cbjc);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos;
        public uint dwYpos;
        public uint dwZpos;
        public uint dwRpos;
        public uint dwUpos;
        public uint dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1;
        public uint dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPS
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint wXmin;
        public uint wXmax;
        public uint wYmin;
        public uint wYmax;
        public uint wZmin;
        public uint wZmax;
        public uint wNumButtons;
        public uint wPeriodMin;
        public uint wPeriodMax;
        public uint wRmin;
        public uint wRmax;
        public uint wUmin;
        public uint wUmax;
        public uint wVmin;
        public uint wVmax;
        public uint wCaps;
        public uint wMaxAxes;
        public uint wNumAxes;
        public uint wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }
}
