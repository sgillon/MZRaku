namespace MZRaku.Core;

/// <summary>
/// What a physical key on an MZ keyboard is, for drawing it in the
/// keyboard diagram. Shared by the three machines' layouts
/// (<see cref="Mz700KeyboardLayout"/>, <see cref="Mz80aKeyboardLayout"/>,
/// <see cref="Mz800KeyboardLayout"/>).
/// </summary>
public enum MzKeyKind
{
    Character,   // letters, digits, punctuation — main typewriter area
    Modifier,    // SHIFT, CTRL
    Mode,        // GRAPH, ALPHA
    Function,    // F1–F5  (half-height, left-justified labels)
    Cursor,      // ←↑↓→
    Edit,        // DEL, INST, BREAK
    Enter,       // RETURN
    Space,       // space bar
    Blank,       // unlabelled filler keys that exist purely for layout
                 // (e.g. the dummy after £ on the MZ-700's QWERTY row).
                 // Cap is coloured like the other amber group on a real
                 // MZ-700 keyboard.
}

/// <summary>
/// One physical key in a machine's keyboard layout: its matrix slot
/// (null for keys with none), its position and size in key units, its
/// kind, and an optional fixed label. Shared by the three machines'
/// layouts.
/// </summary>
public readonly record struct MzKey(
    string Id,
    int? Row,
    int? Col,
    float X,
    float Y,
    float W,
    float H,
    MzKeyKind Kind,
    string? FixedLabel,
    // Optional per-side label overrides used when a key's glyphs
    // aren't expressible via the machine's char map (because they'd
    // collide with another slot's mapping of the same character).
    // Example: the MZ-700's @ key shows `'` when shifted, but `'` is
    // mapped to shift-7 in its CharMap, so we can't reach @-shifted
    // through the catalog — we override here for the diagram's labels.
    string? UnshiftedLabel = null,
    string? ShiftedLabel = null);
