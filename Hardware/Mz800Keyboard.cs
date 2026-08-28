using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 keyboard matrix. Same 10-strobe × 8-bit active-low shape as
/// MZ-700 / MZ-80A. Populated from tech-ref p. 25 via
/// <see cref="Mz800MatrixReference"/> (walk-validated with the user
/// 2026-08-28).
///
/// Input model closely mirrors <see cref="Keyboard"/> (MZ-700's), not
/// <see cref="Mz80aKeyboard"/> — MZ-800 in MZ-700 mode runs the 1Z-013B
/// monitor which is a superset of MZ-700's own 1Z-013B monitor, and it
/// uses the same $1170 RAM location as a PC-shift mirror the GETKY
/// routine checks (bit 0) to pick between unshifted and shifted
/// keyboard tables. Without this shortcut, shifted alphanumerics would
/// be unreachable.
///
/// Layered lookup in <see cref="OnKeyDown"/>:
///   1. <see cref="Mz800SpecialKeyMap"/> — built-in non-character keys
///      (cursors, F1-F5, Enter, TAB, Esc/BREAK, GRAPH, ALPHA, MZ CTRL).
///   2. Defer to <see cref="OnKeyPress"/> for printables, which
///      consults <see cref="Mz800CharMap"/> with the resolved Unicode
///      character.
///
/// Shift-race handling and staged-key-bit logic come from
/// <see cref="KeyboardMatrixBase"/> unchanged — the pattern is
/// vocabulary-independent.
///
/// Phase 3 scope: bring live PC typing to the MZ-800 boot menu (C /
/// M / Enter / arrows / basic alphanumerics). Auto-typer + user
/// overrides + settings integration land in Phase 4 / Phase 8.
/// </summary>
public sealed class Mz800Keyboard : KeyboardMatrixBase
{
    /// <summary>
    /// Direct hook into RAM so we can mirror PC shift-key state at
    /// $1170, which the MZ-800's MZ-700-mode monitor (1Z-013B family)
    /// checks in GETKY to choose between unshifted / shifted keyboard
    /// tables. Wired by <see cref="MZ800"/> at construction.
    /// </summary>
    public MZ800Memory? Memory;

    protected override (int Row, int Col) ShiftSlot => (8, 0);

    protected override void OnShiftStateChanged(bool effective)
    {
        if (Memory != null) Memory.Ram[0x1170] = (byte)(effective ? 0x01 : 0x00);
    }

    /// <summary>
    /// PC KeyDown. Returns true if the form should consider the event
    /// handled. Mirrors <see cref="Keyboard.OnKeyDown"/>:
    ///   - Auto-repeat: if the VK is already held, no-op return true.
    ///   - SpecialKeyMap match: drive matrix directly, pass PC shift
    ///     through (no explicit shift requirement on the hold).
    ///   - Bare Shift key: don't touch shift state yet — the next
    ///     press's char-map entry may want to override it. Deferring
    ///     lets <see cref="KeyboardMatrixBase.EffectiveMzShift"/>
    ///     resolve at press-time.
    ///   - Otherwise: defer to OnKeyPress so the resolved character
    ///     drives the CharMap lookup.
    /// </summary>
    public bool OnKeyDown(Keys keyData, bool pcShift)
    {
        _pcShift = pcShift;
        var bareVk = keyData & Keys.KeyCode;
        if (_holds.ContainsKey(bareVk)) return true;

        Diag.LastKeyDown = keyData;

        if (Mz800SpecialKeyMap.Map.TryGetValue(bareVk, out var sp))
        {
            _holds[bareVk] = new ActiveHold(sp.Strobe, sp.Bit, sp.ExplicitMzShift);
            ApplyShiftState();
            if (sp.ExplicitMzShift == true)
            {
                // Stage the key bit the same way CharMap shifted
                // presses do — assert SHIFT now via ApplyShiftState,
                // let the ROM scan pick it up, THEN drop the key
                // bit. No SpecialKeyMap entries currently need this
                // for MZ-800 but the branch keeps parity with
                // Mz80aKeyboard so future overrides work uniformly.
                _stagedKeyBits.Add(new StagedPress(bareVk, sp.Strobe, sp.Bit, LiveShiftStageFrames));
            }
            else
            {
                SetMatrix(sp.Strobe, sp.Bit, true);
            }
            Diag.Record(InputLayer.SpecialKey, sp.Strobe, sp.Bit, sp.ExplicitMzShift);
            return true;
        }

        if (IsShiftKey(bareVk)) return false;

        _pendingDownVk = bareVk;
        return false;
    }

    /// <summary>
    /// PC KeyPress. Pairs the resolved Unicode char with the pending
    /// KeyDown VK and asserts the corresponding matrix bits. Write
    /// order: SHIFT state (+ $1170 mirror) BEFORE the key bit so any
    /// GETKY scan that picks up the key bit also reads the matching
    /// shift state.
    /// </summary>
    public void OnKeyPress(char ch)
    {
        Diag.LastKeyChar = ch;
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;

        if (!Mz800CharMap.TryLookup(ch, out var p))
        {
            Diag.Record(InputLayer.None, -1, -1, null);
            return;
        }

        _holds[vk] = new ActiveHold(p.Strobe, p.Bit, ExplicitMzShift: p.MzShift);
        ApplyShiftState();

        if (p.MzShift)
        {
            // Stage the key bit so any in-flight GETKY that cached
            // $1170 at entry has time to complete before our updated
            // shift state combines with the key bit. Same rationale
            // as MZ-700's live-typing race fix.
            _stagedKeyBits.Add(new StagedPress(vk, p.Strobe, p.Bit, LiveShiftStageFrames));
        }
        else
        {
            SetMatrix(p.Strobe, p.Bit, true);
        }
        Diag.Record(InputLayer.Character, p.Strobe, p.Bit, p.MzShift);
    }

    /// <summary>
    /// PC KeyUp. Releases whichever matrix bits this VK's KeyDown
    /// asserted, drops any pending staged press for this VK, then
    /// recomputes MZ shift state from remaining holds + PC shift.
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
        _stagedKeyBits.RemoveAll(p => p.Vk == bareVk);
        ApplyShiftState();
        return handled;
    }
}
