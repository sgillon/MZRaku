using System.Collections.Generic;

namespace MZ700Emul.Hardware;

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
        /// audible tone is controlled by the speaker-NAND hard gate
        /// (see <see cref="SpeakerNandGate"/>), not by GATE0.</summary>
        FlipFlopGate0FromPc3,
    }

    /// <summary>
    /// What controls the speaker-amp NAND's second input — the "hard"
    /// gate that decides whether C0.OUT actually reaches the audio
    /// amplifier. This is distinct from <see cref="GateSource"/>
    /// (which controls whether C0 *counts*); a counter that's
    /// counting still produces silence if the speaker NAND is shut.
    /// </summary>
    public enum SpeakerNandGate
    {
        /// <summary>Driven by Q of IC7E LS74 FF1 (lower flip-flop).
        /// FF1 has D=D0 (data-bus bit 0), CK=IC6F LS02 NOR(MW, CSE2)
        /// — a rising edge on every write to the CSE2-decoded
        /// address ($E008), CL=RESET (cleared at power-on),
        /// PR=+5V (unused). The output NAND(C0.OUT, FF1.Q) drives
        /// the speaker amp (TR2/TR1 transistor pair through R103/VR).
        /// So writing D0=1 to $E008 enables audible sound; D0=0 to
        /// $E008 silences it regardless of C0's state. The MZ-700
        /// boot tone and S-BASIC MUSIC notes both rely on this — our
        /// emulator previously dropped $E008 writes, which is why
        /// boot tone never stopped and MUSIC produced one
        /// continuous re-pitched tone instead of discrete notes.
        /// </summary>
        E008Bit0Latch,
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
            "Buzzer tone generator. Reload value = 895000 / target Hz; OUT0 feeds the speaker NAND (the other input is the $E008-D0 hard gate — see SpeakerNandGate).",
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

    /// <summary>
    /// A sound event the system is expected to produce — used by the
    /// diagnostic walkthrough to anchor "is this thing actually
    /// happening?" Each entry carries enough metadata for the
    /// diagnostic to recognise it (frequency band, duration) without
    /// needing exact match. Empirical entries should be tightened
    /// when real-hardware measurements are available.
    /// </summary>
    public readonly record struct ExpectedEvent(
        string Name,
        double FrequencyHz,
        double DurationMs,
        string Trigger,
        ConfidenceLevel Confidence,
        string Notes);

    /// <summary>
    /// Known audible system events. Light to start with — anything
    /// not yet characterised against the manual or real hardware is
    /// marked Empirical with a sensible default so the diagnostic
    /// has something to compare against.
    /// </summary>
    public static readonly IReadOnlyList<ExpectedEvent> ExpectedEvents = new[]
    {
        new ExpectedEvent(
            Name: "Boot tone (Monitor ready beep)",
            FrequencyHz: 0,    // TBD — needs real-hardware measurement
            DurationMs: 0,     // TBD — anecdotally short (~50-100 ms)
            Trigger: "Fires once shortly after Reset, before the monitor prompt appears.",
            Confidence: ConfidenceLevel.Empirical,
            Notes: "On real hardware: ROM writes D0=1 to $E008 to enable the speaker NAND, " +
                   "C0 produces its 710 Hz square wave, then ROM writes D0=0 to $E008 to " +
                   "silence. Diagnosed from schematic 2026-06-19: silencing happens at the " +
                   "speaker-NAND hard gate (FF1.Q), not at GATE0 — C0 keeps counting through " +
                   "the silence."),

        new ExpectedEvent(
            Name: "S-BASIC MUSIC note",
            FrequencyHz: 0,    // Variable — driven by note value
            DurationMs: 0,     // Variable — driven by tempo + duration
            Trigger: "PLAY / MUSIC commands in S-BASIC.",
            Confidence: ConfidenceLevel.Empirical,
            Notes: "Same speaker-NAND mechanism as the boot tone. Each note: program C0's " +
                   "reload to the pitch, write D0=1 to $E008 to open the NAND, wait for the " +
                   "TEMPO-derived note duration, write D0=0 to $E008 to close. The TEMPO bit " +
                   "at $E008.0 is currently driven by a CPU-cycle-derived 50 Hz toggle " +
                   "(MZ700.CyclesPerTempoToggle) rather than C1.OUT1 directly. Worth " +
                   "characterising once boot tone is verified working."),
    };
}
