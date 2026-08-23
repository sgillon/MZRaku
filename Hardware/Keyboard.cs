using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-700 keyboard matrix. The 8255 PPI writes a strobe value to
/// Port A to select a row (0..9), and reads the 8 column bits from
/// Port B. Pressed keys read as 0 (active low).
///
/// Input model: we drive the matrix from the resolved CHARACTER a
/// PC keystroke produces, not from a configurable per-VK map. The
/// host OS handles keyboard layout, AltGr, dead keys, etc. and
/// gives us a Unicode char via WinForms KeyPress; we look that char
/// up in CharMap to find the (row, col) and whether MZ shift must
/// be held, then assert the matrix bits for the duration of the PC
/// keystroke.
///
/// Non-character keys (cursor, function, Enter, Esc, Backspace,
/// Insert, MZ Ctrl) take a separate path through SpecialKeyMap on
/// KeyDown, since they don't fire KeyPress.
///
/// v1.2 audit F-015 / F-021 hoisted the matrix backing, hold
/// bookkeeping, shift-race staging, and effective-shift rule into
/// <see cref="KeyboardMatrixBase"/>. This class holds only the
/// MZ-700-specific bits: the $1170 RAM mirror, the CharMap lookup,
/// and the scan-detection auto-typer.
/// </summary>
public sealed class Keyboard : KeyboardMatrixBase
{
    // Direct hook into RAM so we can mirror PC shift-key state at
    // $1170, which the monitor's GETKY checks (bit 0) to choose
    // between the unshifted table at $0BEA and the shifted table
    // at $0C2A. Neither the monitor ROM nor S-BASIC ever writes to
    // $1170, so without this shortcut shifted alphanumerics would
    // be unreachable.
    public MZ700Memory? Memory;

    // User-editable physical-key overrides, consulted before
    // SpecialKeyMap and CharMap in OnKeyDown. Null = no overrides
    // loaded; behaviour matches pre-layered code.
    public KeyOverride? Overrides;

    /// <summary>
    /// Auto-typer companion (v1.2 audit F-016 extraction). Owns
    /// the press queue + phased state machine that drives the
    /// matrix from scripted input (CLI --basic autorun, Font Sheet
    /// click-to-type).
    /// </summary>
    public readonly KeyboardAutoTyper AutoType;

    public Keyboard()
    {
        AutoType = new KeyboardAutoTyper(this);
    }

    protected override (int Row, int Col) ShiftSlot => (8, 0);

    protected override void OnShiftStateChanged(bool effective)
    {
        if (Memory != null) Memory.Ram[0x1170] = (byte)(effective ? 0x01 : 0x00);
    }

    /// <summary>
    /// PC KeyDown. Returns true if the form should consider the
    /// event handled. <paramref name="keyData"/> is the WinForms
    /// combined VK + modifier flags (e.g. <c>Control | G</c>) so
    /// the override layer can match modifier-aware bindings; the
    /// bare VK is used as the <see cref="_holds"/> key so KeyUp's
    /// bare VK still matches.
    ///
    /// Layered lookup order:
    ///   1. <see cref="Overrides"/> — user-editable physical-key
    ///      map, modifier-aware (combined key wins over bare).
    ///   2. <see cref="SpecialKeyMap"/> — built-in non-character
    ///      keys (cursors, F-keys, Enter, Esc, GRAPH, ALPHA, MZ
    ///      Ctrl).
    ///   3. Defer to <see cref="OnKeyPress"/> for printables, which
    ///      consults <see cref="CharMap"/> with the resolved Unicode
    ///      character so host keyboard layout / AltGr / dead keys
    ///      all work transparently.
    ///
    /// Shift-race avoidance: the form no longer calls SetShift in
    /// its KeyDown handler. For non-shift, non-modifier character
    /// keys we don't touch matrix(8,0) or $1170 until OnKeyPress
    /// resolves the character — otherwise we'd write a
    /// PC-shift-derived value that disagrees with the upcoming
    /// press's MzShift requirement (UK `'` → MZ-shift ON; UK
    /// Shift+`'` → `@` → MZ-shift OFF), and the ROM's GETKY can
    /// cache the wrong shift state across frames in the gap
    /// between the two events.
    /// </summary>
    public bool OnKeyDown(Keys keyData, bool pcShift)
    {
        _pcShift = pcShift;
        var bareVk = keyData & Keys.KeyCode;
        if (_holds.ContainsKey(bareVk)) return true; // auto-repeat: bit already held

        Diag.LastKeyDown = keyData;

        // Layer 1: user overrides. MzShift can be true/false/null
        // and the ActiveHold honours each — see EffectiveMzShift's
        // null check. Write shift state BEFORE the key bit so any
        // GETKY observation that picks up the key bit also picks up
        // the matching shift.
        var ov = Overrides?.Resolve(keyData);
        if (ov.HasValue)
        {
            var b = ov.Value;
            _holds[bareVk] = new ActiveHold(b.Row, b.Col, b.MzShift);
            ApplyShiftState();
            SetMatrix(b.Row, b.Col, true);
            Diag.Record(InputLayer.Override, b.Row, b.Col, b.MzShift);
            return true;
        }

        // Layer 2: built-in defaults (bare VK only).
        if (SpecialKeyMap.Map.TryGetValue(bareVk, out var rc))
        {
            // Non-printable: drive matrix directly, pass PC shift
            // through (no explicit shift requirement on this hold).
            _holds[bareVk] = new ActiveHold(rc.row, rc.col, ExplicitMzShift: null);
            ApplyShiftState();
            SetMatrix(rc.row, rc.col, true);
            Diag.Record(InputLayer.SpecialKey, rc.row, rc.col, null);
            return true;
        }

        // Shift key alone: don't write matrix(8,0) or $1170 yet.
        // The modifier state is tracked in _pcShift and surfaces
        // on the next press via EffectiveMzShift's "any-hold
        // fall-through". Asserting it here would leave the OS with
        // a "shift held" observation in the gap before the next
        // press lands, and the ROM's GETKY can cache that across
        // frames — producing the very mismatch we're trying to
        // close.
        if (IsShiftKey(bareVk))
            return false;

        // Layer 3: defer to KeyPress for character-driven mapping.
        // Deliberately do NOT touch matrix(8,0) / $1170 here — see
        // the shift-race note in the XML doc above.
        _pendingDownVk = bareVk;
        return false;
    }

    /// <summary>
    /// PC KeyPress. Pairs the resolved Unicode char with the
    /// pending KeyDown VK and asserts the corresponding matrix bits.
    ///
    /// Write order matters here: matrix(8,0) and $1170 are written
    /// BEFORE the key bit so any GETKY scan that picks up the key
    /// bit also reads the matching shift state — the auto-typer's
    /// AwaitShiftScan staging exists for the same reason on its
    /// side. Combined with <see cref="OnKeyDown"/> no longer
    /// touching shift state on character keys, this closes the
    /// cross-frame race where the gap between OnKeyDown and
    /// OnKeyPress could leave $1170 at a value that didn't match
    /// the press's MzShift.
    /// </summary>
    public void OnKeyPress(char ch)
    {
        Diag.LastKeyChar = ch;
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;

        if (!CharMap.TryLookup(ch, out var p))
        {
            Diag.Record(InputLayer.None, -1, -1, null);
            return;
        }

        // Record the hold with an EXPLICIT shift requirement so any
        // subsequent EffectiveMzShift reads (e.g. on shift-key
        // release) don't clobber our override.
        _holds[vk] = new ActiveHold(p.Row, p.Col, ExplicitMzShift: p.MzShift);
        ApplyShiftState();

        if (p.MzShift)
        {
            // Any press that requires MZ shift goes through the
            // staged path: hold the key bit back for a couple of
            // frames so any in-flight GETKY routine that cached
            // $1170 at routine entry has time to complete and a
            // fresh one to start with our updated value. The
            // canonical race this closes is unshifted PC ' (UK
            // layout) translating to MZ 7 when GETKY's cached
            // shift = 0 catches a key bit asserted in the same tick.
            _stagedKeyBits.Add(new StagedPress(vk, p.Row, p.Col, LiveShiftStageFrames));
        }
        else
        {
            SetMatrix(p.Row, p.Col, true);
        }
        Diag.Record(InputLayer.Character, p.Row, p.Col, p.MzShift);
    }

    /// <summary>
    /// PC KeyUp. Releases whichever matrix bits this VK's KeyDown
    /// asserted, then recomputes the MZ shift bit from remaining
    /// holds and the user's actual PC shift state.
    /// <paramref name="keyData"/> is the combined VK + modifiers;
    /// the bare VK is what's keyed in <see cref="_holds"/>.
    ///
    /// Always recomputes shift state at the end — even when no
    /// hold matched the released VK — because the form no longer
    /// calls SetShift on shift-key release: that path now flows
    /// through here, with <c>pcShift</c> already reflecting the
    /// post-release modifier state.
    /// </summary>
    public bool OnKeyUp(Keys keyData, bool pcShift)
    {
        _pcShift = pcShift;
        Diag.LastKeyUp = keyData;
        var bareVk = keyData & Keys.KeyCode;
        if (_pendingDownVk == bareVk) _pendingDownVk = Keys.None;

        bool handled = false;
        if (_holds.TryGetValue(bareVk, out var h))
        {
            SetMatrix(h.Row, h.Col, false);
            _holds.Remove(bareVk);
            handled = true;
        }
        // If this VK had a staged key bit still waiting, drop it —
        // the press was never asserted, so there's nothing for the
        // OS to have seen.
        _stagedKeyBits.RemoveAll(p => p.Vk == bareVk);

        // Shift-key release lands here without a matching hold
        // (shift alone doesn't go in _holds). Recompute
        // unconditionally so matrix(8,0) and $1170 mirror the new
        // PC modifier state.
        ApplyShiftState();
        return handled;
    }
}
