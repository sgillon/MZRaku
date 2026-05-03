using System;

namespace MZ700Emul.Hardware;

/// <summary>
/// Intel 8255 PPI emulation for MZ-700.
///
/// Port A (0xE000, output): low nibble selects keyboard row strobe (0-9).
/// Port B (0xE001, input):  reads 8 bits of the selected keyboard row.
/// Port C (0xE002):
///   Output (low nibble):
///     PC0: cassette motor (0 = on)
///     PC1: cassette write data
///     PC2: INTMSK (1 = interrupt enabled - actually resets the 8253 OUT2 latch)
///     PC3: speaker gate (1 = enable speaker output from PIT counter 0)
///   Input (high nibble):
///     PC4: cursor-blink signal (~3 Hz period; mirrors PC6)
///     PC5: cassette read data
///     PC6: cursor-blink signal (~3 Hz period; per service manual)
///     PC7: VBLANK (1 = in vertical blank)
/// Separately, the fast TEMPO signal (~30 Hz) is exposed via TempoBit
/// for IoBus to read on $E008 bit 0. S-BASIC's MUSIC polls there for
/// note-duration timing; cursor display polls PortC for visible blink.
/// Control (0xE003): 8255 control word (we accept writes, ignore semantics).
/// </summary>
public sealed class Ppi8255
{
    public byte PortA;      // keyboard strobe
    public byte PortBIn;    // current keyboard row value (pushed by Keyboard)
    public byte PortCOut;   // low nibble outputs
    public byte PortCIn;    // high nibble inputs (bits 4-7)

    public Keyboard? Keyboard;

    public bool CassetteMotorOn => (PortCOut & 0x01) == 0; // MZ uses active-low; 0 = motor on... docs vary. We'll use: bit = 1 means motor on
    public bool SpeakerGate => (PortCOut & 0x08) != 0;
    public bool InterruptMask => (PortCOut & 0x04) != 0;

    public event Action<bool>? SpeakerGateChanged;
    public event Action<bool>? MotorChanged;
    public event Action<bool>? IntMaskChanged;

    public byte Read(int reg)
    {
        switch (reg & 3)
        {
            case 0: return PortA;
            case 1:
                if (Keyboard != null)
                    PortBIn = Keyboard.ReadRow(PortA & 0x0F);
                return PortBIn;
            case 2:
                // Combine high-nibble inputs with a readable copy of the low outputs
                return (byte)((PortCIn & 0xF0) | (PortCOut & 0x0F));
            case 3:
                return 0xFF;
        }
        return 0xFF;
    }

    public void Write(int reg, byte val)
    {
        switch (reg & 3)
        {
            case 0:
                PortA = val;
                break;
            case 1:
                PortBIn = val; // not normally used, but accept
                break;
            case 2:
            {
                byte old = PortCOut;
                PortCOut = (byte)(val & 0x0F);
                if (((old ^ PortCOut) & 0x08) != 0) SpeakerGateChanged?.Invoke((PortCOut & 0x08) != 0);
                if (((old ^ PortCOut) & 0x01) != 0) MotorChanged?.Invoke((PortCOut & 0x01) != 0);
                if (((old ^ PortCOut) & 0x04) != 0) IntMaskChanged?.Invoke((PortCOut & 0x04) != 0);
                break;
            }
            case 3:
                // 8255 control word. Bit 7 = 1 configures ports; bit 7 = 0 is a single-bit set/reset of port C.
                if ((val & 0x80) == 0)
                {
                    int bit = (val >> 1) & 0x07;
                    bool set = (val & 1) != 0;
                    byte mask = (byte)(1 << bit);
                    byte old = PortCOut;
                    if (bit < 4)
                    {
                        if (set) PortCOut |= mask; else PortCOut &= (byte)~mask;
                        if (((old ^ PortCOut) & 0x08) != 0) SpeakerGateChanged?.Invoke((PortCOut & 0x08) != 0);
                        if (((old ^ PortCOut) & 0x01) != 0) MotorChanged?.Invoke((PortCOut & 0x01) != 0);
                        if (((old ^ PortCOut) & 0x04) != 0) IntMaskChanged?.Invoke((PortCOut & 0x04) != 0);
                    }
                    else
                    {
                        if (set) PortCIn |= mask; else PortCIn &= (byte)~mask;
                    }
                }
                break;
        }
    }

    public void SetCassetteRead(bool bit)
    {
        if (bit) PortCIn |= 0x20; else PortCIn &= 0xDF;
    }

    public void SetCursorBlink(bool bit)
    {
        // Slow visible-cursor signal at ~3 Hz period. Set on BOTH PC4 and
        // PC6 of PortCIn — the cursor-display software (monitor and BASIC)
        // reads PC6 per the service manual table, but our prior wiring used
        // PC4, so we set both to be safe. NOT exposed on $E008 bit 0.
        if (bit) PortCIn |= 0x50; else PortCIn &= 0xAF;
    }

    /// <summary>
    /// Fast tempo signal exposed on $E008 bit 0. Toggled from MZ700.cs by
    /// counting CPU cycles (target rate ~100 Hz toggle, derived to give
    /// MUSIC durations that match real MZ-700 hardware timing). Driven
    /// off the CPU clock rather than video frames because the underlying
    /// 555/556 timer's RC oscillator is independent of video timing on
    /// real hardware.
    /// </summary>
    public bool TempoBit;

    private int _cursorBlinkFrame;
    public void SetVBlank(bool vbl)
    {
        if (vbl)
        {
            PortCIn |= 0x80;
            // Cursor-blink signal: ~3 Hz period (toggle every 20 frames),
            // exposed on PortCIn PC4 + PC6. Software reads this and
            // redraws the visible cursor on each transition.
            if (++_cursorBlinkFrame >= 20)
            {
                _cursorBlinkFrame = 0;
                SetCursorBlink((PortCIn & 0x10) == 0);
            }
        }
        else
        {
            PortCIn &= 0x7F;
        }
    }
}
