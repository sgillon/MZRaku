using System.Collections.Generic;
using System.Windows.Forms;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A keyboard matrix. Same 10-strobe × 8-bit active-low
/// shape as MZ-700, populated from Fig 3.6 in the Owner's Manual
/// (printed p.167) via <see cref="Mz80aMatrixReference"/>.
///
/// Two-layer input mirroring the MZ-700 stack:
///   1. SpecialKeyMap (non-printables — arrows, INST/DEL, HOME,
///      BREAK/CTRL, GRPH, Enter) is consulted in
///      <see cref="OnKeyDown"/> and asserts directly.
///   2. Character keys defer from OnKeyDown to
///      <see cref="OnKeyPress"/>, which looks the resolved char up
///      in <see cref="Mz80aCharMap"/> and asserts strobe/bit +
///      explicit MZ-shift. This routes chars by the GLYPH the OS
///      produced, so UK PC layouts and PC muscle memory both work
///      without per-user VK mapping.
///
/// Shifted MZ chars are staged: SHIFT is applied on strobe 0
/// immediately, but the key bit itself is held back for a couple
/// of host frames so a ROM scan captures the shift observation
/// first — same race window as the MZ-700's pattern (both share
/// <see cref="KeyboardMatrixBase.LiveShiftStageFrames"/>).
///
/// v1.2 audit F-015 / F-021 hoisted the matrix backing, hold
/// bookkeeping, shift-race staging, and effective-shift rule into
/// <see cref="KeyboardMatrixBase"/>. This class keeps the MZ-80A-
/// specific bits: Ctrl-key routing to (0, 7), GraphMode toggle,
/// InvertLetterShift policy, and a time-based auto-typer
/// (SA-1510 doesn't scan the matrix from a predictable rhythm,
/// so the MZ-700's scan-detection auto-typer doesn't apply here).
/// </summary>
public sealed class Mz80aKeyboard : KeyboardMatrixBase
{
    /// <summary>
    /// When false (default) MZ-80A letters follow authentic
    /// behaviour: PC 'a' unshifted → MZ 'A' (uppercase), PC 'A'
    /// shifted → MZ 'a' (lowercase). When true, letters follow PC
    /// muscle memory: unshifted → lowercase, shifted → uppercase.
    /// Implemented by flipping the char-map's MzShift bit for
    /// letter chars in <see cref="OnKeyPress"/>. Digits and
    /// punctuation are unaffected — their char-map entries decide
    /// shift by glyph identity, not case.
    /// </summary>
    public bool InvertLetterShift { get; set; } = false;

    /// <summary>
    /// Tracks whether the MZ-80A is in GRPH mode (true) or ALPHA
    /// (false, default). Toggled on every F11 key-down, which is
    /// what SA-1510 / SA-5510 use as the GRPH key. Read by the
    /// status-bar mode indicator. Note: this is an F11-driven
    /// local approximation — programs that switch mode via other
    /// means (BASIC MODE command, direct video-mode writes) won't
    /// update this flag. Good enough for the user-facing F11
    /// workflow; improve later if a program-driven case surfaces.
    /// </summary>
    public bool GraphMode { get; private set; }

    /// <summary>
    /// User-editable physical-key overrides for MZ-80A, consulted
    /// before <see cref="Mz80aSpecialKeyMap"/> in
    /// <see cref="OnKeyDown"/>. Same <see cref="KeyOverride"/>
    /// shape as MZ-700 — the matrix is the same 10-strobe × 8-bit
    /// topology so nothing here is machine-specific. Null = no
    /// overrides layer wired.
    /// </summary>
    public KeyOverride? Overrides;

    protected override (int Row, int Col) ShiftSlot => (0, 0);

    protected override void OnShiftStateChanged(bool effective)
    {
        // MZ-80A has no equivalent of MZ-700's $1170 RAM shift
        // mirror — SA-1510's GETKY reads matrix(0, 0) directly.
        // Nothing to do here.
    }

    /// <summary>
    /// Extend the base's ReleaseAll with MZ-80A-specific state:
    /// reset the local GraphMode tracker so the status-bar
    /// indicator reflects a fresh machine post-reset.
    /// </summary>
    public override void ReleaseAll()
    {
        base.ReleaseAll();
        GraphMode = false;
    }

    /// <summary>
    /// Called from MainForm.OnKeyDown. Returns true if the key was
    /// consumed (should not be passed on to WinForms). Non-printables
    /// (SpecialKeyMap) return true and assert directly; character
    /// keys return false and wait for OnKeyPress to pair them with
    /// a char.
    /// </summary>
    public bool OnKeyDown(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        _pcShift = (keyData & Keys.Shift) != 0;
        SetMatrix(0, 7, (keyData & Keys.Control) != 0);

        Diag.LastKeyDown = keyData;

        if (_holds.ContainsKey(key)) { ApplyShiftState(); return true; }

        // SHIFT alone: don't touch strobe 0 yet — the next press's
        // char-map entry may want to override it. Deferring lets
        // EffectiveMzShift resolve at press-time. Mirrors the
        // MZ-700 shift-race fix.
        if (IsShiftKey(key)) return false;

        // Layer 1: user overrides. Resolve() checks the combined-
        // modifier form first (e.g. Control+G) then falls back to
        // the bare VK, so modifier-aware bindings work. MzShift can
        // be true / false / null and the ActiveHold honours each —
        // see EffectiveMzShift's null check.
        var ov = Overrides?.Resolve(keyData);
        if (ov.HasValue)
        {
            var b = ov.Value;
            _holds[key] = new ActiveHold(b.Row, b.Col, b.MzShift);
            ApplyShiftState();
            SetMatrix(b.Row, b.Col, true);
            Diag.Record(InputLayer.Override, b.Row, b.Col, b.MzShift);
            return true;
        }

        if (Mz80aSpecialKeyMap.Map.TryGetValue(key, out var sp))
        {
            _holds[key] = new ActiveHold(sp.Strobe, sp.Bit, sp.ExplicitMzShift);
            ApplyShiftState();
            if (sp.ExplicitMzShift == true)
            {
                // Same staged-key-bit pattern as CharMap shifted
                // presses — assert SHIFT now via ApplyShiftState
                // above, let the ROM scan pick it up, THEN drop
                // the key bit.
                _stagedKeyBits.Add(new StagedPress(key, sp.Strobe, sp.Bit, LiveShiftStageFrames));
            }
            else
            {
                SetMatrix(sp.Strobe, sp.Bit, true);
            }
            Diag.Record(InputLayer.SpecialKey, sp.Strobe, sp.Bit, sp.ExplicitMzShift);
            // F11 = GRPH toggle. Track local state so the status
            // bar can reflect ALPHA vs GRAPH.
            if (key == Keys.F11) GraphMode = !GraphMode;
            return true;
        }

        // Defer to OnKeyPress for char-driven mapping.
        _pendingDownVk = key;
        return false;
    }

    /// <summary>
    /// Paired with the preceding OnKeyDown. Looks up the resolved
    /// char in <see cref="Mz80aCharMap"/> and asserts the
    /// corresponding matrix slot with the explicit MZ-shift the
    /// char-map dictates. If the char isn't in the map, the press
    /// is silently dropped — preferable to mis-translating it.
    /// </summary>
    public void OnKeyPress(char ch)
    {
        Diag.LastKeyChar = ch;
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;

        if (!Mz80aCharMap.TryLookup(ch, out var p))
        {
            Diag.Record(InputLayer.None, -1, -1, null);
            return;
        }

        // InvertLetterShift = true swaps MZ-side case for letters
        // so PC-style Shift-for-uppercase works. Only letters flip;
        // digits and punctuation resolve by glyph identity (PC ','
        // and PC '<' are distinct chars → distinct char-map
        // entries), so their shift polarity is already correct.
        bool mzShift = p.MzShift;
        if (InvertLetterShift && char.IsLetter(ch))
            mzShift = !mzShift;

        _holds[vk] = new ActiveHold(p.Strobe, p.Bit, ExplicitMzShift: mzShift);
        ApplyShiftState();

        if (mzShift)
        {
            // Stage the key bit: SHIFT is on strobe 0 already, let
            // a ROM scan catch it, THEN drop the key bit. Without
            // this, a scan that reads the key bit and strobe 0 in
            // the same tick can cache pre-shift state and
            // mis-classify the press as unshifted.
            _stagedKeyBits.Add(new StagedPress(vk, p.Strobe, p.Bit, LiveShiftStageFrames));
        }
        else
        {
            SetMatrix(p.Strobe, p.Bit, true);
        }
        Diag.Record(InputLayer.Character, p.Strobe, p.Bit, mzShift);
    }

    public bool OnKeyUp(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        _pcShift = (keyData & Keys.Shift) != 0;
        if ((keyData & Keys.Control) == 0) SetMatrix(0, 7, false);
        Diag.LastKeyUp = keyData;
        if (_pendingDownVk == key) _pendingDownVk = Keys.None;

        bool handled = false;
        if (_holds.TryGetValue(key, out var h))
        {
            // Release the key bit IMMEDIATELY. Staging the release
            // used to be defensive against SendKeys collapsing
            // KeyDown+KeyUp into one host frame, but combined with
            // the ApplyShiftState fallback below it opened a race:
            // with shift still held, the hold's
            // ExplicitMzShift=false has been removed from the
            // dictionary but its slot bit is still asserted, so
            // EffectiveMzShift falls back to _pcShift=true and the
            // ROM catches an asserted slot bit while shift flips
            // on — reading the just-released letter as shifted
            // (lowercase). Same release pattern as MZ-700.
            SetMatrix(h.Row, h.Col, false);
            _holds.Remove(key);
            handled = true;
        }
        // If a staged press was still waiting, drop it — the OS
        // never saw the key bit, so there's nothing to release.
        _stagedKeyBits.RemoveAll(p => p.Vk == key);
        ApplyShiftState();
        return handled;
    }

    /// <summary>
    /// Called once per host frame by MZ80A.RunFrame. Progresses
    /// staged shifted-key-bit assertions (SHIFT-then-key ordering
    /// guarantee) and the auto-typer state machine.
    /// </summary>
    public void TickFrame()
    {
        TickStagedKeyBits();
        TickAutoType();
    }

    // ---- Auto-typer -----------------------------------------------------
    // Time-based (no scan-detection) — simpler than the MZ-700's
    // queue and adequate for LOAD/RUN sequencing during BASIC
    // cassette load. Each queued press is applied for HoldFrames,
    // released for ReleaseFrames, then the machine returns to Idle
    // (Enter gets a longer cooldown so BASIC has time to parse a
    // line).
    //
    // Shifted presses set SHIFT first, wait ShiftStageFrames for a
    // ROM scan to observe it, THEN drop the key bit — same race
    // window as live typing's staged-key-bit pattern.
    private readonly Queue<Mz80aCharMap.Press> _typeQueue = new();
    private enum AutoPhase { Idle, ShiftStage, Hold, Release, EnterCooldown }
    private AutoPhase _autoPhase;
    private int _autoPhaseFramesLeft;
    private Mz80aCharMap.Press? _autoCurrent;

    private const int AutoShiftStageFrames = 2;
    private const int AutoHoldFrames = 4;
    private const int AutoReleaseFrames = 3;
    private const int AutoEnterCooldownFrames = 20;

    /// <summary>
    /// Enqueue a string for auto-typing. Non-CharMap chars are
    /// silently skipped. '\r' / '\n' become the SA-5510 Enter key
    /// (SpecialKeyMap[Enter] → strobe 7, bit 3).
    /// </summary>
    public void TypeString(string s)
    {
        foreach (char ch in s) TypeChar(ch);
    }

    public void TypeChar(char ch)
    {
        if (ch == '\r' || ch == '\n')
        {
            var enter = Mz80aSpecialKeyMap.Map[Keys.Enter];
            _typeQueue.Enqueue(new Mz80aCharMap.Press(enter.Strobe, enter.Bit, false));
            return;
        }
        // Fold uppercase letters to their lowercase char-map entry
        // so the MZ prints them uppercase. Under the authentic
        // MZ-80A convention encoded in Mz80aCharMap, unshifted-key
        // = UPPERCASE glyph, so PC 'l' maps to (slot,
        // MzShift=false) → MZ prints 'L'. TypeString("LOAD\r")
        // therefore needs the letters to go through the lowercase
        // char-map entries. Callers pass strings in their natural
        // uppercase form (BASIC keywords) — this fold does the
        // translation once, centrally.
        if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
        if (Mz80aCharMap.TryLookup(ch, out var p))
            _typeQueue.Enqueue(p);
    }

    public bool AutoTypeIdle => _autoPhase == AutoPhase.Idle && _typeQueue.Count == 0;

    private void TickAutoType()
    {
        switch (_autoPhase)
        {
            case AutoPhase.Idle:
                if (_typeQueue.Count == 0) return;
                _autoCurrent = _typeQueue.Dequeue();
                var p = _autoCurrent.Value;
                if (p.MzShift)
                {
                    SetMatrix(0, 0, true);
                    _autoPhase = AutoPhase.ShiftStage;
                    _autoPhaseFramesLeft = AutoShiftStageFrames;
                }
                else
                {
                    SetMatrix(p.Strobe, p.Bit, true);
                    _autoPhase = AutoPhase.Hold;
                    _autoPhaseFramesLeft = AutoHoldFrames;
                }
                break;

            case AutoPhase.ShiftStage:
                if (--_autoPhaseFramesLeft <= 0)
                {
                    var ps = _autoCurrent!.Value;
                    SetMatrix(ps.Strobe, ps.Bit, true);
                    _autoPhase = AutoPhase.Hold;
                    _autoPhaseFramesLeft = AutoHoldFrames;
                }
                break;

            case AutoPhase.Hold:
                if (--_autoPhaseFramesLeft <= 0)
                {
                    var ph = _autoCurrent!.Value;
                    SetMatrix(ph.Strobe, ph.Bit, false);
                    if (ph.MzShift) SetMatrix(0, 0, false);
                    // Enter uses SpecialKeyMap coords (strobe 7,
                    // bit 3); any other press advances via the
                    // short release window. Enter needs BASIC's
                    // line-tokenise pause.
                    bool isEnter = ph.Strobe == 7 && ph.Bit == 3;
                    _autoPhase = isEnter ? AutoPhase.EnterCooldown : AutoPhase.Release;
                    _autoPhaseFramesLeft = isEnter ? AutoEnterCooldownFrames : AutoReleaseFrames;
                }
                break;

            case AutoPhase.Release:
            case AutoPhase.EnterCooldown:
                if (--_autoPhaseFramesLeft <= 0)
                {
                    _autoCurrent = null;
                    _autoPhase = AutoPhase.Idle;
                }
                break;
        }
    }
}
