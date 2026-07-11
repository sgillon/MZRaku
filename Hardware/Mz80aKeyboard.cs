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

    // Live PC-key → (strobe, bit) holds. Lets OnKeyUp release exactly
    // the matrix bits its OnKeyDown asserted.
    private readonly Dictionary<Keys, (int strobe, int bit)> _holds = new();

    // Staged key releases — when PC KeyUp fires, we don't release the
    // matrix bit immediately. Instead we schedule it to release after
    // MinHoldFrames frames from the KeyDown. This ensures the ROM's
    // keyboard scan (which runs several times per host frame at 2 MHz)
    // sees the pressed state on at least one full scan pass, even
    // when SendKeys / rapid typing fires KeyDown+KeyUp within a single
    // 60Hz host frame. Same pattern MZ-700 uses (staged shift bits) but
    // applied uniformly since MZ-80A has no character-driven typing
    // path yet.
    private record struct StagedRelease(Keys Vk, int Strobe, int Bit, int FramesLeft);
    private readonly List<StagedRelease> _pendingReleases = new();
    private const int MinHoldFrames = 4;

    /// <summary>
    /// If true, inverts the SHIFT-bit assertion for letter keys only
    /// so PC users get PC-style casing (Shift+A → uppercase A, plain
    /// a → lowercase a). Set from Settings.Mz80aInvertLetterShift.
    /// Digits and punctuation ignore this flag — their shifted
    /// variants (! # $ etc.) still work via Shift regardless.
    /// </summary>
    public bool InvertLetterShift { get; set; } = true;

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
        UpdateModifiers(keyData, key);
        var pos = MapKey(key);
        if (pos == null) return false;
        SetMatrix(pos.Value.strobe, pos.Value.bit, true);
        _holds[key] = pos.Value;
        return true;
    }

    public bool OnKeyUp(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        UpdateModifiers(keyData, key);
        if (_holds.TryGetValue(key, out var pos))
        {
            // Stage the release rather than fire immediately — the
            // ROM's scan needs to observe the pressed state on at
            // least one full pass. TickFrame() completes the release
            // once the countdown expires.
            _pendingReleases.Add(new StagedRelease(key, pos.strobe, pos.bit, MinHoldFrames));
            _holds.Remove(key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Called once per host frame by MZ80A.RunFrame. Advances staged
    /// releases and drops matrix bits whose countdown has expired.
    /// </summary>
    public void TickFrame()
    {
        for (int i = _pendingReleases.Count - 1; i >= 0; i--)
        {
            var r = _pendingReleases[i];
            r.FramesLeft--;
            if (r.FramesLeft <= 0)
            {
                SetMatrix(r.Strobe, r.Bit, false);
                _pendingReleases.RemoveAt(i);
            }
            else
            {
                _pendingReleases[i] = r;
            }
        }
    }

    private void UpdateModifiers(Keys keyData, Keys currentKey)
    {
        // SHIFT is strobe 0 D0 on MZ-80A (Fig 3.6). MZ-80A's letter
        // convention is unshifted=uppercase / shifted=lowercase, which
        // is the opposite of PC. When InvertLetterShift is on AND the
        // key currently being pressed is a plain A-Z letter, we flip
        // the SHIFT assertion so PC muscle memory works. Digits and
        // punctuation aren't inverted — their shifted variants are
        // needed as-is for '!', '#', '$', etc.
        bool pcShift = (keyData & Keys.Shift) != 0;
        bool invert = InvertLetterShift && IsLetter(currentKey);
        SetMatrix(0, 0, pcShift ^ invert);
        // CTRL is strobe 0 D7 on MZ-80A — same key as BREAK, which is
        // shifted-CTRL. Phase 3 wires plain Ctrl only; BREAK detection
        // (checking shift+ctrl combo) can layer on top.
        bool pcCtrl = (keyData & Keys.Control) != 0;
        SetMatrix(0, 7, pcCtrl);
    }

    private static bool IsLetter(Keys k) => k >= Keys.A && k <= Keys.Z;

    /// <summary>
    /// Reverse index built once from <see cref="Mz80aMatrixReference"/>:
    /// PC virtual key → (strobe, bit). The reference is the single
    /// source of truth for slot placement; MapKey defers to it via
    /// this lookup so drift between the map and the reference is
    /// impossible.
    /// </summary>
    private static readonly Dictionary<Keys, (int strobe, int bit)> _pcKeyToSlot = BuildPcKeyMap();

    private static Dictionary<Keys, (int, int)> BuildPcKeyMap()
    {
        var m = new Dictionary<Keys, (int, int)>();
        foreach (var kv in Mz80aMatrixReference.All)
        {
            var slot = kv.Value;
            var vk = PcKeyForSlot(slot);
            if (vk != Keys.None) m[vk] = (slot.Strobe, slot.Bit);
        }
        return m;
    }

    /// <summary>
    /// PC virtual-key that "should" produce the character sitting at
    /// this slot. Match is by <see cref="Mz80aMatrixReference.Slot.Id"/>
    /// — the canonical slot label. Unknown / Unused slots return
    /// Keys.None so the PC key list stays minimal.
    /// </summary>
    private static Keys PcKeyForSlot(Mz80aMatrixReference.Slot s) => s.Id switch
    {
        // Letters — direct A..Z map.
        "A" => Keys.A, "B" => Keys.B, "C" => Keys.C, "D" => Keys.D,
        "E" => Keys.E, "F" => Keys.F, "G" => Keys.G, "H" => Keys.H,
        "I" => Keys.I, "J" => Keys.J, "K" => Keys.K, "L" => Keys.L,
        "M" => Keys.M, "N" => Keys.N, "O" => Keys.O, "P" => Keys.P,
        "Q" => Keys.Q, "R" => Keys.R, "S" => Keys.S, "T" => Keys.T,
        "U" => Keys.U, "V" => Keys.V, "W" => Keys.W, "X" => Keys.X,
        "Y" => Keys.Y, "Z" => Keys.Z,

        // Number row — D0..D9.
        "D0" => Keys.D0, "D1" => Keys.D1, "D2" => Keys.D2, "D3" => Keys.D3,
        "D4" => Keys.D4, "D5" => Keys.D5, "D6" => Keys.D6, "D7" => Keys.D7,
        "D8" => Keys.D8, "D9" => Keys.D9,

        // Punctuation / edit / control.
        "SPACE"        => Keys.Space,
        "CR"           => Keys.Enter,
        "COMMA"        => Keys.Oemcomma,
        "DOT"          => Keys.OemPeriod,
        "MINUS"        => Keys.OemMinus,         // PC '-' key
        "SLASH"        => Keys.OemQuestion,      // PC '/' key (US layout — UK may differ)
        "SEMI"         => Keys.OemSemicolon,
        "COLON"        => Keys.Oemplus,          // ':' shares key with '*'
        "LBRK"         => Keys.OemOpenBrackets,
        "RBRK"         => Keys.OemCloseBrackets,
        "AT"           => Keys.Oem3,             // UK layout '@' on Shift+'
        "PIPE"         => Keys.OemPipe,
        "CARET"        => Keys.Oem6,             // '^' on UK layout
        "INST_DEL"     => Keys.Delete,           // BACK also aliased below
        "BREAK_CTRL"   => Keys.Escape,
        "CLR_HOME"     => Keys.Home,
        "CURSOR_UP"    => Keys.Up,
        "CURSOR_RIGHT" => Keys.Right,
        "LEFT"         => Keys.Left,             // ← printable / cursor-left
        "UPARROW"      => Keys.PageUp,           // best-effort placeholder

        // Numeric-pad handled via NumPadN aliases would be cleaner —
        // for now they collide with the main-row digits so leave them
        // unmapped rather than double-binding.
        _ => Keys.None,
    };

    /// <summary>
    /// PC virtual-key → (strobe, bit) lookup. Backspace mirrors Delete
    /// to the INST/DEL slot since both are edit-erase on PC.
    /// </summary>
    private static (int strobe, int bit)? MapKey(Keys k)
    {
        if (k == Keys.Back && _pcKeyToSlot.TryGetValue(Keys.Delete, out var d))
            return d;
        return _pcKeyToSlot.TryGetValue(k, out var pos) ? pos : null;
    }
}
