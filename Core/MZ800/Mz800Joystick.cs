namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 joystick ports (tech-ref p. 32, MZ-1X16 manual p. 5): two
/// Atari-compatible digital sticks. The 8255 drives each stick's common
/// line low as a strobe — PA4 for joystick 1, PA5 for joystick 2 — and
/// closed switches pull data lines low through an LS365, read at IN $F0
/// (joystick 1) and IN $F1 (joystick 2). Active low:
///   D0 forward (up)   D1 back (down)   D2 left   D3 right
///   D4 trigger 1      D5 trigger 2     D6-D7 not driven (read 1)
/// With the strobe high, or no stick connected, the port reads $FF.
/// Bit order confirmed by Bruce Lee's decode table ($C0F5) and the
/// strobes by its OUT ($D0),$EF / $DF before each read.
///
/// The host gamepad feeds the same <see cref="Joystick.StickState"/> the
/// MZ-700's analog MZ-1X03 uses (axes 0..255, two buttons); a direction
/// counts once the stick is a quarter of its travel past centre.
/// </summary>
public static class Mz800Joystick
{
    private const int Low = 64;
    private const int High = 192;

    public static byte Read(Joystick.StickState s, bool strobed)
    {
        byte v = 0xFF;
        if (!strobed || !s.Active) return v;
        if (s.AxisY < Low)  v &= unchecked((byte)~0x01);
        if (s.AxisY > High) v &= unchecked((byte)~0x02);
        if (s.AxisX < Low)  v &= unchecked((byte)~0x04);
        if (s.AxisX > High) v &= unchecked((byte)~0x08);
        if (s.Sw1)          v &= unchecked((byte)~0x10);
        if (s.Sw2)          v &= unchecked((byte)~0x20);
        return v;
    }
}
