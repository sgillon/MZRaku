using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Which mapping layer resolved the last PC keystroke. Shared across
/// both machines' keyboards so the HID Diagnostic pane renders a
/// single consistent view regardless of which machine is monitored.
/// <see cref="Override"/> is populated by both
/// <see cref="Keyboard.OnKeyDown"/> and
/// <see cref="Mz80aKeyboard.OnKeyDown"/> — each carries its own
/// <see cref="KeyOverride"/> field, wired via the Settings dialog
/// per machine.
/// </summary>
public enum InputLayer { None, Override, SpecialKey, Character }

/// <summary>
/// Per-frame telemetry the HID Diagnostic form reads to render its
/// "what just happened" view. Populated by the concrete keyboard
/// classes (<see cref="Keyboard"/>, <see cref="Mz80aKeyboard"/>) in
/// their KeyDown / KeyPress / KeyUp / ReadRow paths — the diagnostic
/// itself never subscribes to events or duplicates mapping logic.
/// </summary>
public sealed class KeyboardDiagnostics
{
    public Keys LastKeyDown;
    public Keys LastKeyUp;
    public char LastKeyChar;
    public InputLayer LastLayer;
    public int LastRow = -1;
    public int LastCol = -1;
    public bool? LastMzShift;
    public int LastScanRow = -1;

    public void Record(InputLayer layer, int row, int col, bool? mzShift)
    {
        LastLayer = layer;
        LastRow = row;
        LastCol = col;
        LastMzShift = mzShift;
    }
}
