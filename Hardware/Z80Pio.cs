using System;

namespace MZRaku.Hardware;

/// <summary>
/// Zilog Z80 PIO — control-word decode and mode-3 (bit control)
/// interrupt generation. Written from the Z80 PIO datasheet's
/// programming model; only the parts the MZ-800 exercises so far are
/// modelled.
///
/// MZ-800 wiring (tech-ref p. 28): control ports $FC (A) / $FD (B),
/// data ports $FE (A) / $FF (B). PIT OUT0 passes through an inverter
/// to PA4, so PA4 falls when counter 0 reaches terminal count. BASIC
/// (1Z-016) programs port A as mode 3, monitoring PA4 only, active
/// low, vector $FC, then runs IM 2 with I=$0F — its keyboard-scan /
/// PSG-timing ISR lives at the word stored at $0FFC.
///
/// Phase 6.0 scope: control words (vector, mode select, I/O mask,
/// interrupt control, mask, enable flip-flop), mode-3 logic equation
/// with edge-triggered interrupt request, data-port reads of the input
/// pins. Not modelled yet: mode 0/1/2 handshakes (ARDY/ASTB — printer
/// strobes, Phase 7), IEI/IEO daisy-chain priority and the
/// interrupt-under-service latch cleared by RETI (the CPU's own IFF1
/// keeps the ISR from re-entering in practice).
/// </summary>
public sealed class Z80Pio
{
    public sealed class Port
    {
        public byte Vector;
        public int Mode = 1;           // power-on: input mode
        public byte IoMask = 0xFF;     // mode 3: 1 = bit is an input
        public byte IntMask = 0xFF;    // mode 3: 0 = bit is monitored
        public bool IntEnabled;
        public bool AndLogic;          // false = OR, true = AND
        public bool ActiveHigh;        // false = active low
        public byte Output;
        public byte Pins = 0xFF;       // external input levels
        internal bool ExpectIoMask;
        internal bool ExpectIntMask;
        internal bool Match;           // last evaluated logic-equation state
    }

    public readonly Port A = new();
    public readonly Port B = new();

    /// <summary>
    /// Raised when a port's interrupt condition asserts. Argument is
    /// the programmed vector byte the PIO drives onto the data bus
    /// during the acknowledge cycle.
    /// </summary>
    public event Action<byte>? InterruptRequested;

    public void Reset()
    {
        foreach (var p in new[] { A, B })
        {
            p.Vector = 0;
            p.Mode = 1;
            p.IoMask = 0xFF;
            p.IntMask = 0xFF;
            p.IntEnabled = false;
            p.AndLogic = false;
            p.ActiveHigh = false;
            p.Output = 0;
            p.ExpectIoMask = false;
            p.ExpectIntMask = false;
            p.Match = false;
            // Pins are external signals — not reset by the PIO.
        }
    }

    public void WriteControl(bool portB, byte value)
    {
        var p = portB ? B : A;
        if (p.ExpectIoMask)
        {
            p.IoMask = value;
            p.ExpectIoMask = false;
            Evaluate(p);
            return;
        }
        if (p.ExpectIntMask)
        {
            p.IntMask = value;
            p.ExpectIntMask = false;
            Evaluate(p);
            return;
        }
        if ((value & 0x01) == 0)
        {
            p.Vector = value;
            return;
        }
        switch (value & 0x0F)
        {
            case 0x0F: // mode select, D7-D6 = mode
                p.Mode = value >> 6;
                if (p.Mode == 3) p.ExpectIoMask = true;
                break;
            case 0x07: // interrupt control word
                p.IntEnabled = (value & 0x80) != 0;
                p.AndLogic = (value & 0x40) != 0;
                p.ActiveHigh = (value & 0x20) != 0;
                if ((value & 0x10) != 0) p.ExpectIntMask = true;
                Evaluate(p);
                break;
            case 0x03: // interrupt enable flip-flop only
                p.IntEnabled = (value & 0x80) != 0;
                Evaluate(p);
                break;
        }
    }

    public void WriteData(bool portB, byte value)
    {
        (portB ? B : A).Output = value;
    }

    public byte ReadData(bool portB)
    {
        var p = portB ? B : A;
        if (p.Mode == 3)
            return (byte)((p.Pins & p.IoMask) | (p.Output & ~p.IoMask));
        if (p.Mode == 0) return p.Output;
        return p.Pins;
    }

    /// <summary>Drive an external input pin on port A.</summary>
    public void SetPortAPin(int bit, bool high) => SetPin(A, bit, high);

    private void SetPin(Port p, int bit, bool high)
    {
        byte m = (byte)(1 << bit);
        byte next = high ? (byte)(p.Pins | m) : (byte)(p.Pins & ~m);
        if (next == p.Pins) return;
        p.Pins = next;
        Evaluate(p);
    }

    private void Evaluate(Port p)
    {
        if (p.Mode != 3 || p.ExpectIoMask || p.ExpectIntMask) return;
        byte monitored = (byte)(p.IoMask & ~p.IntMask);
        bool match = false;
        if (monitored != 0)
        {
            byte active = p.ActiveHigh ? p.Pins : (byte)~p.Pins;
            match = p.AndLogic
                ? (active & monitored) == monitored
                : (active & monitored) != 0;
        }
        bool rising = match && !p.Match;
        p.Match = match;
        if (rising && p.IntEnabled) InterruptRequested?.Invoke(p.Vector);
    }
}
