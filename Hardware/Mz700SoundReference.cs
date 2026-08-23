using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// Canonical MZ-700 sound-side reference — the single source of truth
/// for "how is the 8253 PIT wired in this machine, and what's it
/// programmed to do." Same pattern as
/// <see cref="Mz700MatrixReference"/>: every other sound-aware piece
/// of the codebase (<see cref="Pit8253"/>, <see cref="Sound"/>,
/// <see cref="Ppi8255"/>) is expected to derive from or validate
/// against this table.
///
/// SOURCE: Sharp MZ-700 Service Manual. The narrative section on the
/// 8253 (paragraph d, "Signals around the 8253") is the authority on
/// counter assignments and modes; the topology facts (gate sources,
/// clock-input wiring) come from the schematic. Service manual is
/// not in the repo — see [[reference-docs]]; user holds a local copy.
///
/// CONFIDENCE: facts encoded here are explicitly cited. Anything
/// derived empirically (e.g. boot-tone characteristics) is marked
/// <see cref="ConfidenceLevel.Empirical"/> so the diagnostic can flag
/// it for revisiting against real hardware.
/// </summary>
public static class Mz700SoundReference
{
    public enum PitCounter { C0 = 0, C1 = 1, C2 = 2 }

    /// <summary>
    /// 8253 operating modes. Names mirror the Intel datasheet so a
    /// reader cross-checking against either the chip datasheet or the
    /// service-manual narrative recognises the entry.
    /// </summary>
    public enum PitMode
    {
        Mode0InterruptOnTerminalCount = 0,
        Mode1HardwareRetriggerableOneShot = 1,
        Mode2RateGenerator = 2,
        Mode3SquareWave = 3,
        Mode4SoftwareTriggeredStrobe = 4,
        Mode5HardwareTriggeredStrobe = 5,
    }

    /// <summary>
    /// What feeds a counter's CLK pin in MZ-700 hardware.
    /// </summary>
    public enum ClockSource
    {
        /// <summary>Externally fed at 895 kHz from the "SOIN" line on
        /// the schematic. Confirmed by service-manual narrative
        /// ("counter #0 counts the input pulse of 895KHz").</summary>
        Soin895kHz,
        /// <summary>Externally fed at 15.6 kHz — the horizontal
        /// line rate. The schematic labels this "BLNK" at C1.CLK
        /// (inconsistent with the manual's HBLK label elsewhere) but
        /// the narrative is explicit: "counter #1 receives an input
        /// pulse of 15.6KHz."</summary>
        HBlank15p6kHz,
        /// <summary>Cascaded from another counter's OUT pin. Used by
        /// C2 (input from C1.OUT1, per the narrative "counter #2
        /// counts those pulses").</summary>
        CascadeFromOut1,
    }

    /// <summary>
    /// What controls a counter's GATE pin in MZ-700 hardware.
    /// </summary>
    public enum GateSource
    {
        /// <summary>Tied to +5V on the schematic — gate is always
        /// asserted, counter free-runs as long as it's been
        /// programmed.</summary>
        AlwaysHigh,
        /// <summary>Driven by Q of IC7E LS74 FF2 (upper flip-flop),
        /// through a 7417 open-collector buffer (IC8C). FF2 is clocked
        /// by PPI PC3 and samples a PC4-derived signal as D — but for
        /// emulation purposes the simplification "GATE0 follows PC3"
        /// holds: the counter is allowed to count once the speaker
        /// is enabled and stays counting; the actual on/off of the
        /// audible tone is controlled by the speaker-amp NAND's
        /// second input (Q of IC7E FF1, latched from $E008 D0),
        /// not by GATE0.</summary>
        FlipFlopGate0FromPc3,
    }

    public enum ConfidenceLevel
    {
        /// <summary>Cited directly from the service manual narrative
        /// or schematic — high confidence.</summary>
        ServiceManual,
        /// <summary>Inferred from code-comment archaeology that
        /// references the service manual without an attached
        /// citation. Should be re-checked against the manual at
        /// next opportunity.</summary>
        InferredFromCodeComments,
        /// <summary>Derived empirically (e.g. timing measurements
        /// against real hardware, or in-emulator observation).
        /// Worth re-validating when a measurement target presents
        /// itself.</summary>
        Empirical,
    }

    public readonly record struct CounterSpec(
        PitCounter Counter,
        ClockSource Clock,
        double ClockHz,
        GateSource Gate,
        PitMode ProgrammedMode,
        string Purpose,
        ConfidenceLevel Confidence);

    /// <summary>
    /// Counter assignments in MZ-700, in (C0, C1, C2) order.
    /// </summary>
    public static readonly IReadOnlyList<CounterSpec> Counters = new[]
    {
        new CounterSpec(
            PitCounter.C0,
            ClockSource.Soin895kHz,
            895_000.0,
            GateSource.FlipFlopGate0FromPc3,
            PitMode.Mode3SquareWave,
            "Buzzer tone generator. Reload value = 895000 / target Hz; OUT0 feeds the speaker NAND (the other input is the $E008-D0 hard gate: FF1.Q latched on every write to $E008, cleared by RESET).",
            ConfidenceLevel.ServiceManual),

        new CounterSpec(
            PitCounter.C1,
            ClockSource.HBlank15p6kHz,
            15_600.0,
            GateSource.AlwaysHigh,
            PitMode.Mode2RateGenerator,
            "Rate generator. Reload programmed to ~15600 → 1 Hz OUT1; cascades into C2's CLK.",
            ConfidenceLevel.ServiceManual),

        new CounterSpec(
            PitCounter.C2,
            ClockSource.CascadeFromOut1,
            // Effective tick rate is whatever C1.OUT1 produces; left
            // as 0 here because the cascade source isn't a fixed
            // clock.
            0.0,
            GateSource.AlwaysHigh,
            PitMode.Mode0InterruptOnTerminalCount,
            "12-hour interrupt timer. Counts C1.OUT1 pulses; OUT2 goes high after ~43200 ticks (≈12 h), wired to CPU INT.",
            ConfidenceLevel.ServiceManual),
    };

}
