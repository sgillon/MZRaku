using System;

namespace MZ700Emul.Hardware;

/// <summary>
/// Intel 8253 PIT emulation for MZ-700.
///
/// Counter 0 (0xE004): sound frequency generator. Input clock = ~895kHz
///                     (derived from video master clock). Output feeds
///                     speaker gate (AND with PPI PC3 = speaker gate),
///                     and physically cascades to Counter 1's CLK pin.
/// Counter 1 (0xE005): input clock = Counter 0 output. Used by S-BASIC's
///                     MUSIC command as the note-duration timer — without
///                     the cascade, MUSIC hangs waiting for C1 to time out.
/// Counter 2 (0xE006): input clock = 15.7kHz (horizontal sync); output
///                     is cursor blink signal into PPI PC4. Also (when
///                     enabled via PPI PC2) triggers Z80 interrupt.
/// Control  (0xE007): mode/latch commands.
///
/// We implement mode 3 (square wave) for counter 0 and mode 2/3 for
/// counters 1 and 2. Latching of counters for readback is supported.
/// </summary>
public sealed class Pit8253
{
    public sealed class Counter
    {
        public ushort Reload;
        public ushort Value;
        // Default to mode 3 (square wave). On real hardware the mode is
        // undefined at power-on, but the monitor's pre-LOAD break-wait at
        // $02C8 polls C2's OUT for toggling activity before any control word
        // has been written — that only works if our default mode keeps the
        // counter cycling. Mode 0 (one-shot) takes effect only after BASIC
        // explicitly programs it.
        public byte Mode = 3;
        public byte RwMode;       // 1=LSB, 2=MSB, 3=LSB then MSB
        public bool WriteHigh;    // for RwMode=3
        public bool ReadHigh;
        public bool Running;
        public bool Out;
        public ushort Latched;
        public bool IsLatched;
        public bool Gate = true;
    }

    public readonly Counter[] Counters = new Counter[3];
    public System.Text.StringBuilder? WriteLog;

    public event Action<bool>? Counter2Out;  // cursor blink / interrupt source

    public Pit8253()
    {
        for (int i = 0; i < 3; i++) Counters[i] = new Counter();
        // On real hardware the 8253 counters begin receiving their clock
        // inputs from power-on even before the ROM writes a control word.
        // 1Z-013A routines (e.g. the $02C8 break-wait) busy-wait on the
        // TEMPO signal at $E008 bit 0 — which is C1.OUT — long before any
        // counter is explicitly programmed, so default C1 to a free-running
        // square wave with a small reload so the output toggles promptly.
        // (The $02C8 break-wait is also short-circuited by Cassette.OnPreStep
        // for safety in our emulation.)
        Counters[1].Reload = 8;
        Counters[1].Value = 8;
        Counters[1].Running = true;
    }

    public byte Read(int reg)
    {
        int idx = reg & 3;
        if (idx > 2) return 0xFF;
        var c = Counters[idx];
        ushort v = c.IsLatched ? c.Latched : c.Value;
        byte result;
        string tag;
        switch (c.RwMode)
        {
            case 1: // LSB only
                result = (byte)(v & 0xFF);
                c.IsLatched = false;
                tag = "LSB only";
                break;
            case 2: // MSB only
                result = (byte)((v >> 8) & 0xFF);
                c.IsLatched = false;
                tag = "MSB only";
                break;
            case 3: // LSB then MSB
                if (!c.ReadHigh)
                {
                    result = (byte)(v & 0xFF);
                    c.ReadHigh = true;
                    tag = "LSB of 16";
                }
                else
                {
                    result = (byte)((v >> 8) & 0xFF);
                    c.ReadHigh = false;
                    c.IsLatched = false;
                    tag = "MSB of 16";
                }
                break;
            default:
                result = 0;
                tag = "rwMode=0?";
                break;
        }
        WriteLog?.AppendLine($"C{idx}->${result:X2} ({tag}, {(c.IsLatched ? "latched" : "live")} cnt=${v:X4})");
        return result;
    }

    public void Write(int reg, byte val)
    {
        int idx = reg & 3;
        if (idx == 3)
        {
            // Control word
            int sc = (val >> 6) & 3;
            int rw = (val >> 4) & 3;
            int mode = (val >> 1) & 7;
            WriteLog?.AppendLine($"CTRL=${val:X2}: counter={sc} rw={rw} mode={mode}{(rw == 0 ? " (LATCH)" : "")}");
            if (sc == 3) return; // read-back, ignore

            if (rw == 0)
            {
                // counter latch command
                Counters[sc].Latched = Counters[sc].Value;
                Counters[sc].IsLatched = true;
                Counters[sc].ReadHigh = false;
            }
            else
            {
                Counters[sc].RwMode = (byte)rw;
                Counters[sc].Mode = (byte)mode;
                Counters[sc].WriteHigh = false;
                Counters[sc].ReadHigh = false;
                Counters[sc].Running = false;
                // Intel 8253 spec: writing a control word forces OUT to its
                // initial state for the new mode. Mode 0 → OUT low; modes
                // 2/3 → OUT high. Critical for BASIC's MUSIC, which polls
                // C2.OUT via $E008 bit 0 in a "wait LOW then wait HIGH" loop
                // at $09F1 — without this reset, OUT stays high from the
                // previous note's terminal count and the loop hangs.
                if (mode == 0) Counters[sc].Out = false;
                else if (mode == 2 || mode == 3) Counters[sc].Out = true;
            }
            return;
        }

        var c = Counters[idx];
        switch (c.RwMode)
        {
            case 1:
                c.Reload = (ushort)((c.Reload & 0xFF00) | val);
                c.Value = c.Reload == 0 ? (ushort)0xFFFF : c.Reload;
                c.Running = true;
                if (c.Mode == 0) c.Out = false;
                WriteLog?.AppendLine($"C{idx}<-${val:X2} (LSB only) reload now=${c.Reload:X4}");
                break;
            case 2:
                c.Reload = (ushort)((c.Reload & 0x00FF) | (val << 8));
                c.Value = c.Reload == 0 ? (ushort)0xFFFF : c.Reload;
                c.Running = true;
                if (c.Mode == 0) c.Out = false;
                WriteLog?.AppendLine($"C{idx}<-${val:X2} (MSB only) reload now=${c.Reload:X4}");
                break;
            case 3:
                if (!c.WriteHigh)
                {
                    c.Reload = (ushort)((c.Reload & 0xFF00) | val);
                    c.WriteHigh = true;
                    WriteLog?.AppendLine($"C{idx}<-${val:X2} (LSB of 16) reload partial=${c.Reload:X4}");
                }
                else
                {
                    c.Reload = (ushort)((c.Reload & 0x00FF) | (val << 8));
                    c.WriteHigh = false;
                    c.Value = c.Reload == 0 ? (ushort)0xFFFF : c.Reload;
                    c.Running = true;
                    // Mode 0: writing the new count starts a fresh countdown
                    // with OUT low until terminal count. See note in the
                    // control-word branch above.
                    if (c.Mode == 0) c.Out = false;
                    WriteLog?.AppendLine($"C{idx}<-${val:X2} (MSB of 16) reload now=${c.Reload:X4} RUN");
                }
                break;
        }
    }

    // Tick counters with their respective number of input-clock pulses.
    // Real MZ-700 hardware (per service manual):
    //   C0 input = 895kHz  (audio frequency divider)
    //   C1 input = 15.6kHz (HBLNK; mode 2 → 1Hz pulse on OUT1, the "tempo")
    //   C2 input = OUT1    (cascade from C1; mode 0 → 12-hour RTC interrupt)
    // The TEMPO signal (C1.OUT) is what's exposed as $E008 bit 0; S-BASIC's
    // MUSIC polls it to count tempo cycles for note duration.
    public void Tick(int c0Ticks, int c1Ticks)
    {
        TickCounter(0, c0Ticks);
        TickCounter(1, c1Ticks);
    }

    private void TickCounter(int idx, int ticks)
    {
        var c = Counters[idx];
        if (!c.Running || c.Reload == 0 || ticks <= 0) return;

        int remaining = ticks;
        while (remaining > 0)
        {
            if (c.Value > remaining)
            {
                c.Value -= (ushort)remaining;
                remaining = 0;
            }
            else
            {
                remaining -= c.Value;
                if (c.Mode == 0)
                {
                    // Mode 0 (interrupt-on-terminal-count): on reaching 0,
                    // OUT goes HIGH (and stays high until a new count is
                    // loaded), counter wraps to $FFFF and keeps decrementing.
                    // S-BASIC's MUSIC uses C2 in mode 0 as a duration timer
                    // and polls the count, detecting completion by the wrap
                    // (value rising from near-0 to $FFFF) and/or OUT going
                    // high. The previous always-reload-from-Reload behaviour
                    // had it wrap back to the original reload value — which
                    // happened to look like a wrap to BASIC but caused MUSIC
                    // to play through several extra spurious cycles.
                    c.Value = 0xFFFF;
                    if (!c.Out)
                    {
                        c.Out = true;
                        if (idx == 2) Counter2Out?.Invoke(true);
                    }
                }
                else
                {
                    // Real Intel 8253 mode 2/3: full OUT period = Reload
                    // input ticks. We model OUT as a 50/50 toggle, so we
                    // reload to Reload/2 (each toggle is one half-period)
                    // — without this, period was 2*Reload and audio pitch
                    // came out an octave low, plus BASIC MUSIC notes ran
                    // 2× longer than the tempo cycle they're polling for.
                    c.Value = (ushort)Math.Max(1, c.Reload >> 1);
                    c.Out = !c.Out;
                    // Real-hardware cascade: OUT1 -> CLK2 (every C1 OUT
                    // edge clocks C2 — service manual: "C2 counts those
                    // pulses"). Drives the 12-hour RTC interrupt.
                    if (idx == 1) TickCounter(2, 1);
                    if (idx == 2) Counter2Out?.Invoke(c.Out);
                }
            }
        }
    }

    // Helper: get approximate output frequency for counter 0 (Hz), given input clock.
    public double Counter0FrequencyHz(double inputHz)
    {
        var c = Counters[0];
        if (c.Reload < 2) return 0;
        // Mode 3: square wave; period = reload / inputHz
        return inputHz / c.Reload;
    }
}
