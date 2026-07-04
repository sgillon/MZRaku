using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A keyboard matrix. Same 10-row × 8-column active-low
/// shape as MZ-700, but different key placements per Fig 3.6 in the
/// Owner's Manual (printed p.167). This class is deliberately much
/// leaner than the MZ-700's <see cref="Keyboard"/> — Phase 3 aims
/// for physical PC-key → MZ-80A matrix scans, not the rich
/// CharMap/SpecialKeyMap/auto-typer stack. Adding those on top is
/// straightforward once the machine boots to a prompt and there's
/// something to type into.
/// </summary>
public sealed class Mz80aKeyboard : IKeyboardMatrix
{
    private readonly byte[] _rows = new byte[10];

    // Live PC-key → (row, col) holds. Lets OnKeyUp release exactly
    // the matrix bits its OnKeyDown asserted.
    private readonly Dictionary<Keys, (int row, int col)> _holds = new();

    public Mz80aKeyboard()
    {
        for (int i = 0; i < 10; i++) _rows[i] = 0xFF;
    }

    public byte ReadRow(int strobe)
    {
        if (strobe < 0 || strobe > 9) return 0xFF;
        return _rows[strobe];
    }

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
    }

    /// <summary>
    /// Called from MainForm.OnKeyDown. Returns true if the key was
    /// consumed (should not be passed on to WinForms).
    /// </summary>
    public bool OnKeyDown(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        // Map the modifier flags into their own matrix bits. Shift and
        // Ctrl aren't ordinary characters but they matter for shifted
        // glyphs / BREAK detection, so we assert them alongside the
        // "real" key.
        UpdateModifiers(keyData);
        var pos = MapKey(key);
        if (pos == null) return false;
        SetMatrix(pos.Value.row, pos.Value.col, true);
        _holds[key] = pos.Value;
        return true;
    }

    public bool OnKeyUp(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        UpdateModifiers(keyData);
        if (_holds.TryGetValue(key, out var pos))
        {
            SetMatrix(pos.row, pos.col, false);
            _holds.Remove(key);
            return true;
        }
        return false;
    }

    private void UpdateModifiers(Keys keyData)
    {
        // SHIFT is strobe 0 D0 on MZ-80A (Fig 3.6).
        bool pcShift = (keyData & Keys.Shift) != 0;
        SetMatrix(0, 0, pcShift);
        // CTRL is strobe 0 D7 on MZ-80A — same key as BREAK, which is
        // shifted-CTRL. Phase 3 wires plain Ctrl only; BREAK detection
        // (checking shift+ctrl combo) can layer on top.
        bool pcCtrl = (keyData & Keys.Control) != 0;
        SetMatrix(0, 7, pcCtrl);
    }

    /// <summary>
    /// Static PC virtual-key → MZ-80A (row, col) map derived from Fig
    /// 3.6. Covers letters, digits, main-block control keys. Numeric
    /// pad, cursor keys, GRPH etc. can be added as needs surface.
    /// </summary>
    private static (int row, int col)? MapKey(Keys k) => k switch
    {
        // Row 0 (D0) — SHIFT, Z, C, B, SPACE, M, >., ↑?, 0, .
        Keys.Z => (1, 0),
        Keys.C => (2, 0),
        Keys.B => (3, 0),
        Keys.Space => (4, 0),
        Keys.M => (5, 0),
        Keys.OemPeriod => (6, 0),

        // Row 1 (D1) — GRPH, X, V, N, <,, -/, ←, unused, unused, 00
        Keys.X => (1, 1),
        Keys.V => (2, 1),
        Keys.N => (3, 1),
        Keys.Oemcomma => (4, 1),
        Keys.OemMinus => (5, 1),

        // Row 2 (D2) — INST/DEL, S, F, H, K, +;, ], unused, 1(pad), 3(pad)
        Keys.Delete => (0, 2),
        Keys.Back => (0, 2),
        Keys.S => (1, 2),
        Keys.F => (2, 2),
        Keys.H => (3, 2),
        Keys.K => (4, 2),
        Keys.OemSemicolon => (5, 2),
        Keys.OemCloseBrackets => (6, 2),

        // Row 3 (D3) — unused, A, D, G, J, L, *:, CR/ENT, 2(pad), unused
        Keys.A => (1, 3),
        Keys.D => (2, 3),
        Keys.G => (3, 3),
        Keys.J => (4, 3),
        Keys.L => (5, 3),
        Keys.Oemplus => (6, 3), // ':' shifted lives on same key as '*'
        Keys.Enter => (7, 3), // Keys.Return is an alias of Keys.Enter

        // Row 4 (D4) — unused, Q, E, T, U, O, \@, CURSOR↑, 4(pad), 6(pad)
        Keys.Q => (1, 4),
        Keys.E => (2, 4),
        Keys.T => (3, 4),
        Keys.U => (4, 4),
        Keys.O => (5, 4),
        Keys.Oem3 => (6, 4), // UK-layout '@' on Shift+' — approximation
        Keys.Up => (7, 4),

        // Row 5 (D5) — unused, W, R, Y, I, P, [, CURSOR→, 5(pad), -(pad)
        Keys.W => (1, 5),
        Keys.R => (2, 5),
        Keys.Y => (3, 5),
        Keys.I => (4, 5),
        Keys.P => (5, 5),
        Keys.OemOpenBrackets => (6, 5),
        Keys.Right => (7, 5),

        // Row 6 (D6) — unused, !1, #3, %5, /7, )9, =, |\, 7(pad), 9(pad)
        Keys.D1 => (1, 6),
        Keys.D3 => (2, 6),
        Keys.D5 => (3, 6),
        Keys.D7 => (4, 6),
        Keys.D9 => (5, 6),
        Keys.OemQuestion => (6, 6), // '=' — best-effort layout guess
        Keys.OemPipe => (7, 6),

        // Row 7 (D7) — BREAK/CTRL, 1", $4, &6, (8, )0, ~^, CLR/HOME
        // CTRL modifier handled in UpdateModifiers; ESC drops into
        // BREAK (Shift+CTRL) here for convenience.
        Keys.Escape => (0, 7),
        Keys.D2 => (8, 3),  // '2' on num-pad column (unshifted)
        Keys.D4 => (8, 4),  // '4'
        Keys.D6 => (9, 4),  // '6'
        Keys.D8 => (8, 7),  // '8'
        Keys.D0 => (8, 0),  // '0'
        Keys.Home => (7, 7),

        _ => null,
    };
}
