using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Reusable user-control for capturing a single PC keystroke for the
/// keyboard-map editor. Hooks KeyDown + KeyPress and suppresses the
/// usual form-navigation behaviour for Tab / Esc / Enter / arrows so the
/// user can bind any of them. Raises <see cref="Captured"/> with the
/// resolved (Keys, char?) pair; non-printable keys come through with
/// char = null.
///
/// Modifier handling:
///   * Tap-and-release a bare modifier (Ctrl / Shift / Alt) with no
///     intervening key press → captured as that modifier alone.
///   * Hold a modifier and press another key → captured as the
///     combination on the second key's press.
///
/// Surfaces decision #8 of the keyboard-editor plan: WinForms exposes
/// LCtrl/RCtrl/LShift/RShift/LAlt/RAlt as distinct VKs here, but the
/// downstream binding layer treats them generically. We normalise to
/// the generic VK on emit and flag <see cref="KeyCapturedEventArgs.Ambiguous"/>
/// with a short note so the user knows the binding fires for both sides.
/// </summary>
public sealed class KeyCaptureControl : UserControl
{
    public event EventHandler<KeyCapturedEventArgs>? Captured;

    private readonly Label _label;
    private readonly Label _hint;
    private Keys _pendingDownVk = Keys.None;
    private Keys _modifierTapBare = Keys.None;
    private bool _interveningPress;
    private bool _capturing = true;

    private static readonly Dictionary<Keys, string> FriendlyNames = new()
    {
        // WinForms exposes PageUp / PageDown as Keys.Prior / Keys.Next
        // when ToString'd — friendlier labels here.
        [Keys.PageUp]      = "Page Up",
        [Keys.PageDown]    = "Page Down",
        [Keys.LControlKey] = "Left Ctrl",
        [Keys.RControlKey] = "Right Ctrl",
        [Keys.LShiftKey]   = "Left Shift",
        [Keys.RShiftKey]   = "Right Shift",
        [Keys.LMenu]       = "Left Alt",
        [Keys.RMenu]       = "Right Alt",
        [Keys.ControlKey]  = "Ctrl",
        [Keys.ShiftKey]    = "Shift",
        [Keys.Menu]        = "Alt",
        [Keys.Space]       = "Space",
        [Keys.OemPeriod]   = ".",
        [Keys.Oemcomma]    = ",",
        [Keys.OemMinus]    = "-",
        [Keys.Oemplus]     = "+",
        [Keys.OemQuestion] = "/",
        [Keys.OemSemicolon] = ";",
        [Keys.OemQuotes]   = "'",
        [Keys.OemPipe]     = "\\",
        [Keys.Oemtilde]    = "`",
        [Keys.OemOpenBrackets]  = "[",
        [Keys.OemCloseBrackets] = "]",
    };

    public KeyCaptureControl()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
        Padding = new Padding(8);
        MinimumSize = new Size(340, 110);

        // TableLayoutPanel with percentage rows — a Dock=Top label with
        // fixed Height clips wrapped capture text on a second line.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _label = new Label
        {
            Text = "Click here, then press a key…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
        };
        _hint = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DarkSlateGray,
        };
        layout.Controls.Add(_label, 0, 0);
        layout.Controls.Add(_hint, 0, 1);
        Controls.Add(layout);

        // The inner controls intercept Click — wire all three so clicking
        // anywhere inside the box gives the control focus.
        EventHandler focusSelf = (_, _) => Focus();
        Click += focusSelf;
        layout.Click += focusSelf;
        _label.Click += focusSelf;
        _hint.Click += focusSelf;

        GotFocus += (_, _) => { BackColor = Color.LightYellow; Invalidate(); };
        LostFocus += (_, _) => { BackColor = SystemColors.Window; Invalidate(); };
    }

    /// <summary>
    /// When true (default) the control intercepts keystrokes and raises
    /// <see cref="Captured"/>. Set false to disable capture without
    /// destroying the control.
    /// </summary>
    public bool Capturing
    {
        get => _capturing;
        set
        {
            _capturing = value;
            if (_capturing && CanFocus) Focus();
        }
    }

    /// <summary>Clear any displayed capture and return to the prompt state.</summary>
    public void ResetCapture()
    {
        _label.Text = "Click here, then press a key…";
        _hint.Text = "";
        _hint.ForeColor = Color.DarkSlateGray;
        _pendingDownVk = Keys.None;
        _modifierTapBare = Keys.None;
        _interveningPress = false;
    }

    /// <summary>
    /// Direct entry point for Alt-modified keystrokes that arrive via
    /// the WM_SYSKEYDOWN path. Called by our own <see cref="WndProc"/>
    /// intercept; can also be called by a hosting form's
    /// <c>ProcessCmdKey</c> if the host prefers to gate it there.
    /// </summary>
    public void InjectSystemKey(Keys keyData)
    {
        if (!_capturing) return;
        var bare = keyData & Keys.KeyCode;

        if (IsModifierOnly(bare))
        {
            HandleModifierDown(bare);
            return;
        }

        // Windows produces WM_SYSCHAR (not WM_CHAR) for Alt+letter, which
        // WinForms doesn't surface as KeyPress — emit non-printable.
        _interveningPress = true;
        Emit(keyData, null);
    }

    // Intercept the Alt-key message family directly on the control.
    // Two reasons this lives here rather than on the host form:
    //   1. WM_SYSCHAR is dispatched to the focused control's WndProc,
    //      not the form's, so only the control can suppress the
    //      DefWindowProc "no matching menu accelerator" beep.
    //   2. The control becomes self-contained — any form can host it
    //      without needing to remember to forward Alt-keys.
    protected override void WndProc(ref Message m)
    {
        const int WM_SYSKEYDOWN = 0x104;
        const int WM_SYSCHAR    = 0x106;
        if (_capturing)
        {
            if (m.Msg == WM_SYSCHAR)
                return; // swallow the menu-mnemonic-not-found beep
            if (m.Msg == WM_SYSKEYDOWN)
            {
                var vk = (Keys)m.WParam.ToInt32();
                var keyData = vk | ModifierKeys;
                // Leave Alt+F4 alone so the dialog can still be closed.
                if (!(vk == Keys.F4 && (keyData & Keys.Alt) != 0))
                {
                    InjectSystemKey(keyData);
                    return;
                }
            }
        }
        base.WndProc(ref m);
    }

    // Tell WinForms not to let the surrounding form's dialog-navigation
    // logic (Tab cycle, Esc cancel, Enter accept, arrow-key navigation)
    // eat our keystrokes — we want every press to land in OnKeyDown.
    protected override bool IsInputKey(Keys keyData) =>
        _capturing || base.IsInputKey(keyData);

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        if (_capturing) e.IsInputKey = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        e.Handled = true;

        var bare = e.KeyCode;

        if (IsModifierOnly(bare))
        {
            HandleModifierDown(bare);
            return;
        }

        // Non-modifier press while a modifier might be held: the tap-to-
        // bind state for that modifier is no longer eligible.
        _interveningPress = true;

        // Printable keys: KeyPress fires next with the resolved Unicode
        // char (post-layout, post-AltGr). Stash and wait.
        // Non-printables (cursors, F-keys, Insert/Delete, Esc, Tab,
        // Enter) don't fire KeyPress — emit immediately.
        if (CouldBePrintable(bare))
        {
            // Ctrl+Alt (AltGr on UK/EU layouts, or deliberate hotkey
            // combo) — bind as a hotkey, not a char. Most layouts
            // produce no WM_CHAR for AltGr+letter so the KeyPress wait
            // would hang; on layouts that do produce one (UK AltGr+I,
            // etc.) we still prefer the VK+modifier for a binding.
            if ((e.KeyData & Keys.Control) != 0 && (e.KeyData & Keys.Alt) != 0)
                Emit(e.KeyData, null);
            else
                _pendingDownVk = e.KeyData;
        }
        else
            Emit(e.KeyData, null);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!_capturing) { base.OnKeyPress(e); return; }
        e.Handled = true;
        if (_pendingDownVk == Keys.None) return;
        var vk = _pendingDownVk;
        _pendingDownVk = Keys.None;
        Emit(vk, e.KeyChar);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyUp(e); return; }
        e.Handled = true;
        var bare = e.KeyCode;

        // Tap-to-bind: bare modifier pressed and released with no
        // intervening key press → capture the modifier alone.
        if (bare == _modifierTapBare && !_interveningPress)
            Emit(bare, null);

        if (IsModifierOnly(bare))
        {
            _modifierTapBare = Keys.None;
            _interveningPress = false;
        }

        base.OnKeyUp(e);
    }

    private void ShowModifierHeld(Keys bare)
    {
        _label.Text = $"{DescribeModifier(bare)} held — release to bind alone, or press another key";
        _hint.Text = "";
        _hint.ForeColor = Color.DarkSlateGray;
        _pendingDownVk = Keys.None;
    }

    private void HandleModifierDown(Keys bare)
    {
        if (_modifierTapBare == Keys.None)
        {
            _modifierTapBare = bare;
            _interveningPress = false;
            ShowModifierHeld(bare);
        }
        else if (_modifierTapBare != bare)
        {
            // A second, different modifier was pressed while the first
            // is still held — disqualifies a clean tap-to-bind on the
            // first. Covers AltGr (Windows synthesises LCtrl-down ahead
            // of RAlt-down on UK/EU layouts; without this guard, the
            // LCtrl tap-bind fires on the trailing LCtrl-up) and any
            // user-held modifier-stack like Ctrl+Shift+key.
            _interveningPress = true;
            _label.Text = "Multiple modifiers held — press another key to capture the combination";
            _hint.Text = "";
            _hint.ForeColor = Color.DarkSlateGray;
        }
    }

    private void Emit(Keys keyData, char? ch)
    {
        var bare = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;

        // Decision #8: bind L/R modifier variants generically. Normalise
        // the bare VK so downstream callers see one canonical form.
        Keys normalizedBare = bare switch
        {
            Keys.LControlKey or Keys.RControlKey => Keys.ControlKey,
            Keys.LShiftKey   or Keys.RShiftKey   => Keys.ShiftKey,
            Keys.LMenu       or Keys.RMenu       => Keys.Menu,
            _ => bare,
        };
        var normalized = normalizedBare | mods;

        DescribeAmbiguity(bare, keyData, out var ambiguous, out var note);

        _label.Text = ch.HasValue
            ? $"Captured: {DescribeKey(normalized)}  →  '{ch.Value}'  (U+{(int)ch.Value:X4})"
            : $"Captured: {DescribeKey(normalized)}";
        _hint.Text = note ?? "";
        _hint.ForeColor = ambiguous ? Color.DarkOrange : Color.DarkSlateGray;

        Captured?.Invoke(this, new KeyCapturedEventArgs(normalized, ch, ambiguous, note));
    }

    private static bool IsModifierOnly(Keys k) =>
        k is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
          or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
          or Keys.Menu or Keys.LMenu or Keys.RMenu;

    // KeyPress fires for keys that produce a character on the active
    // layout — letters, digits, punctuation, space, Backspace. It does
    // NOT fire for cursors, function keys, Insert, Delete, Esc, Tab,
    // or the Windows/menu keys. List the known non-printables so anything
    // else falls through to the KeyPress path.
    private static bool CouldBePrintable(Keys k) =>
        !(k is Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
            or Keys.Insert or Keys.Delete
            or Keys.F1 or Keys.F2 or Keys.F3 or Keys.F4 or Keys.F5 or Keys.F6
            or Keys.F7 or Keys.F8 or Keys.F9 or Keys.F10 or Keys.F11 or Keys.F12
            or Keys.Escape or Keys.Tab or Keys.CapsLock
            or Keys.NumLock or Keys.Scroll or Keys.Pause
            or Keys.LWin or Keys.RWin or Keys.Apps);

    private static void DescribeAmbiguity(Keys bareReported, Keys keyData, out bool ambiguous, out string? note)
    {
        ambiguous = false;
        note = null;

        // Bare-modifier capture: warn that L and R share the binding.
        if (bareReported is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
        { ambiguous = true; note = "Note: Left/Right Ctrl share this binding — both will fire."; return; }
        if (bareReported is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)
        { ambiguous = true; note = "Note: Left/Right Shift share this binding — both will fire."; return; }
        if (bareReported is Keys.Menu or Keys.LMenu or Keys.RMenu)
        { ambiguous = true; note = "Note: Left/Right Alt share this binding — both will fire."; return; }

        // Modifier flag set (e.g. Ctrl+G) — the modifier portion is
        // ambiguous even though the base key isn't.
        var mods = keyData & Keys.Modifiers;
        var sides = new List<string>();
        if ((mods & Keys.Control) != 0) sides.Add("Ctrl");
        if ((mods & Keys.Shift)   != 0) sides.Add("Shift");
        if ((mods & Keys.Alt)     != 0) sides.Add("Alt");
        if (sides.Count > 0)
        {
            ambiguous = true;
            note = $"Note: {string.Join(" / ", sides)} modifier bound generically — left and right both trigger.";
        }
    }

    private static string DescribeKey(Keys keyData)
    {
        var bare = keyData & Keys.KeyCode;
        string mods = "";
        if ((keyData & Keys.Control) != 0) mods += "Ctrl+";
        if ((keyData & Keys.Alt)     != 0) mods += "Alt+";
        if ((keyData & Keys.Shift)   != 0) mods += "Shift+";

        if (FriendlyNames.TryGetValue(bare, out var friendly)) return mods + friendly;
        if (SpecialKeyMap.Labels.TryGetValue(bare, out var lbl)) return mods + lbl;
        return mods + bare.ToString();
    }

    private static string DescribeModifier(Keys bare) => bare switch
    {
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Ctrl",
        Keys.ShiftKey   or Keys.LShiftKey   or Keys.RShiftKey   => "Shift",
        Keys.Menu       or Keys.LMenu       or Keys.RMenu       => "Alt",
        _ => "Modifier",
    };
}

public sealed class KeyCapturedEventArgs : EventArgs
{
    public Keys KeyData { get; }
    public char? Char { get; }
    public bool Ambiguous { get; }
    public string? AmbiguityNote { get; }

    public KeyCapturedEventArgs(Keys keyData, char? ch, bool ambiguous, string? note)
    {
        KeyData = keyData;
        Char = ch;
        Ambiguous = ambiguous;
        AmbiguityNote = note;
    }
}
