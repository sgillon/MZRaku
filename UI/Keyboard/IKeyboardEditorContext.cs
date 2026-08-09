using System.Collections.Generic;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// The data / mutation surface the keyboard-editor UI needs from a Sharp
/// MZ machine. Introduced in v1.1 Phase 5.5a so the same editor widgets
/// (matrix grid, per-slot editor, per-binding editors, advanced form)
/// serve both MZ-700 and MZ-80A without duplication.
///
/// A concrete implementation adapts one machine's statics
/// (<c>CharMap</c>, <c>SpecialKeyMap</c>, <c>MzGlyphCatalog</c>,
/// <c>MatrixCoverage</c>) and its mutable overrides
/// (<c>CharMapOverrides</c>, <c>KeyOverride</c>) into this shape. The
/// UI never touches the machine-specific types directly.
///
/// Both machines share the same 10-strobe × 8-bit matrix shape and the
/// same <see cref="KeyOverride"/> type (matrix coordinates are the only
/// thing that layer stores). What differs — and what this interface
/// mediates — is the char-map's <c>Press</c> type (structurally
/// identical but distinct C# types), the machine-specific special-key
/// tables, and the glyph / slot-label catalogs.
/// </summary>
public interface IKeyboardEditorContext
{
    /// <summary>Machine tag for header text ("MZ-700" or "MZ-80A").</summary>
    string MachineLabel { get; }

    /// <summary>
    /// Read-only peek at the live matrix row for strobe 0-9. Bit N clear
    /// = column N pressed. Feeds the grid's "held" highlight without
    /// registering as a scan on the machine side.
    /// </summary>
    byte PeekMatrixRow(int strobe);

    /// <summary>Built-in char → matrix-slot defaults, normalised through
    /// <see cref="MatrixPress"/>.</summary>
    IReadOnlyDictionary<char, MatrixPress> CharDefaults { get; }

    /// <summary>Mutable char-override layer, exposed via a
    /// machine-agnostic view.</summary>
    ICharMapOverridesView CharOverrides { get; }

    /// <summary>Built-in VK → matrix-slot defaults (special / non-printable
    /// keys — Enter, arrows, INST/DEL, etc.).</summary>
    IReadOnlyDictionary<Keys, (int Row, int Col)> SpecialKeyMap { get; }

    /// <summary>Friendly labels for the PC VKs above (capture-dialog
    /// display, per-slot bind text).</summary>
    IReadOnlyDictionary<Keys, string> SpecialKeyLabels { get; }

    /// <summary>Friendly labels for non-printable matrix slots (Enter,
    /// GRAPH, cursors, SHIFT, CTRL, BREAK, F-keys).</summary>
    IReadOnlyDictionary<(int Row, int Col), string> SlotLabels { get; }

    /// <summary>Mutable VK-override layer — shared type across both
    /// machines (matrix coords are the only thing it stores).</summary>
    KeyOverride KeyOverrides { get; }

    /// <summary>Canonical printable glyph at (row, col, mzShift), or null
    /// if the slot has no printable glyph on this machine.</summary>
    char? FindGlyphAt(int row, int col, bool mzShift);

    /// <summary>Friendly label for a non-printable slot on this machine,
    /// or null if the slot has no such label.</summary>
    string? FindSpecialLabelAt(int row, int col);

    /// <summary>
    /// Reference cells nothing currently reaches (given the char + VK
    /// override layers on top of the built-in defaults). Ordered by
    /// (row, col) for stable rendering. Kinds filtered to bindable
    /// cells per the machine's own bindability rule.
    /// </summary>
    IReadOnlyList<MatrixReferenceSlot> FindUnboundSlots();
}

/// <summary>
/// Machine-agnostic mirror of <c>CharMap.Press</c> /
/// <c>Mz80aCharMap.Press</c>. Fields Row/Col match the UI vocabulary
/// (matrix rows and columns) — MZ-80A code refers to the same integers
/// as Strobe/Bit but the shape is identical.
/// </summary>
public readonly record struct MatrixPress(int Row, int Col, bool MzShift);

/// <summary>
/// Read/write view over a machine-specific char-override store. Same
/// operations both <c>CharMapOverrides</c> and <c>Mz80aCharMapOverrides</c>
/// support; adapters translate between their native <c>Press</c> types
/// and <see cref="MatrixPress"/>.
/// </summary>
public interface ICharMapOverridesView
{
    bool TryLookup(char c, out MatrixPress press);
    void Set(char c, MatrixPress press);
    void Remove(char c);
    void Suppress(char c);
    void Unsuppress(char c);
    bool IsSuppressed(char c);
    IEnumerable<KeyValuePair<char, MatrixPress>> All { get; }
}

/// <summary>
/// Machine-agnostic mirror of <c>Mz700MatrixReference.Slot</c> /
/// <c>Mz80aMatrixReference.Slot</c>. Used by
/// <see cref="IKeyboardEditorContext.FindUnboundSlots"/> so the
/// AdvancedKeyboardForm's unbound-slot list renders identically for
/// either machine.
/// </summary>
public readonly record struct MatrixReferenceSlot(
    int Row,
    int Col,
    MatrixSlotKind Kind,
    string Id,
    string? UnshiftedGlyph = null,
    string? ShiftedGlyph = null);

/// <summary>
/// Superset of the slot-kind enums on both machines' matrix references.
/// Machine adapters map their native kind onto the corresponding member;
/// values a machine doesn't have (MZ-80A has no Function or Blank; both
/// have all others) simply aren't emitted by that adapter.
/// </summary>
public enum MatrixSlotKind
{
    Char,
    Function,
    Modifier,
    Mode,
    Edit,
    Cursor,
    Enter,
    Space,
    Unused,
    Blank,
    Unknown,
}
