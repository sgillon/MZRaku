using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// MZ-700 keyboard matrix. The 8255 PPI writes a strobe value to Port A
/// to select a row (0..9), and reads the 8 column bits from Port B.
/// Pressed keys read as 0 (active low).
///
/// Input model (post-refactor): we drive the matrix from the resolved
/// CHARACTER a PC keystroke produces, not from a configurable per-VK
/// map. The host OS handles keyboard layout, AltGr, dead keys, etc. and
/// gives us a Unicode char via WinForms KeyPress; we look that char up
/// in CharMap to find the (row, col) and whether MZ shift must be held,
/// then assert the matrix bits for the duration of the PC keystroke.
///
/// Non-character keys (cursor, function, Enter, Esc, Backspace, Insert,
/// MZ Ctrl) take a separate path through SpecialKeyMap on KeyDown,
/// since they don't fire KeyPress.
/// </summary>
public sealed class Keyboard
{
    private readonly byte[] _rows = new byte[10];

    // Direct hook into RAM so we can mirror PC shift-key state at $1170,
    // which the monitor's GETKY checks (bit 0) to choose between the
    // unshifted table at $0BEA and the shifted table at $0C2A. Neither
    // the monitor ROM nor S-BASIC ever writes to $1170, so without this
    // shortcut shifted alphanumerics would be unreachable.
    public MZ700Memory? Memory;

    // User-editable physical-key overrides, consulted before SpecialKeyMap
    // and CharMap in OnKeyDown. Null = no overrides loaded; behaviour
    // matches pre-layered code.
    public KeyOverride? Overrides;

    // Live telemetry the HID diagnostic window reads each frame. Populated
    // here so the diagnostic doesn't need to subscribe to events or duplicate
    // mapping logic.
    public readonly KeyboardDiagnostics Diag = new();

    // True PC shift state; tracked so KeyUp can recompute the MZ shift
    // bit after a char-driven hold ends.
    private bool _pcShift;

    // Active matrix-bit holds, keyed by the originating PC virtual key.
    // Lets KeyUp release exactly the bits its KeyDown asserted, even if
    // shift state changed between down and up.
    //
    // ExplicitMzShift:
    //   true  → hold requires MZ shift bit (8,0) SET while held
    //   false → hold requires MZ shift bit (8,0) CLEAR while held
    //   null  → no preference, pass through PC shift state
    //
    // Both true and false are "overrides" — without the false override,
    // an unshifted char produced via PC Shift (e.g. UK Shift+' → '@')
    // gets clobbered the next time SetShift fires with PC Shift held.
    private record ActiveHold(int Row, int Col, bool? ExplicitMzShift);
    private readonly Dictionary<Keys, ActiveHold> _holds = new();

    // The most recent KeyDown VK that's expected to produce a printable
    // character. KeyPress fires next with the resolved char; we pair it
    // back to this VK so KeyUp knows which matrix bits to release.
    private Keys _pendingDownVk = Keys.None;

    public Keyboard()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
    }

    // Bit N set means the OS has scanned row N since the auto-typer last
    // cleared the mask. The auto-typer uses this to release a key only
    // after the OS has actually observed it (rather than holding for a
    // hardcoded number of host frames and hoping).
    private int _scanMask;

    public byte ReadRow(int strobe)
    {
        if (strobe < 0 || strobe > 9) return 0xFF;
        _scanMask |= 1 << strobe;
        Diag.LastScanRow = strobe;
        return _rows[strobe];
    }

    /// <summary>
    /// Side-effect-free row read for diagnostic UIs — does not touch the
    /// auto-typer's scan mask or the diagnostic's last-scanned-row.
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

    public void ReleaseAll()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
        _holds.Clear();
        _pendingDownVk = Keys.None;
    }

    /// <summary>
    /// Reflect the PC's shift-modifier state into the MZ-700.
    /// Retained as a public API for callers that need to force a sync
    /// outside the OnKeyDown / OnKeyUp flow (none in-tree right now).
    /// The form no longer calls this on every key event — see the
    /// "shift-race" notes on <see cref="OnKeyDown"/>.
    /// </summary>
    public void SetShift(bool held)
    {
        _pcShift = held;
        ApplyShiftState();
    }

    /// <summary>
    /// Write the current <see cref="EffectiveMzShift"/> value to both
    /// the matrix bit (8,0) and the RAM mirror at $1170. Consolidates
    /// the two writes so any caller that updates <c>_pcShift</c> or
    /// <c>_holds</c> can sync the MZ-visible state with one line.
    /// </summary>
    private void ApplyShiftState()
    {
        bool effective = EffectiveMzShift();
        SetMatrix(8, 0, effective);
        if (Memory != null) Memory.Ram[0x1170] = (byte)(effective ? 0x01 : 0x00);
    }

    private static bool IsShiftKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;

    /// <summary>
    /// Resolve the MZ shift bit's desired state.
    /// - Any active hold with an explicit MzShift requirement wins
    ///   (e.g. UK Shift+' → '@' wants shift OFF even though PC Shift
    ///   is held).
    /// - Otherwise, if at least one hold is active, fall through to PC
    ///   shift state (lets Shift+arrow assert MZ shift on the arrow's
    ///   SpecialKey hold).
    /// - With no holds at all, returns false unconditionally. PC Shift
    ///   held alone does NOT raise the MZ shift bit: the MZ only cares
    ///   about shift state when a key is also being pressed, and
    ///   asserting it between presses opens a window where the ROM's
    ///   GETKY can cache "shift held" and apply it to the next press
    ///   even when that press's MzShift requirement disagrees.
    /// </summary>
    private bool EffectiveMzShift()
    {
        bool anyHold = false;
        foreach (var h in _holds.Values)
        {
            if (h.ExplicitMzShift.HasValue) return h.ExplicitMzShift.Value;
            anyHold = true;
        }
        return anyHold && _pcShift;
    }

    /// <summary>
    /// PC KeyDown. Returns true if the form should consider the event
    /// handled. <paramref name="keyData"/> is the WinForms combined
    /// VK + modifier flags (e.g. <c>Control | G</c>) so the override
    /// layer can match modifier-aware bindings; the bare VK is used as
    /// the <see cref="_holds"/> key so KeyUp's bare VK still matches.
    ///
    /// Layered lookup order:
    ///   1. <see cref="Overrides"/> — user-editable physical-key map,
    ///      modifier-aware (combined key wins over bare).
    ///   2. <see cref="SpecialKeyMap"/> — built-in non-character keys
    ///      (cursors, F-keys, Enter, Esc, GRAPH, ALPHA, MZ Ctrl).
    ///   3. Defer to <see cref="OnKeyPress"/> for printables, which
    ///      consults <see cref="CharMap"/> with the resolved Unicode
    ///      character so host keyboard layout / AltGr / dead keys all
    ///      work transparently.
    ///
    /// Shift-race avoidance: the form no longer calls SetShift in its
    /// KeyDown handler. For non-shift, non-modifier character keys we
    /// don't touch matrix(8,0) or $1170 until OnKeyPress resolves the
    /// character — otherwise we'd write a PC-shift-derived value that
    /// disagrees with the upcoming press's MzShift requirement (UK
    /// `'` → MZ-shift ON; UK Shift+`'` → `@` → MZ-shift OFF), and the
    /// ROM's GETKY can cache the wrong shift state across frames in
    /// the gap between the two events.
    /// </summary>
    public bool OnKeyDown(Keys keyData, bool pcShift)
    {
        _pcShift = pcShift;
        var bareVk = keyData & Keys.KeyCode;
        if (_holds.ContainsKey(bareVk)) return true; // auto-repeat: bit already held

        Diag.LastKeyDown = keyData;

        // Layer 1: user overrides. MzShift can be true/false/null and the
        // ActiveHold honours each — see EffectiveMzShift's null check.
        // Write shift state BEFORE the key bit so any GETKY observation
        // that picks up the key bit also picks up the matching shift.
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
            // Non-printable: drive matrix directly, pass PC shift through
            // (no explicit shift requirement on this hold).
            _holds[bareVk] = new ActiveHold(rc.row, rc.col, ExplicitMzShift: null);
            ApplyShiftState();
            SetMatrix(rc.row, rc.col, true);
            Diag.Record(InputLayer.SpecialKey, rc.row, rc.col, null);
            return true;
        }

        // Shift key alone: don't write matrix(8,0) or $1170 yet. The
        // modifier state is tracked in _pcShift and surfaces on the
        // next press via EffectiveMzShift's "any-hold fall-through".
        // Asserting it here would leave the OS with a "shift held"
        // observation in the gap before the next press lands, and the
        // ROM's GETKY can cache that across frames — producing the
        // very mismatch we're trying to close.
        if (IsShiftKey(bareVk))
            return false;

        // Layer 3: defer to KeyPress for character-driven mapping.
        // Deliberately do NOT touch matrix(8,0) / $1170 here — see the
        // shift-race note in the XML doc above.
        _pendingDownVk = bareVk;
        return false;
    }

    /// <summary>
    /// PC KeyPress. Pairs the resolved Unicode char with the pending
    /// KeyDown VK and asserts the corresponding matrix bits.
    ///
    /// Write order matters here: matrix(8,0) and $1170 are written
    /// BEFORE the key bit so any GETKY scan that picks up the key bit
    /// also reads the matching shift state — the auto-typer's
    /// AwaitShiftScan staging exists for the same reason on its side.
    /// Combined with <see cref="OnKeyDown"/> no longer touching shift
    /// state on character keys, this closes the cross-frame race
    /// where the gap between OnKeyDown and OnKeyPress could leave
    /// $1170 at a value that didn't match the press's MzShift.
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
        // subsequent EffectiveMzShift reads (e.g. on shift-key release)
        // don't clobber our override.
        _holds[vk] = new ActiveHold(p.Row, p.Col, ExplicitMzShift: p.MzShift);
        ApplyShiftState();
        SetMatrix(p.Row, p.Col, true);
        Diag.Record(InputLayer.Character, p.Row, p.Col, p.MzShift);
    }

    /// <summary>
    /// PC KeyUp. Releases whichever matrix bits this VK's KeyDown
    /// asserted, then recomputes the MZ shift bit from remaining holds
    /// and the user's actual PC shift state. <paramref name="keyData"/>
    /// is the combined VK + modifiers; the bare VK is what's keyed in
    /// <see cref="_holds"/>.
    ///
    /// Always recomputes shift state at the end — even when no hold
    /// matched the released VK — because the form no longer calls
    /// SetShift on shift-key release: that path now flows through
    /// here, with <c>pcShift</c> already reflecting the post-release
    /// modifier state.
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

        // Shift-key release lands here without a matching hold (shift
        // alone doesn't go in _holds). Recompute unconditionally so
        // matrix(8,0) and $1170 mirror the new PC modifier state.
        ApplyShiftState();
        return handled;
    }

    // ---- Auto-typing (used by CLI auto-load to send "RUN\r" etc.) -------
    //
    // Re-uses the same CharMap so the live and auto-typed paths agree on
    // every glyph mapping. Driven by detection rather than blind hold
    // counts (see _scanMask above). TickAutoType is polled per frame by
    // MZ700.RunFrame.
    //
    // Shifted keys are staged: shift bit is asserted first, we wait for
    // the OS to actually scan row 8 (so it has the shift state on file),
    // THEN we assert the key. Without this, the OS can capture a key-down
    // observation before its first scan of row 8 with our bit set, and
    // permanently mis-classify the press as unshifted.
    private readonly Queue<CharMap.Press> _typeQueue = new();
    private CharMap.Press? _current;
    private enum AutoPhase
    {
        Idle,
        AwaitShiftScan,   // shifted keys only — wait for OS to see shift
        AwaitKeyScan,     // wait for OS to see the key (with shift if any)
        AwaitRelease,     // wait for OS to see key-up
        EnterCooldown     // BASIC line-parse delay after Enter
    }
    private AutoPhase _phase;
    private int _phaseFramesLeft;

    // Safety net: if the OS isn't scanning the keyboard (e.g. interrupts
    // masked, mid-routine), don't wait forever. ~10 host frames (~167ms)
    // is well under the old 12-frame fixed hold but generous enough to
    // cover any realistic gap between scan bursts.
    private const int ScanTimeoutFrames = 10;
    // After Enter, BASIC tokenises and inserts the line; the scan-loop
    // pauses during that work. Hold this fixed cooldown to give BASIC
    // headroom before the next press lands. Empirical, same as before.
    private const int EnterCooldownFrames = 30;

    public void TypeString(string s)
    {
        foreach (char ch in s) TypeChar(ch);
    }

    public void TypeChar(char ch)
    {
        // CR/LF aren't in CharMap (Enter is a special key); translate here.
        if (ch == '\r' || ch == '\n')
        {
            _typeQueue.Enqueue(new CharMap.Press(0, 0, false));
            return;
        }
        if (CharMap.TryLookup(ch, out var p)) _typeQueue.Enqueue(p);
    }

    /// <summary>
    /// Queue a raw matrix-position press for the auto-typer. Used to drive
    /// the keyboard from sources that don't go through a Unicode char —
    /// e.g. the Font Sheet's click-to-input flow, which knows the MZ
    /// display code but not necessarily its host-keyboard glyph.
    /// </summary>
    public void TypePress(CharMap.Press p) => _typeQueue.Enqueue(p);

    public void TickAutoType()
    {
        switch (_phase)
        {
            case AutoPhase.Idle:
            {
                if (_typeQueue.Count == 0) return;
                var p = _typeQueue.Dequeue();
                _current = p;
                // Set shift / $1170 to the press's required state in both
                // cases — false explicitly clears any stale state left by
                // a prior shifted press.
                SetMatrix(8, 0, p.MzShift);
                if (Memory != null) Memory.Ram[0x1170] = (byte)(p.MzShift ? 0x01 : 0x00);
                _scanMask = 0;
                if (p.MzShift)
                {
                    // Stage shift first; key follows once OS has scanned row 8.
                    _phase = AutoPhase.AwaitShiftScan;
                }
                else
                {
                    SetMatrix(p.Row, p.Col, true);
                    _phase = AutoPhase.AwaitKeyScan;
                }
                _phaseFramesLeft = ScanTimeoutFrames;
                break;
            }

            case AutoPhase.AwaitShiftScan:
            {
                bool observed = (_scanMask & (1 << 8)) != 0;
                if (observed || --_phaseFramesLeft <= 0)
                {
                    var pa = _current!.Value;
                    SetMatrix(pa.Row, pa.Col, true);
                    _scanMask = 0;
                    _phase = AutoPhase.AwaitKeyScan;
                    _phaseFramesLeft = ScanTimeoutFrames;
                }
                break;
            }

            case AutoPhase.AwaitKeyScan:
            {
                var pa = _current!.Value;
                bool observed = (_scanMask & (1 << pa.Row)) != 0;
                if (observed || --_phaseFramesLeft <= 0)
                {
                    // Release both key and (any) shift together.
                    SetMatrix(pa.Row, pa.Col, false);
                    if (pa.MzShift)
                    {
                        SetMatrix(8, 0, false);
                        if (Memory != null) Memory.Ram[0x1170] = 0x00;
                    }
                    _scanMask = 0;
                    _phase = AutoPhase.AwaitRelease;
                    _phaseFramesLeft = ScanTimeoutFrames;
                }
                break;
            }

            case AutoPhase.AwaitRelease:
            {
                var pa = _current!.Value;
                bool observed = (_scanMask & (1 << pa.Row)) != 0;
                if (observed || --_phaseFramesLeft <= 0)
                {
                    if (pa.Row == 0 && pa.Col == 0)
                    {
                        _phase = AutoPhase.EnterCooldown;
                        _phaseFramesLeft = EnterCooldownFrames;
                    }
                    else
                    {
                        _current = null;
                        _phase = AutoPhase.Idle;
                    }
                }
                break;
            }

            case AutoPhase.EnterCooldown:
                if (--_phaseFramesLeft <= 0)
                {
                    _current = null;
                    _phase = AutoPhase.Idle;
                }
                break;
        }
    }
}

public enum InputLayer { None, Override, SpecialKey, Character }

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
