using System;
using System.Drawing;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Phase 2 sibling of <see cref="KeyBindingEditorForm"/> for fixed-label
/// keys (CR, GRAPH, ALPHA, CTRL, SHIFT, BREAK, INST, DEL, cursor arrows).
/// Captures a PC virtual key (optionally with modifier flags) and writes
/// it to the <see cref="KeyOverride"/> layer.
///
/// Differences from the char editor:
///   * Save target is <see cref="KeyOverride"/>, not <see cref="CharMapOverrides"/>.
///   * MzShift is tri-state — force on / force off / pass through the
///     user's actual PC shift state (the natural default).
///   * Conflict detection looks at <see cref="KeyOverride"/> and
///     <see cref="SpecialKeyMap.Map"/> — the two VK-side layers — and
///     flags a captured key that already produces a different slot.
///
/// Capture accepts any <see cref="Keys"/> value the user presses,
/// including bare characters: binding e.g. <c>Keys.Q</c> to GRAPH means
/// pressing Q will fire GRAPH instead of typing 'Q'. The user is
/// trusted to know what they're asking for; conflict messaging surfaces
/// the consequence at capture time.
///
/// Mutations are live (commit on Save). Persistence still rides on the
/// parent SettingsForm's Apply / OK.
/// </summary>
public sealed class VkBindingEditorForm : Form
{
    public int Row { get; }
    public int Col { get; }

    private readonly string _targetLabel;
    private readonly KeyOverride _overrides;

    private readonly KeyCaptureControl _capture;
    private readonly Label _status;
    private readonly Label _ambiguityNote;
    private readonly Button _advancedBtn;
    private readonly Panel _advancedPanel;
    private readonly RadioButton _shiftPassthrough;
    private readonly RadioButton _shiftForceOn;
    private readonly RadioButton _shiftForceOff;
    private readonly Button _saveBtn;

    private Keys? _capturedKey;
    private string? _ambiguityText;
    private ConflictInfo? _conflict;

    private readonly record struct ConflictInfo(
        int Row,
        int Col,
        bool? Shift,
        bool FromOverride);

    public VkBindingEditorForm(int row, int col, string targetLabel, KeyOverride overrides)
    {
        Row = row;
        Col = col;
        _targetLabel = targetLabel;
        _overrides = overrides;

        Text = $"Bind PC key to {targetLabel}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 380);
        KeyPreview = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // header / capture / status / ambiguity / advanced btn / advanced panel / buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Text = $"Target: MZ key {targetLabel} — matrix slot ({row},{col})",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(header, 0, 0);

        _capture = new KeyCaptureControl
        {
            Dock = DockStyle.Top,
            Height = 110,
            Margin = new Padding(0, 0, 0, 8),
        };
        _capture.Captured += OnCaptured;
        root.Controls.Add(_capture, 0, 1);

        _status = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(_status, 0, 2);

        _ambiguityNote = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.DarkOrange,
            Visible = false,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(_ambiguityNote, 0, 3);

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

        // Tri-state MZ-shift selector. Pass-through is the right default
        // for the special keys this form edits — none of them inherently
        // want to assert MZ shift, but the user may want F-keys to behave
        // as if shifted (e.g. binding a custom BREAK-style key).
        _shiftPassthrough = new RadioButton
        {
            Text = "Pass through PC shift state (default)",
            AutoSize = true,
            Checked = true,
        };
        _shiftForceOn = new RadioButton
        {
            Text = "Force MZ shift ON while this key is held",
            AutoSize = true,
        };
        _shiftForceOff = new RadioButton
        {
            Text = "Force MZ shift OFF while this key is held",
            AutoSize = true,
        };
        var shiftFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        shiftFlow.Controls.Add(_shiftPassthrough);
        shiftFlow.Controls.Add(_shiftForceOn);
        shiftFlow.Controls.Add(_shiftForceOff);
        _advancedPanel = new Panel
        {
            AutoSize = true,
            Visible = false,
            Margin = new Padding(16, 4, 0, 0),
        };
        _advancedPanel.Controls.Add(shiftFlow);
        _shiftPassthrough.CheckedChanged += (_, _) => EvaluateBinding();
        _shiftForceOn.CheckedChanged += (_, _) => EvaluateBinding();
        _shiftForceOff.CheckedChanged += (_, _) => EvaluateBinding();
        root.Controls.Add(_advancedPanel, 0, 5);

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

    private void ToggleAdvanced()
    {
        _advancedPanel.Visible = !_advancedPanel.Visible;
        _advancedBtn.Text = _advancedPanel.Visible ? "Advanced ▴" : "Advanced ▾";
    }

    private void OnCaptured(object? sender, KeyCapturedEventArgs e)
    {
        _capturedKey = e.KeyData;
        _ambiguityText = e.AmbiguityNote;
        EvaluateBinding();
    }

    /// <summary>
    /// Recomputes status text, conflict state, and Save enablement from
    /// the current captured Keys and the selected shift mode. Re-runs on
    /// every capture and whenever the shift radio group changes.
    /// </summary>
    private void EvaluateBinding()
    {
        if (_capturedKey is not Keys k)
        {
            _conflict = null;
            _saveBtn.Enabled = false;
            return;
        }

        bool? targetShift = SelectedShift();

        bool isConflict = false;
        string suffix;
        ConflictInfo? newConflict = null;

        if (_overrides.TryLookup(k, out var ex))
        {
            bool sameSlot = ex.Row == Row && ex.Col == Col && ex.MzShift == targetShift;
            if (sameSlot)
            {
                suffix = " — already bound here (re-saving same override).";
            }
            else
            {
                isConflict = true;
                suffix = $" — ⚠ currently overridden to ({ex.Row},{ex.Col}) {DescribeShift(ex.MzShift)}.";
                newConflict = new ConflictInfo(ex.Row, ex.Col, ex.MzShift, FromOverride: true);
            }
        }
        else if (SpecialKeyMap.Map.TryGetValue(k, out var def))
        {
            // SpecialKeyMap defaults carry no shift state — model them as
            // pass-through for comparison and display.
            bool sameSlot = def.row == Row && def.col == Col && targetShift == null;
            if (sameSlot)
            {
                suffix = " — matches the built-in default.";
            }
            else
            {
                isConflict = true;
                suffix = $" — ⚠ default maps to ({def.row},{def.col}) shift pass-through.";
                newConflict = new ConflictInfo(def.row, def.col, null, FromOverride: false);
            }
        }
        else
        {
            suffix = " — no existing binding.";
        }

        _conflict = newConflict;

        _status.Text = $"Will bind {DescribeKey(k)} → ({Row},{Col}) {_targetLabel}{suffix}";
        _status.ForeColor = isConflict ? Color.DarkOrange : SystemColors.ControlText;
        _ambiguityNote.Text = _ambiguityText ?? "";
        _ambiguityNote.Visible = !string.IsNullOrEmpty(_ambiguityText);
        _saveBtn.Enabled = true;
    }

    private void OnSave()
    {
        if (_capturedKey is not Keys k) return;

        if (_conflict is ConflictInfo c)
        {
            string src = c.FromOverride ? "currently bound (override)" : "currently bound (default)";
            var result = MessageBox.Show(this,
                $"PC {DescribeKey(k)} is {src} to MZ slot ({c.Row},{c.Col}) {DescribeShift(c.Shift)}.\n\n" +
                $"Replace with ({Row},{Col}) {_targetLabel} {DescribeShift(SelectedShift())}?",
                "Conflict — replace existing binding?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
        }

        _overrides.Set(k, new KeyOverride.Binding(Row, Col, SelectedShift()));
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool? SelectedShift()
    {
        if (_shiftForceOn.Checked) return true;
        if (_shiftForceOff.Checked) return false;
        return null;
    }

    private static string DescribeShift(bool? s) => s switch
    {
        true => "shifted (forced)",
        false => "unshifted (forced)",
        _ => "shift pass-through",
    };

    private static string DescribeKey(Keys keyData)
    {
        var bare = keyData & Keys.KeyCode;
        string mods = "";
        if ((keyData & Keys.Control) != 0) mods += "Ctrl+";
        if ((keyData & Keys.Alt) != 0) mods += "Alt+";
        if ((keyData & Keys.Shift) != 0) mods += "Shift+";

        if (SpecialKeyMap.Labels.TryGetValue(bare, out var lbl)) return mods + lbl;
        return mods + bare.ToString();
    }
}
