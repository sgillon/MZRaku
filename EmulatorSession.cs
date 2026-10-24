using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// One running machine and everything that drives it, independent of
/// any front end (v1.4 design decision D8). A session holds a single
/// machine for its whole lifetime: switching machine means a new
/// session (the WinForms front end relaunches the process).
///
/// Being built up in v1.4 Phase 2, a step at a time, out of MainForm.
/// So far it owns creating the machine and applying its settings.
/// </summary>
public sealed class EmulatorSession
{
    private readonly Settings _settings;

    /// <summary>
    /// Create the machine <see cref="Settings.CurrentMachine"/> names and
    /// apply its settings. ROM paths are resolved first (and saved if
    /// that found new ones); the ROMs themselves load at start.
    /// </summary>
    public EmulatorSession(Settings settings)
    {
        _settings = settings;
        // EnsureRomPaths scans for both machines' ROMs, so the CLI
        // override finding e.g. SA-1510.rom under roms/ works even
        // when settings.ini had never seen an MZ-80A launch before.
        // No-op if the previous Load() already populated both sides.
        if (_settings.EnsureRomPaths()) _settings.Save();
        // Construct exactly one of the three machines.
        if (_settings.CurrentMachine == MachineType.MZ700)
            Mz700 = new MZ700();
        else if (_settings.CurrentMachine == MachineType.MZ80A)
        {
            Mz80a = new MZ80A();
            Mz80a.Keyboard.InvertLetterShift = _settings.Mz80aInvertLetterShift;
            ApplyMz80aScreenColor();
        }
        else /* MachineType.MZ800 */
        {
            Mz800 = new MZ800();
            Mz800.Mem.VramExpansion = _settings.Mz800VramExpansion;
        }
        Active = (IMachine?)Mz700 ?? (IMachine?)Mz80a ?? Mz800!;

        // A host gamepad feeds the machine's stick state: the MZ-700's
        // MZ-1X03 or the MZ-800's $F0/$F1 ports. The MZ-80A has no
        // joystick; a throwaway keeps Joystick non-null for the front
        // end's gamepad source, and frame stepping doesn't poll it.
        Joystick = Mz700?.Joystick ?? Mz800?.Joystick ?? new Joystick();

        // Keyboard-override wiring. All three machines have the same
        // 10-strobe × 8-bit matrix topology so KeyOverride is shared;
        // Settings persists one instance per machine at
        // [KeyOverrides.MZ700] / [KeyOverrides.MZ80A] / [KeyOverrides.MZ800].
        // Char-map layers are split too: CharMap, Mz80aCharMap and
        // Mz800CharMap.
        if (Mz700 != null)
        {
            Mz700.Keyboard.Overrides = _settings.KeyOverrides;
            CharMap.Overrides = _settings.CharMapOverrides;
        }
        if (Mz80a != null)
        {
            Mz80a.Keyboard.Overrides = _settings.Mz80aKeyOverrides;
        }
        Mz80aCharMap.Overrides = _settings.Mz80aCharMapOverrides;
        if (Mz800 != null)
        {
            Mz800.Keyboard.Overrides = _settings.Mz800KeyOverrides;
        }
        Mz800CharMap.Overrides = _settings.Mz800CharMapOverrides;
    }

    // Exactly one of these is populated, from Settings.CurrentMachine.
    // Machine-specific hardware (Sound, Ppi, Pit, Cassette, Keyboard,
    // Video, ...) is reached through the populated one; the IMachine
    // surface (Cpu, Mem, RunFrame, Reset, LoadRoms, AutoLoad*, debugger
    // controls) through Active, which works for all three.
    public MZ700? Mz700 { get; }
    public MZ80A? Mz80a { get; }
    public MZ800? Mz800 { get; }
    public IMachine Active { get; }

    /// <summary>The joystick a host gamepad drives (a detached one on the MZ-80A).</summary>
    public Joystick Joystick { get; }

    /// <summary>"MZ-700", "MZ-80A" or "MZ-800".</summary>
    public string MachineLabel =>
        Mz800 != null ? "MZ-800" :
        Mz80a != null ? "MZ-80A" :
                        "MZ-700";

    /// <summary>
    /// Push the MZ-80A green-screen setting into its video. No-op on the
    /// other machines.
    /// </summary>
    public void ApplyMz80aScreenColor()
    {
        if (Mz80a == null) return;
        if (_settings.Mz80aGreenScreen)
        {
            Mz80a.Video.ForegroundArgb = unchecked((int)0xFF00FF00);
            Mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
        else
        {
            Mz80a.Video.ForegroundArgb = unchecked((int)0xFFFFFFFF);
            Mz80a.Video.BackgroundArgb = unchecked((int)0xFF000000);
        }
    }
}
