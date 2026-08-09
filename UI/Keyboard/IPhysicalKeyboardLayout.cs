using System.Collections.Generic;
using System.Drawing;

namespace MZRaku;

/// <summary>
/// Physical-keyboard layout the diagram control renders. Each machine
/// supplies its own implementation (MZ-700 has coloured caps and a
/// left-edge modifier stack; MZ-80A has a different keycap distribution
/// and a distinct GRPH key). Widget code is a pure function of this
/// spec — no machine-specific branches in the drawing.
///
/// Introduced in v1.1 Phase 5.5a alongside <see cref="IKeyboardEditorContext"/>
/// so <c>MzKeyboardDiagram</c> serves both machines with one class.
/// </summary>
public interface IPhysicalKeyboardLayout
{
    /// <summary>Total layout width in key units (1.0 = standard alpha cap).</summary>
    float Width { get; }

    /// <summary>Total layout height in key units.</summary>
    float Height { get; }

    /// <summary>Every physical key on the machine, in draw order.</summary>
    IReadOnlyList<PhysicalKey> Keys { get; }

    /// <summary>
    /// Cap colour palette for the given kind. Different machines have
    /// different cap colouring — MZ-700 is white / amber / blue by
    /// function group; MZ-80A is closer to a single grey-and-brown
    /// scheme. <paramref name="hovered"/> gets a slightly darker fill
    /// so the user can see what they're pointing at.
    /// </summary>
    (Color fill, Color border, Color text) ColorsForKind(PhysicalKeyKind kind, bool hovered);

    /// <summary>
    /// Canonical printable glyph at (row, col, mzShift) on this machine.
    /// The diagram falls back to this when a <see cref="PhysicalKey"/>
    /// doesn't carry an explicit <c>UnshiftedLabel</c> / <c>ShiftedLabel</c>
    /// — the common case, since most keys let the glyph catalog resolve
    /// their labels rather than duplicating them in layout data.
    /// </summary>
    char? FindGlyphAt(int row, int col, bool mzShift);

    /// <summary>
    /// Friendly label for a non-printable slot on this machine, or null
    /// if none. Used by the diagram for slots without a
    /// <see cref="PhysicalKey.FixedLabel"/> and no printable glyph.
    /// </summary>
    string? FindSpecialLabelAt(int row, int col);
}

/// <summary>
/// One physical keycap. Matrix coords are nullable — layout-filler caps
/// (the dummy at MZ-700's (0, 7)) have no scan slot. Function keys and
/// modifier keys carry <see cref="FixedLabel"/>; character keys either
/// resolve their glyph pair from the editor context or supply explicit
/// per-side overrides where the glyph pair isn't representable in the
/// char-map (e.g. MZ-700 '@' / '`' where '`' collides with another slot).
/// </summary>
public readonly record struct PhysicalKey(
    string Id,
    int? Row,
    int? Col,
    float X,
    float Y,
    float W,
    float H,
    PhysicalKeyKind Kind,
    string? FixedLabel,
    string? UnshiftedLabel = null,
    string? ShiftedLabel = null);

/// <summary>
/// Superset of the <c>KeyKind</c> enums on both machines' physical
/// layouts. Machine adapters map their native kind onto the
/// corresponding member; values a machine doesn't have simply aren't
/// emitted by that adapter.
/// </summary>
public enum PhysicalKeyKind
{
    Character,
    Modifier,
    Mode,
    Function,
    Cursor,
    Edit,
    Enter,
    Space,
    Blank,
}
