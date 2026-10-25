namespace MZRaku.Core;

/// <summary>
/// A host gamepad feeding a machine's <see cref="Joystick"/> (v1.4
/// design decision D5). The front end creates one bound to the active
/// machine's <see cref="Joystick"/>; on Windows that is
/// the app's <c>JoystickInput</c> (WinMM). Host slot 0 drives MZ stick 1
/// and slot 1 drives MZ stick 2.
/// </summary>
public interface IJoystickSource
{
    /// <summary>
    /// Read both host slots and write their position and buttons into
    /// the bound <see cref="Joystick"/>. A slot with nothing connected
    /// leaves its stick inactive. Called once per emulated frame.
    /// </summary>
    void Poll();

    /// <summary>
    /// The raw button bitmask host slot <paramref name="slot"/> (0 or 1)
    /// reports now, or 0 if nothing is connected. Lets settings and
    /// diagnostics show or capture a button press.
    /// </summary>
    uint GetCurrentButtons(uint slot);

    /// <summary>
    /// Choose the host buttons (0-31) that act as MZ SW1 and SW2. An
    /// index outside that range leaves the switch unmapped.
    /// </summary>
    void SetButtonIndices(int sw1Index, int sw2Index);

    /// <summary>The host button bitmask that acts as MZ SW1.</summary>
    uint Sw1ButtonMask { get; }

    /// <summary>The host button bitmask that acts as MZ SW2.</summary>
    uint Sw2ButtonMask { get; }
}
