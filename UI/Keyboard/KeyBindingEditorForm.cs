using System;
using System.Drawing;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Modal editor that binds a PC keystroke to a single MZ-700 matrix slot.
/// Phase A: writes only into the <see cref="CharMapOverrides"/> layer —
/// captures that resolve to a Unicode char are saved; non-character VKs
/// (modifiers, function keys, cursors, Enter, Esc, Tab) are politely
/// refused with a "Phase B coming" note.
///
/// The target slot is fixed at construction (cell-clicked in the matrix
/// grid). The MzShift checkbox under the Advanced expander lets the user
/// flip the shift assertion away from the default the cell-click implied.
///
/// Conflict detection (P2-5): if the captured PC char already produces a
/// different MZ slot — either via an existing override or a built-in
/// default — the status line flips to a warning, and Save prompts for
/// confirmation before clobbering the prior binding.
///
/// Mutations are live: <see cref="CharMapOverrides.Set"/> is called from
/// Save and immediately affects subsequent keystrokes. Persistence to
/// <c>settings.ini</c> still waits for the parent <see cref="SettingsForm"/>'s
/// Apply / OK — consistent with the rest of the dialog.
/// </summary>
public sealed class KeyBindingEditorForm : Form
{
    public int Row { get; }
    public int Col { get; }
    public bool MzShift => _shiftCheck.Checked;

    private readonly CharMapOverrides _overrides;
    private readonly KeyCaptureControl _capture;
    private readonly Label _status;
    private readonly Label _phaseBNote;
    private readonly Button _advancedBtn;
    private readonly Panel _advancedPanel;
    private readonly CheckBox _shiftCheck;
    private readonly Button _saveBtn;

    private char? _capturedChar;

    // When non-null, the captured char already maps to a different slot
    // (or different shift state). Save will prompt before replacing it.
    // Recomputed on every capture and whenever the MzShift checkbox flips.
    private ConflictInfo? _conflict;

    private readonly record struct ConflictInfo(
        CharMap.Press Existing,
        bool FromOverride);

    public KeyBindingEditorForm(int row, int col, bool defaultMzShift, CharMapOverrides overrides)
    {
        Row = row;
        Col = col;
        _overrides = overrides;

        Text = "Bind PC key to MZ slot";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 360);
        KeyPreview = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // header / capture / status / phaseB / advanced btn / advanced panel / buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Header — describes the target slot.
        var header = new Label
        {
            Text = BuildHeaderText(row, col, defaultMzShift),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(header, 0, 0);

        // Capture control.
        _capture = new KeyCaptureControl
        {
            Dock = DockStyle.Top,
            Height = 110,
            Margin = new Padding(0, 0, 0, 8),
        };
        _capture.Captured += OnCaptured;
        root.Controls.Add(_capture, 0, 1);

        // Status line — shows what's about to be bound on a successful capture.
        _status = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(_status, 0, 2);

        // Phase-B note — visible only when a non-char VK is captured.
        _phaseBNote = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.DarkOrange,
            Visible = false,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(_phaseBNote, 0, 3);

        // Advanced expander button.
        _advancedBtn = new Button
        {
            Text = "Advanced ▾",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 0, 0),
        };
        _advancedBtn.FlatAppearance.BorderSize = 0;
        _advancedBtn.Click += (_, _) => ToggleAdvanced();
        root.Controls.Add(_advancedBtn, 0, 4);

        // Advanced panel — hidden by default. MzShift checkbox lives here.
        _shiftCheck = new CheckBox
        {
            Text = "Assert MZ shift while this key is held",
            Checked = defaultMzShift,
            AutoSize = true,
        };
        _advancedPanel = new Panel
        {
            AutoSize = true,
            Visible = false,
            Margin = new Padding(16, 4, 0, 0),
        };
        _advancedPanel.Controls.Add(_shiftCheck);
        _shiftCheck.CheckedChanged += (_, _) => EvaluateBinding();
        root.Controls.Add(_advancedPanel, 0, 5);

        // Buttons.
        _saveBtn = new Button { Text = "Save", Width = 80, Enabled = false };
        var cancelBtn = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
        _saveBtn.Click += (_, _) => OnSave();
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttonRow.Controls.Add(cancelBtn);
        buttonRow.Controls.Add(_saveBtn);
        root.Controls.Add(buttonRow, 0, 6);

        AcceptButton = _saveBtn;
        CancelButton = cancelBtn;

        Controls.Add(root);
        Shown += (_, _) => _capture.Focus();
    }

    private static string BuildHeaderText(int row, int col, bool mzShift)
    {
        var un = MzGlyphCatalog.FindByPrintableSlot(row, col, false);
        var sh = MzGlyphCatalog.FindByPrintableSlot(row, col, true);
        char? targetGlyph = mzShift ? sh : un;
        var special = MzGlyphCatalog.FindSpecialLabel(row, col);

        var shiftLabel = mzShift ? "shifted" : "unshifted";
        if (targetGlyph.HasValue)
            return $"Target: MZ slot ({row},{col}) producing '{targetGlyph.Value}' ({shiftLabel})";
        if (special != null)
            return $"Target: MZ slot ({row},{col}) — {special} ({shiftLabel})";
        return $"Target: MZ slot ({row},{col}) — no printable glyph ({shiftLabel})";
    }

    private void ToggleAdvanced()
    {
        _advancedPanel.Visible = !_advancedPanel.Visible;
        _advancedBtn.Text = _advancedPanel.Visible ? "Advanced ▴" : "Advanced ▾";
    }

    private void OnCaptured(object? sender, KeyCapturedEventArgs e)
    {
        _capturedChar = e.Char;

        if (!e.Char.HasValue)
        {
            // Non-character VK: defer to Phase B (P2-6).
            _status.Text = "";
            _phaseBNote.Text =
                "Non-character keys (modifiers, function keys, cursors, Enter, Esc, Tab) edit " +
                "the Key Overrides layer, which arrives in Phase B. For now, hand-edit the " +
                "[KeyOverrides] section in settings.ini.";
            _phaseBNote.Visible = true;
            _conflict = null;
            _saveBtn.Enabled = false;
            return;
        }

        EvaluateBinding();
    }

    /// <summary>
    /// Recomputes status text, conflict state, and Save enablement from
    /// the current <see cref="_capturedChar"/> and shift checkbox. Runs
    /// on every char capture and whenever the shift toggle flips — both
    /// can turn a no-op rebind into a conflict and vice-versa.
    /// </summary>
    private void EvaluateBinding()
    {
        if (_capturedChar is not char ch)
        {
            _conflict = null;
            _saveBtn.Enabled = false;
            return;
        }

        // A prior non-char capture may have shown the Phase-B note; a
        // subsequent char capture must clear it.
        _phaseBNote.Visible = false;

        bool targetShift = _shiftCheck.Checked;

        CharMap.Press? existing = null;
        bool fromOverride = false;
        if (_overrides.TryLookup(ch, out var cur))
        {
            existing = cur;
            fromOverride = true;
        }
        else if (CharMap.Defaults.TryGetValue(ch, out var def))
        {
            existing = def;
            fromOverride = false;
        }

        bool isConflict = existing.HasValue &&
            (existing.Value.Row != Row
             || existing.Value.Col != Col
             || existing.Value.MzShift != targetShift);

        _conflict = isConflict ? new ConflictInfo(existing!.Value, fromOverride) : null;

        string suffix;
        if (!existing.HasValue)
        {
            suffix = " — no existing binding.";
        }
        else if (isConflict)
        {
            string src = fromOverride ? "currently overridden to" : "default maps to";
            var ex = existing.Value;
            suffix = $" — ⚠ {src} ({ex.Row},{ex.Col}) {(ex.MzShift ? "shifted" : "unshifted")}.";
        }
        else
        {
            suffix = fromOverride
                ? " — already bound here (re-saving same override)."
                : " — matches the built-in default.";
        }

        _status.Text = $"Will bind '{ch}' (U+{(int)ch:X4}) → ({Row},{Col}){suffix}";
        _status.ForeColor = isConflict ? Color.DarkOrange : SystemColors.ControlText;
        _saveBtn.Enabled = true;
    }

    private void OnSave()
    {
        if (_capturedChar is not char ch) return;

        if (_conflict is ConflictInfo c)
        {
            var ex = c.Existing;
            string src = c.FromOverride ? "currently bound (override)" : "currently bound (default)";
            var result = MessageBox.Show(this,
                $"PC '{ch}' is {src} to MZ slot ({ex.Row},{ex.Col}) " +
                $"{(ex.MzShift ? "shifted" : "unshifted")}.\n\n" +
                $"Replace with ({Row},{Col}) {(_shiftCheck.Checked ? "shifted" : "unshifted")}?",
                "Conflict — replace existing binding?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
        }

        _overrides.Set(ch, new CharMap.Press(Row, Col, _shiftCheck.Checked));
        DialogResult = DialogResult.OK;
        Close();
    }
}
