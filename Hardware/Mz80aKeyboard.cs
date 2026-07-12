using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A keyboard matrix. Same 10-strobe × 8-bit active-low
/// shape as MZ-700, populated from Fig 3.6 in the Owner's Manual
/// (printed p.167) via <see cref="Mz80aMatrixReference"/>.
///
/// PHASE A ARCHITECTURE (2026-07-12): two-layer input mirroring the
/// MZ-700 stack.
///   1. SpecialKeyMap (non-printables — arrows, INST/DEL, HOME,
///      BREAK/CTRL, GRPH, Enter) is consulted in
///      <see cref="OnKeyDown"/> and asserts directly.
///   2. Character keys defer from OnKeyDown to
///      <see cref="OnKeyPress"/>, which looks the resolved char up in
///      <see cref="Mz80aCharMap"/> and asserts strobe/bit + explicit
///      MZ-shift. This routes chars by the GLYPH the OS produced, so
///      UK PC layouts and PC muscle memory both work without per-user
///      VK mapping.
///
/// Shifted MZ chars are staged: SHIFT is applied on strobe 0 immediately,
/// but the key bit itself is held back for a couple of host frames so a
/// ROM scan captures the shift observation first — same race window as
/// the MZ-700's LiveShiftStageFrames pattern.
/// </summary>
public sealed class Mz80aKeyboard : IKeyboardMatrix
{
    private readonly byte[] _rows = new byte[10];

    private record struct ActiveHold(int Strobe, int Bit, bool? ExplicitMzShift);
    private readonly Dictionary<Keys, ActiveHold> _holds = new();

    // PC VK whose KeyDown fired but hasn't yet been paired with its
    // KeyPress char. Cleared on OnKeyPress or on OnKeyUp of the same VK.
    private Keys _pendingDownVk;

    // Latest observed PC-shift state. When a hold has no explicit
    // MzShift requirement, EffectiveMzShift falls back to this so
    // SpecialKeyMap presses respect PC shift naturally.
    private bool _pcShift;

    private record struct StagedPress(Keys Vk, int Strobe, int Bit, int FramesLeft);
    private readonly List<StagedPress> _stagedKeyBits = new();
    private const int LiveShiftStageFrames = 2;

    /// <summary>
    /// Legacy setting retained for INI compatibility. With the char-map
    /// layer active, letter case is encoded per-char (PC 'a' and 'A'
    /// both land on the uppercase slot by default), so this flag no
    /// longer changes runtime behaviour. Users wanting PC-style casing
    /// can add per-char overrides. Kept as a public property so old
    /// settings.ini files load without complaint.
    /// </summary>
    public bool InvertLetterShift { get; set; } = false;

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
        _stagedKeyBits.Clear();
        _pendingDownVk = Keys.None;
    }

    /// <summary>
    /// Called from MainForm.OnKeyDown. Returns true if the key was
    /// consumed (should not be passed on to WinForms). Non-printables
    /// (SpecialKeyMap) return true and assert directly; character keys
    /// return false and wait for OnKeyPress to pair them with a char.
    /// </summary>
    public bool OnKeyDown(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        _pcShift = (keyData & Keys.Shift) != 0;
        SetMatrix(0, 7, (keyData & Keys.Control) != 0);

        if (_holds.ContainsKey(key)) { ApplyShiftState(); return true; }

        // SHIFT alone: don't touch strobe 0 yet — the next press's
        // char-map entry may want to override it. Deferring lets
        // EffectiveMzShift resolve at press-time. Mirrors the MZ-700
        // shift-race fix.
        if (IsShiftKey(key)) return false;

        if (Mz80aSpecialKeyMap.Map.TryGetValue(key, out var sp))
        {
            _holds[key] = new ActiveHold(sp.Strobe, sp.Bit, sp.ExplicitMzShift);
            ApplyShiftState();
            if (sp.ExplicitMzShift == true)
            {
                // Same staged-key-bit pattern as CharMap shifted presses
                // — assert SHIFT now via ApplyShiftState above, let the
                // ROM scan pick it up, THEN drop the key bit.
                _stagedKeyBits.Add(new StagedPress(key, sp.Strobe, sp.Bit, LiveShiftStageFrames));
            }
            else
            {
                SetMatrix(sp.Strobe, sp.Bit, true);
            }
            return true;
        }

        // Defer to OnKeyPress for char-driven mapping.
        _pendingDownVk = key;
        return false;
    }

    /// <summary>
    /// Paired with the preceding OnKeyDown. Looks up the resolved char
    /// in <see cref="Mz80aCharMap"/> and asserts the corresponding
    /// matrix slot with the explicit MZ-shift the char-map dictates.
    /// If the char isn't in the map, the press is silently dropped —
    /// preferable to mis-translating it.
    /// </summary>
    public void OnKeyPress(char ch)
    {
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;

        if (!Mz80aCharMap.TryLookup(ch, out var p)) return;

        _holds[vk] = new ActiveHold(p.Strobe, p.Bit, ExplicitMzShift: p.MzShift);
        ApplyShiftState();

        if (p.MzShift)
        {
            // Stage the key bit: SHIFT is on strobe 0 already, let a
            // ROM scan catch it, THEN drop the key bit. Without this,
            // a scan that reads the key bit and strobe 0 in the same
            // tick can cache pre-shift state and mis-classify the
            // press as unshifted.
            _stagedKeyBits.Add(new StagedPress(vk, p.Strobe, p.Bit, LiveShiftStageFrames));
        }
        else
        {
            SetMatrix(p.Strobe, p.Bit, true);
        }
    }

    public bool OnKeyUp(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        _pcShift = (keyData & Keys.Shift) != 0;
        if ((keyData & Keys.Control) == 0) SetMatrix(0, 7, false);
        if (_pendingDownVk == key) _pendingDownVk = Keys.None;

        bool handled = false;
        if (_holds.TryGetValue(key, out var h))
        {
            // Release the key bit IMMEDIATELY. Staging the release used
            // to be defensive against SendKeys collapsing KeyDown+KeyUp
            // into one host frame, but combined with the ApplyShiftState
            // fallback below it opened a race: with shift still held, the
            // hold's ExplicitMzShift=false has been removed from the
            // dictionary but its slot bit is still asserted, so
            // EffectiveMzShift falls back to _pcShift=true and the ROM
            // catches an asserted slot bit while shift flips on — reading
            // the just-released letter as shifted (lowercase). Same
            // release pattern as MZ-700.
            SetMatrix(h.Strobe, h.Bit, false);
            _holds.Remove(key);
            handled = true;
        }
        // If a staged press was still waiting, drop it — the OS never
        // saw the key bit, so there's nothing to release.
        _stagedKeyBits.RemoveAll(p => p.Vk == key);
        ApplyShiftState();
        return handled;
    }

    /// <summary>
    /// Called once per host frame by MZ80A.RunFrame. Progresses staged
    /// shifted-key-bit assertions (SHIFT-then-key ordering guarantee).
    /// </summary>
    public void TickFrame()
    {
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
                SetMatrix(s.Strobe, s.Bit, true);
            _stagedKeyBits.RemoveAt(i);
        }
    }

    private void ApplyShiftState() => SetMatrix(0, 0, EffectiveMzShift());

    private bool EffectiveMzShift()
    {
        // Any hold with an explicit MzShift wins. Multiple holds with
        // conflicting explicit shifts is a pathological case (chord)
        // — first-encountered wins, which is fine in practice.
        foreach (var h in _holds.Values)
            if (h.ExplicitMzShift.HasValue) return h.ExplicitMzShift.Value;
        return _pcShift;
    }

    private static bool IsShiftKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;
}
