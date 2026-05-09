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

    public byte ReadRow(int strobe)
    {
        if (strobe < 0 || strobe > 9) return 0xFF;
        return _rows[strobe];
    }

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
    /// Called from the form on every key event so SHIFT bit (8,0) tracks
    /// the user's actual shift-held state, except while a char-driven
    /// hold has an explicit MZ-shift requirement.
    /// </summary>
    public void SetShift(bool held)
    {
        _pcShift = held;
        SetMatrix(8, 0, EffectiveMzShift());
        if (Memory != null) Memory.Ram[0x1170] = (byte)(EffectiveMzShift() ? 0x01 : 0x00);
    }

    /// <summary>
    /// Resolve the MZ shift bit's desired state. Any active hold with an
    /// explicit MzShift requirement wins (e.g. UK Shift+' → '@' wants
    /// shift OFF even though PC Shift is held); otherwise pass through
    /// PC shift state.
    /// </summary>
    private bool EffectiveMzShift()
    {
        foreach (var h in _holds.Values)
            if (h.ExplicitMzShift.HasValue) return h.ExplicitMzShift.Value;
        return _pcShift;
    }

    /// <summary>
    /// PC KeyDown. Returns true if the form should consider the event
    /// handled. For printable keys we defer to OnKeyPress (which has the
    /// resolved Unicode char); for special keys we drive the matrix here.
    /// </summary>
    public bool OnKeyDown(Keys vk, bool pcShift)
    {
        _pcShift = pcShift;
        if (_holds.ContainsKey(vk)) return true; // auto-repeat: bit already held

        if (SpecialKeyMap.Map.TryGetValue(vk, out var rc))
        {
            // Non-printable: drive matrix directly, pass PC shift through
            // (no explicit shift requirement on this hold).
            _holds[vk] = new ActiveHold(rc.row, rc.col, ExplicitMzShift: null);
            SetMatrix(rc.row, rc.col, true);
            SetMatrix(8, 0, EffectiveMzShift());
            return true;
        }

        // Printable: park the VK and wait for KeyPress to deliver the char.
        _pendingDownVk = vk;
        return false;
    }

    /// <summary>
    /// PC KeyPress. Pairs the resolved Unicode char with the pending
    /// KeyDown VK and asserts the corresponding matrix bits.
    /// </summary>
    public void OnKeyPress(char ch)
    {
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;

        if (!CharMap.TryLookup(ch, out var p)) return;

        // Record the hold with an EXPLICIT shift requirement so SetShift
        // (which fires on every subsequent key event while PC Shift is
        // held) doesn't clobber our override.
        _holds[vk] = new ActiveHold(p.Row, p.Col, ExplicitMzShift: p.MzShift);
        SetMatrix(p.Row, p.Col, true);
        SetMatrix(8, 0, EffectiveMzShift());
        if (Memory != null) Memory.Ram[0x1170] = (byte)(p.MzShift ? 0x01 : 0x00);
    }

    /// <summary>
    /// PC KeyUp. Releases whichever matrix bits this VK's KeyDown
    /// asserted, then recomputes the MZ shift bit from remaining holds
    /// and the user's actual PC shift state.
    /// </summary>
    public bool OnKeyUp(Keys vk, bool pcShift)
    {
        _pcShift = pcShift;
        if (_pendingDownVk == vk) _pendingDownVk = Keys.None;

        if (!_holds.TryGetValue(vk, out var h)) return false;

        SetMatrix(h.Row, h.Col, false);
        _holds.Remove(vk);

        // Recompute MZ shift from remaining holds + PC state.
        bool effective = EffectiveMzShift();
        SetMatrix(8, 0, effective);
        if (Memory != null) Memory.Ram[0x1170] = (byte)(effective ? 0x01 : 0x00);
        return true;
    }

    // ---- Auto-typing (used by CLI auto-load to send "RUN\r" etc.) -------
    //
    // Re-uses the same CharMap so the live and auto-typed paths agree on
    // every glyph mapping. Each queued press holds the matrix bits for a
    // few ticks then releases them; the loader polls TickAutoType per
    // frame.
    private readonly Queue<CharMap.Press> _typeQueue = new();
    private int _typeTimer;
    private CharMap.Press? _current;

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

    public void TickAutoType()
    {
        if (_current.HasValue)
        {
            _typeTimer--;
            if (_typeTimer <= 0)
            {
                var p = _current.Value;
                SetMatrix(p.Row, p.Col, false);
                if (p.MzShift)
                {
                    SetMatrix(8, 0, false);
                    if (Memory != null) Memory.Ram[0x1170] = 0x00;
                }
                _current = null;
                _typeTimer = 4;
            }
            return;
        }

        if (_typeTimer > 0) { _typeTimer--; return; }

        if (_typeQueue.Count > 0)
        {
            var p = _typeQueue.Dequeue();
            if (p.MzShift)
            {
                SetMatrix(8, 0, true);
                if (Memory != null) Memory.Ram[0x1170] = 0x01;
            }
            SetMatrix(p.Row, p.Col, true);
            _current = p;
            _typeTimer = 4;
        }
    }
}
