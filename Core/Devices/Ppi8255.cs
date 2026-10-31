using System;

namespace MZRaku.Core;

/// <summary>
/// Intel 8255 PPI emulation. Generic three-port implementation shared
/// by all three machines — per-machine bit wiring lives with the port
/// consumers, not here. See <see cref="Mz700IoBus"/> for MZ-700's Port C
/// bit map (cassette motor, speaker gate, INTMSK, cursor blink,
/// VBLANK) and <see cref="Mz80aIoBus"/> for the MZ-80A's simpler use.
///
/// One shared surface exposed here: the keyboard-matrix strobe path
/// (<see cref="Keyboard"/> = <see cref="IKeyboardMatrix"/>) all three
/// machines wire the same way, and a separate fast TEMPO signal
/// (driven from MZ700's <c>CyclesPerTempoToggle</c> empirical fit —
/// see its comment for the calibration history) exposed via
/// <see cref="TempoBit"/> for
/// MZ-700's Mz700IoBus to read on $E008 bit 0.
///
/// Control (0xE003 on MZ-700): 8255 control word (writes accepted,
/// mode semantics ignored — no consumer depends on mode setup).
/// </summary>
public sealed class Ppi8255
{
    internal byte PortA;      // keyboard strobe
    internal byte PortBIn;    // current keyboard row value (pushed by Keyboard)
    public byte PortCOut;   // low nibble outputs
    internal byte PortCIn;    // high nibble inputs (bits 4-7)

    // Held as the interface so each machine's keyboard (Mz700Keyboard,
    // Mz80aKeyboard, Mz800Keyboard) can feed the strobed row bits into
    // Port B.
    internal IKeyboardMatrix? Keyboard;

    internal bool InterruptMask => (PortCOut & 0x04) != 0;

    // Read by the Sound Diagnostic pane for its live "soft gate" gauge.
    // The change event below is what drives the log line; the diagnostic
    // needs the current level too, so we expose it as a computed getter.
    public bool SpeakerGate => (PortCOut & 0x08) != 0;

    public event Action<bool>? SpeakerGateChanged;

    // Every change of the PC0-PC3 outputs as (old, new). The MZ-800
    // Sound Diagnostic logs PC0, that machine's speaker gate.
    public event Action<byte, byte>? PortCChanged;


    internal byte Read(int reg)
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

    internal void Write(int reg, byte val)
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
                if (old != PortCOut) PortCChanged?.Invoke(old, PortCOut);
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
                        if (old != PortCOut) PortCChanged?.Invoke(old, PortCOut);
                    }
                    else
                    {
                        if (set) PortCIn |= mask; else PortCIn &= (byte)~mask;
                    }
                }
                break;
        }
    }

    internal void SetCursorBlink(bool bit)
    {
        // Slow visible-cursor signal at ~3 Hz period. Set on BOTH PC4 and
        // PC6 of PortCIn — the cursor-display software (monitor and BASIC)
        // reads PC6 per the service manual table, but our prior wiring used
        // PC4, so we set both to be safe. NOT exposed on $E008 bit 0.
        if (bit) PortCIn |= 0x50; else PortCIn &= 0xAF;
    }

    /// <summary>
    /// Fast tempo signal exposed on $E008 bit 0. Toggled from MZ700.cs by
    /// counting CPU cycles (rate fitted so MUSIC durations match real
    /// MZ-700 hardware timing — see MZ700.CyclesPerTempoToggle). Driven
    /// off the CPU clock rather than video frames because the underlying
    /// 555/556 timer's RC oscillator is independent of video timing on
    /// real hardware.
    /// </summary>
    internal bool TempoBit;

    private int _cursorBlinkFrame;
    internal void SetVBlank(bool vbl)
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
