namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 keyboard — Phase 1 stub. Implements
/// <see cref="IKeyboardMatrix"/> so <see cref="Ppi8255"/> can wire it
/// through PortA/PortB, but every strobe returns $FF (no keys
/// pressed). This lets the boot ROM's scan loop complete without
/// registering phantom key presses.
///
/// Phase 3 replaces this with a real 10×8 matrix driven by
/// <see cref="Mz800MatrixReference"/> (built from tech-ref p. 25),
/// PC-key mapping via the canonical-reference pattern, and the
/// diagnostics surface HidDiagnosticForm reads.
/// </summary>
public sealed class Mz800Keyboard : IKeyboardMatrix
{
    public KeyboardDiagnostics Diag { get; } = new();

    public byte ReadRow(int strobe) => 0xFF;

    public byte PeekMatrixRow(int row) => 0xFF;
}
