using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace MZ700Emul.Hardware;

/// <summary>
/// MZ-700 keyboard matrix. The 8255 PPI writes a strobe value to Port A to
/// select a row (0..9), and reads the 8 column bits from Port B. Pressed
/// keys read as 0 (active low).
///
/// The matrix below follows the layout from the Sharp MZ-700 technical
/// reference. For PC ergonomics we then map virtual keys to (row,col).
/// </summary>
public sealed class Keyboard
{
    private readonly byte[] _rows = new byte[10];

    // Direct hook into RAM so we can mirror PC shift-key state at $1170, which
    // the monitor's GETKY checks (bit 0) to choose between the unshifted table
    // at $0BEA and the shifted table at $0C2A. Neither the monitor ROM nor
    // S-BASIC ever writes to $1170, so without this shortcut shifted
    // alphanumerics ('=', '!', '(', ')', etc.) are unreachable.
    public MZ700Memory? Memory;
    private bool _lShift, _rShift;

    // PC keys with an active shift-override binding (force MZ shift OFF).
    // While any are held, MZ shift bit (8,0) stays cleared even if Windows
    // auto-repeats the Shift key.
    private readonly HashSet<Keys> _shiftOverrideKeys = new();
    // PC keys with an active shift-force binding (force MZ shift ON).
    // While any are held, MZ shift bit (8,0) stays set even if user is
    // not actually holding Shift on the PC.
    private readonly HashSet<Keys> _shiftForceKeys = new();

    public Keyboard()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
    }

    private void UpdateShiftFlag()
    {
        if (Memory == null) return;
        Memory.Ram[0x1170] = (byte)((_lShift || _rShift) ? 0x01 : 0x00);
    }

    /// <summary>
    /// Reflect the PC's true shift-modifier state into the MZ-700.
    /// Called from the form on every key event.
    ///
    /// SHIFT lives at matrix bit (8,0) (NOT (8,6) — that's a separate
    /// modifier that routes through the graphics-only $0CAA table). With
    /// (8,0) cleared and a key found, the ROM scan exits with D=$C0
    /// (bit 6 + bit 7 set), and GETKY then uses the populated $0C2A table
    /// containing '=', '!', '(', ')', lowercase letters, etc.
    ///
    /// S-BASIC's decoder at $0436 / $03F4 reads $0EE9 (row 8 raw snapshot)
    /// and checks BIT 0 (after the bit-6 fail) to decide whether to apply
    /// the shifted offset to its lookup table at $1322.
    /// </summary>
    public void SetShift(bool held)
    {
        _lShift = held;
        _rShift = false;
        // Note: do NOT set $1170. With matrix shift bit (8,0) cleared, the
        // ROM scan returns B=$C0 which routes GETKY through $08F3 → $0C2A
        // (the desired shifted-alphanumerics table). If $1170 were also set,
        // the path through $08FE would override and use $0CE9 (graphics).
        bool effective;
        if (_shiftOverrideKeys.Count > 0) effective = false;       // override forces OFF
        else if (_shiftForceKeys.Count > 0) effective = true;      // force forces ON
        else effective = held;                                     // pass through
        SetMatrix(8, 0, effective);
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
        _shiftOverrideKeys.Clear();
        _shiftForceKeys.Clear();
    }

    /// <summary>
    /// PC-virtual-key (with shift state) → MZ-700 (row, col) mapping.
    /// Populated from the loaded <see cref="KeyMapping"/> file (or the
    /// built-in default if no keymap.json exists). Shift-required bindings
    /// take priority and override the MZ shift bit so an unshifted MZ
    /// position can be reached via a shifted PC keystroke.
    /// Layout origin: ROM key-translation table at $0BEA in 1z-013a.rom;
    /// scan formula (from ROM scan routine at $0A50) is index = row*8 + (7 - col).
    /// </summary>
    public Dictionary<(Keys key, bool shift), (int row, int col, bool overrideShift, bool forceShifted)> Map { get; private set; } = KeyMapping.CreateDefault().ToKeysDictionary();

    /// <summary>Replace the live mapping (called when the user edits keymap).</summary>
    public void SetMapping(Dictionary<(Keys key, bool shift), (int row, int col, bool overrideShift, bool forceShifted)> map)
    {
        ReleaseAll();
        Map = map;
    }

    public bool OnKeyDown(Keys k, bool shift)
    {
        // Shift-required binding takes priority over the unshifted variant.
        // OverrideShift applies only when a (k, shift=true) binding fires —
        // it has no meaning for unshifted bindings. ForceShifted applies to
        // either kind of binding.
        if (shift && Map.TryGetValue((k, true), out var b))
        {
            if (b.forceShifted)
            {
                _shiftForceKeys.Add(k);
                SetMatrix(8, 0, true);
            }
            else if (b.overrideShift)
            {
                _shiftOverrideKeys.Add(k);
                SetMatrix(8, 0, false);
                if (Memory != null) Memory.Ram[0x1170] = 0;
            }
            SetMatrix(b.row, b.col, true);
            return true;
        }
        if (Map.TryGetValue((k, false), out b))
        {
            // Unshifted binding fired. OverrideShift is irrelevant here
            // (the binding doesn't require shift). ForceShifted still
            // applies — used when an unshifted PC key needs to produce a
            // SHIFTED MZ glyph (e.g. PC = → MZ '=' at shifted (6,5)).
            if (b.forceShifted)
            {
                _shiftForceKeys.Add(k);
                SetMatrix(8, 0, true);
            }
            SetMatrix(b.row, b.col, true);
            return true;
        }
        return false;
    }

    public bool OnKeyUp(Keys k, bool shift)
    {
        bool any = false;
        if (Map.TryGetValue((k, true), out var b)) { SetMatrix(b.row, b.col, false); any = true; }
        if (Map.TryGetValue((k, false), out b)) { SetMatrix(b.row, b.col, false); any = true; }
        // End any shift override/force held by this key. When the last is
        // released, restore MZ shift to actual PC shift state.
        bool overrideRemoved = _shiftOverrideKeys.Remove(k);
        bool forceRemoved = _shiftForceKeys.Remove(k);
        if ((overrideRemoved || forceRemoved) && _shiftOverrideKeys.Count == 0 && _shiftForceKeys.Count == 0)
            SetMatrix(8, 0, _lShift || _rShift);
        return any;
    }

    // Type a character via injection (used for auto-typing commands like "RUN\n").
    // This uses a simple press/release sequence; the emulator polls during steps.
    private readonly Queue<(int row, int col, bool shift)> _typeQueue = new();
    private int _typeTimer;
    private (int row, int col, bool shift)? _current;

    public void TypeString(string s)
    {
        foreach (char ch in s) TypeChar(ch);
    }

    public void TypeChar(char ch)
    {
        bool shift = false;
        (int row, int col)? rc = CharToKey(ch, out shift);
        if (rc.HasValue) _typeQueue.Enqueue((rc.Value.row, rc.Value.col, shift));
    }

    public void TickAutoType()
    {
        if (_current.HasValue)
        {
            _typeTimer--;
            if (_typeTimer <= 0)
            {
                var k = _current.Value;
                SetMatrix(k.row, k.col, false);
                if (k.shift) { _lShift = false; UpdateShiftFlag(); }
                _current = null;
                _typeTimer = 4;
            }
            return;
        }

        if (_typeTimer > 0) { _typeTimer--; return; }

        if (_typeQueue.Count > 0)
        {
            var k = _typeQueue.Dequeue();
            if (k.shift) { _lShift = true; UpdateShiftFlag(); }
            SetMatrix(k.row, k.col, true);
            _current = k;
            _typeTimer = 4;
        }
    }

    private static (int row, int col)? CharToKey(char ch, out bool shift)
    {
        shift = false;
        ch = char.ToUpperInvariant(ch);
        if (ch >= 'A' && ch <= 'Z')
        {
            var map = new Dictionary<char, (int,int)>
            {
                {'A',(4,7)}, {'B',(4,6)}, {'C',(4,5)}, {'D',(4,4)}, {'E',(4,3)},
                {'F',(4,2)}, {'G',(4,1)}, {'H',(4,0)}, {'I',(3,7)}, {'J',(3,6)},
                {'K',(3,5)}, {'L',(3,4)}, {'M',(3,3)}, {'N',(3,2)}, {'O',(3,1)},
                {'P',(3,0)}, {'Q',(2,7)}, {'R',(2,6)}, {'S',(2,5)}, {'T',(2,4)},
                {'U',(2,3)}, {'V',(2,2)}, {'W',(2,1)}, {'X',(2,0)}, {'Y',(1,7)}, {'Z',(1,6)}
            };
            if (map.TryGetValue(ch, out var rc)) return rc;
        }
        if (ch >= '0' && ch <= '9')
        {
            return ch switch
            {
                '0' => (6,3), '1' => (5,7), '2' => (5,6), '3' => (5,5),
                '4' => (5,4), '5' => (5,3), '6' => (5,2), '7' => (5,1),
                '8' => (5,0), '9' => (6,2),
                _ => ((int row, int col)?)null
            };
        }
        switch (ch)
        {
            case ' ': return (6, 4);
            case '\r': case '\n': return (0, 0);
            case '.': return (6, 0);
            case ',': return (0, 2);
            case '-': return (6, 5);
            case '/': return (7, 0);
            case '?': return (7, 1);
        }
        return null;
    }
}
