using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Which mapping layer resolved the last PC keystroke. Shared across
/// both machines' keyboards so the HID Diagnostic pane can render a
/// single consistent view regardless of which machine is monitored.
/// <see cref="Override"/> is currently MZ-700-only (MZ-80A doesn't
/// yet ship a KeyOverride layer — coming with the v1.1.0 Phase 5
/// settings-dialog work), but the enum tolerates that gracefully.
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
