using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-700 auto-typer: drives the keyboard matrix from a queue of
/// <see cref="CharMap.Press"/> entries so the CLI --basic autorun
/// can send "RUN\r", the Font Sheet can inject a clicked glyph,
/// and any host-side scripted-input flow can play through the same
/// matrix as a live human. Split out of <see cref="Keyboard"/> in
/// v1.2 audit F-016 — the live-key handling and the auto-typer are
/// separate subsystems that happen to share the same matrix.
///
/// Uses scan detection rather than fixed hold counts: after
/// asserting a press, wait for the ROM's scan-loop to observe the
/// affected row before advancing. Falls back to a
/// <see cref="ScanTimeoutFrames"/> timeout so a masked-interrupt
/// mid-routine can't wedge the typer forever. Shifted presses are
/// staged — SHIFT (8, 0) is asserted first and we wait for row 8
/// to be scanned before dropping the key bit; without this stage
/// the OS can capture the key-down before its first scan of row 8
/// with our bit set, and permanently mis-classify the press as
/// unshifted.
///
/// The MZ-80A auto-typer is a separate implementation on
/// <see cref="Mz80aKeyboard"/> — time-based rather than
/// scan-detection (SA-1510 doesn't scan the matrix from a
/// predictable rhythm), so no shared base yet.
/// </summary>
public sealed class KeyboardAutoTyper
{
    private readonly Keyboard _kb;
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

    // Safety net: if the OS isn't scanning the keyboard (e.g.
    // interrupts masked, mid-routine), don't wait forever. ~10 host
    // frames (~167ms) is well under the old 12-frame fixed hold but
    // generous enough to cover any realistic gap between scan bursts.
    private const int ScanTimeoutFrames = 10;

    // After Enter, BASIC tokenises and inserts the line; the
    // scan-loop pauses during that work. Hold this fixed cooldown
    // to give BASIC headroom before the next press lands. Empirical,
    // same as before.
    private const int EnterCooldownFrames = 30;

    public KeyboardAutoTyper(Keyboard kb) => _kb = kb;

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
    /// Queue a raw matrix-position press for the auto-typer. Used
    /// to drive the keyboard from sources that don't go through a
    /// Unicode char — e.g. the Font Sheet's click-to-input flow,
    /// which knows the MZ display code but not necessarily its
    /// host-keyboard glyph.
    /// </summary>
    public void TypePress(CharMap.Press p) => _typeQueue.Enqueue(p);

    public void Tick()
    {
        switch (_phase)
        {
            case AutoPhase.Idle:
            {
                if (_typeQueue.Count == 0) return;
                var p = _typeQueue.Dequeue();
                _current = p;
                // Set shift / $1170 to the press's required state in
                // both cases — false explicitly clears any stale
                // state left by a prior shifted press.
                _kb.SetMatrix(8, 0, p.MzShift);
                if (_kb.Memory != null) _kb.Memory.Ram[0x1170] = (byte)(p.MzShift ? 0x01 : 0x00);
                _kb.ClearScanObservation();
                if (p.MzShift)
                {
                    // Stage shift first; key follows once OS has scanned row 8.
                    _phase = AutoPhase.AwaitShiftScan;
                }
                else
                {
                    _kb.SetMatrix(p.Row, p.Col, true);
                    _phase = AutoPhase.AwaitKeyScan;
                }
                _phaseFramesLeft = ScanTimeoutFrames;
                break;
            }

            case AutoPhase.AwaitShiftScan:
            {
                bool observed = _kb.WasStrobeScanned(8);
                if (observed || --_phaseFramesLeft <= 0)
                {
                    var pa = _current!.Value;
                    _kb.SetMatrix(pa.Row, pa.Col, true);
                    _kb.ClearScanObservation();
                    _phase = AutoPhase.AwaitKeyScan;
                    _phaseFramesLeft = ScanTimeoutFrames;
                }
                break;
            }

            case AutoPhase.AwaitKeyScan:
            {
                var pa = _current!.Value;
                bool observed = _kb.WasStrobeScanned(pa.Row);
                if (observed || --_phaseFramesLeft <= 0)
                {
                    // Release both key and (any) shift together.
                    _kb.SetMatrix(pa.Row, pa.Col, false);
                    if (pa.MzShift)
                    {
                        _kb.SetMatrix(8, 0, false);
                        if (_kb.Memory != null) _kb.Memory.Ram[0x1170] = 0x00;
                    }
                    _kb.ClearScanObservation();
                    _phase = AutoPhase.AwaitRelease;
                    _phaseFramesLeft = ScanTimeoutFrames;
                }
                break;
            }

            case AutoPhase.AwaitRelease:
            {
                var pa = _current!.Value;
                bool observed = _kb.WasStrobeScanned(pa.Row);
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
