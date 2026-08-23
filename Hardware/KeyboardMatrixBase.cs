using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Shared plumbing for the two Sharp keyboards
/// (<see cref="Keyboard"/> and <see cref="Mz80aKeyboard"/>): the
/// 10 × 8 active-low matrix backing, the KeyDown→KeyPress→KeyUp
/// hold bookkeeping, the shift-race stage buffer, the "any-hold
/// gates PC shift" effective-shift rule. Both machines' 8255 PPI
/// wiring is identical at this layer (strobe 0-9 → Port A, column
/// bits back on Port B, pressed = 0), so the primitives are shared
/// verbatim.
///
/// v1.2 audit F-015 / F-021 extracted this. Concrete keyboards keep
/// only the per-machine behaviour: MZ-700 writes $1170 as its shift
/// mirror and drives a scan-detection auto-typer; MZ-80A holds
/// GraphMode / InvertLetterShift / different SpecialKeyMap shape /
/// time-based auto-typer / Ctrl-key routing. Both machines'
/// OnKeyDown / OnKeyPress / OnKeyUp handlers stay per-machine
/// because the divergences there (Ctrl handling, RAM mirror,
/// case-inversion, GraphMode toggle) resist a clean shared skeleton
/// without risk to the shift-race timing behaviour.
///
/// The shift-slot coordinate is exposed via <see cref="ShiftSlot"/>
/// — MZ-700 SHIFT sits at (8, 0), MZ-80A at (0, 0). Subclasses
/// hook <see cref="OnShiftStateChanged"/> to plug in machine-specific
/// side effects (MZ-700 mirrors to Memory.Ram[$1170]; MZ-80A no-op).
/// </summary>
public abstract class KeyboardMatrixBase : IKeyboardMatrix
{
    // Matrix backing: bit N of _rows[strobe] is column N of that
    // strobe. Active-low, so 0 = pressed. Public ReadRow / SetMatrix
    // are the only supported ways to touch this array from outside.
    protected readonly byte[] _rows = new byte[10];

    /// <summary>
    /// One held key: the matrix bits its KeyDown asserted plus its
    /// shift-state requirement. ExplicitMzShift:
    ///   true  → hold requires MZ shift bit SET while held
    ///   false → hold requires MZ shift bit CLEAR while held
    ///   null  → no preference, pass through PC shift state
    /// Both true and false are "overrides" — without the false
    /// override, an unshifted char produced via PC Shift (e.g. UK
    /// Shift+' → '@') gets clobbered the next time
    /// <see cref="ApplyShiftState"/> fires with PC Shift held.
    /// </summary>
    protected record ActiveHold(int Row, int Col, bool? ExplicitMzShift);

    /// <summary>Live holds keyed by the originating PC virtual key.</summary>
    protected readonly Dictionary<Keys, ActiveHold> _holds = new();

    /// <summary>Most-recent KeyDown VK expected to pair with a KeyPress char.</summary>
    protected Keys _pendingDownVk = Keys.None;

    /// <summary>Latest observed PC-shift state.</summary>
    protected bool _pcShift;

    /// <summary>
    /// A press whose key bit is being held back until a ROM scan
    /// observes the shift state. See
    /// <see cref="LiveShiftStageFrames"/> and
    /// <see cref="TickStagedKeyBits"/>.
    /// </summary>
    protected record struct StagedPress(Keys Vk, int Row, int Col, int FramesLeft);
    protected readonly List<StagedPress> _stagedKeyBits = new();

    /// <summary>
    /// Two frames empirically clears the unshifted-`'` race on
    /// typical hardware; smaller values still leak presses where
    /// the ROM's GETKY happened to enter its scan loop just before
    /// the shift state updated.
    /// </summary>
    protected const int LiveShiftStageFrames = 2;

    /// <summary>Bit N = 1 means the OS scanned row N since the auto-typer last cleared the mask.</summary>
    private int _scanMask;

    public KeyboardDiagnostics Diag { get; } = new();

    protected KeyboardMatrixBase()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
    }

    /// <summary>
    /// Matrix coordinates of MZ SHIFT on this machine. MZ-700 is
    /// (8, 0); MZ-80A is (0, 0). Used by
    /// <see cref="ApplyShiftState"/> so the base doesn't have to
    /// know which machine it's serving.
    /// </summary>
    protected abstract (int Row, int Col) ShiftSlot { get; }

    /// <summary>
    /// Called after <see cref="SetMatrix"/> has written the effective
    /// shift bit to <see cref="ShiftSlot"/>. Concrete keyboards
    /// override to plug in machine-specific side effects — MZ-700
    /// mirrors to <c>Memory.Ram[$1170]</c>; MZ-80A is a no-op
    /// (SA-1510 has no equivalent RAM mirror).
    /// </summary>
    protected abstract void OnShiftStateChanged(bool effective);

    public virtual byte ReadRow(int strobe)
    {
        if (strobe < 0 || strobe > 9) return 0xFF;
        _scanMask |= 1 << strobe;
        Diag.LastScanRow = strobe;
        return _rows[strobe];
    }

    /// <summary>
    /// Side-effect-free row read for diagnostic UIs — does not
    /// touch the auto-typer's scan mask or the diagnostic's
    /// last-scanned-row.
    /// </summary>
    public byte PeekMatrixRow(int row) =>
        (row < 0 || row > 9) ? (byte)0xFF : _rows[row];

    public void SetMatrix(int row, int col, bool pressed)
    {
        if (row < 0 || row > 9 || col < 0 || col > 7) return;
        byte mask = (byte)(1 << col);
        if (pressed) _rows[row] &= (byte)~mask;
        else _rows[row] |= mask;
    }

    /// <summary>
    /// Release every held bit, clear holds + staged presses + the
    /// pending KeyDown. Machine subclasses override to reset any
    /// additional per-machine state (e.g. MZ-80A GraphMode).
    /// </summary>
    public virtual void ReleaseAll()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
        _holds.Clear();
        _pendingDownVk = Keys.None;
        _stagedKeyBits.Clear();
    }

    /// <summary>
    /// Write the current <see cref="EffectiveMzShift"/> value to
    /// the shift matrix bit and fire the
    /// <see cref="OnShiftStateChanged"/> hook so per-machine
    /// mirrors stay in sync.
    /// </summary>
    protected void ApplyShiftState()
    {
        bool effective = EffectiveMzShift();
        var slot = ShiftSlot;
        SetMatrix(slot.Row, slot.Col, effective);
        OnShiftStateChanged(effective);
    }

    /// <summary>
    /// Resolve the MZ shift bit's desired state.
    /// - Any active hold with an explicit MzShift requirement wins
    ///   (e.g. UK Shift+' → '@' wants shift OFF even though PC
    ///   Shift is held).
    /// - Otherwise, if at least one hold is active, fall through
    ///   to PC shift state (lets Shift+arrow assert MZ shift on
    ///   the arrow's SpecialKey hold).
    /// - With no holds at all, returns false unconditionally. PC
    ///   Shift held alone does NOT raise the MZ shift bit: the MZ
    ///   only cares about shift state when a key is also being
    ///   pressed, and asserting it between presses opens a window
    ///   where the ROM's GETKY can cache "shift held" and apply it
    ///   to the next press even when that press's MzShift
    ///   requirement disagrees.
    /// </summary>
    protected bool EffectiveMzShift()
    {
        bool anyHold = false;
        foreach (var h in _holds.Values)
        {
            if (h.ExplicitMzShift.HasValue) return h.ExplicitMzShift.Value;
            anyHold = true;
        }
        return anyHold && _pcShift;
    }

    protected static bool IsShiftKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;

    /// <summary>
    /// Per-frame progression of staged shifted-key presses.
    /// Decrements each entry's countdown; when it hits zero the
    /// key bit lands (if the hold's still live). Skips presses
    /// whose hold has already been released — a press that arrives
    /// and releases inside the stage window simply doesn't
    /// register, which is preferable to mis-translating it.
    /// </summary>
    public void TickStagedKeyBits()
    {
        if (_stagedKeyBits.Count == 0) return;
        for (int i = _stagedKeyBits.Count - 1; i >= 0; i--)
        {
            var s = _stagedKeyBits[i];
            int left = s.FramesLeft - 1;
            if (left > 0)
            {
                _stagedKeyBits[i] = s with { FramesLeft = left };
                continue;
            }
            if (_holds.ContainsKey(s.Vk))
                SetMatrix(s.Row, s.Col, true);
            _stagedKeyBits.RemoveAt(i);
        }
    }

    // Two thin scan-observation accessors used by the MZ-700
    // auto-typer to detect whether the ROM's scan loop has actually
    // read the row a press was asserted on. Internal so external
    // callers can't peek at raw scan state.
    internal bool WasStrobeScanned(int strobe) => (_scanMask & (1 << strobe)) != 0;
    internal void ClearScanObservation() => _scanMask = 0;
}
