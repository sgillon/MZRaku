using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Modal dialog for editing the PC-key → MZ-700 matrix mapping. Shows one
/// row per configurable MZ-700 position (from KeyMapping.Positions), with
/// the currently bound PC key for that position. User can capture a new
/// PC key by selecting a row and pressing "Capture key" then any key, or
/// clear a binding with "Clear mapping". OK applies + persists.
/// </summary>
public sealed class KeyMappingDialog : Form
{
    public KeyMapping Result { get; private set; }

    private readonly DataGridView _grid;
    private readonly Button _captureBtn;
    private readonly Button _clearBtn;
    private readonly Button _resetDefaultsBtn;
    private readonly Label _capturePrompt;
    private readonly ComboBox _shiftModeCombo;
    private bool _capturing;

    private const int MODE_AUTO = 0;          // pass through PC shift
    private const int MODE_FORCE_UNSHIFT = 1; // OverrideShift = true
    private const int MODE_FORCE_SHIFT = 2;   // ForceShifted = true

    public KeyMappingDialog(KeyMapping current)
    {
        // Deep copy of the input so Cancel can discard cleanly.
        Result = new KeyMapping();
        foreach (var e in current.Entries)
            Result.Entries.Add(new KeyMapping.Entry { PcKey = e.PcKey, Row = e.Row, Col = e.Col, Shift = e.Shift, OverrideShift = e.OverrideShift });

        Text = "Keyboard mapping";
        ClientSize = new Size(500, 600);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        KeyPreview = true;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
        };
        _grid.Columns.Add("Mz", "MZ-700 Key");
        _grid.Columns.Add("Pos", "Pos (row,col)");
        _grid.Columns.Add("Pc", "PC Key");
        _grid.Columns["Pos"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _grid.Columns["Pos"]!.Width = 90;
        _grid.CellDoubleClick += (_, _) => BeginCapture();
        PopulateGrid();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            ColumnCount = 5,
            RowCount = 2,
            Padding = new Padding(8),
        };
        for (int i = 0; i < 5; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        _capturePrompt = new Label
        {
            Text = "Double-click a row, or select then click \"Capture key\", then press any key.",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(0, 8, 0, 8),
        };

        _shiftModeCombo = new ComboBox
        {
            Dock = DockStyle.Top,
            Height = 24,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _shiftModeCombo.Items.Add("MZ shift mode: Auto (PC shift → MZ shift)");
        _shiftModeCombo.Items.Add("MZ shift mode: Force MZ unshifted (e.g. PC Shift+; → MZ ':')");
        _shiftModeCombo.Items.Add("MZ shift mode: Force MZ shifted (e.g. PC = → MZ '=')");
        _shiftModeCombo.SelectedIndex = MODE_AUTO;

        _captureBtn = new Button { Text = "Capture key", Dock = DockStyle.Fill };
        _captureBtn.Click += (_, _) => BeginCapture();
        _clearBtn = new Button { Text = "Clear mapping", Dock = DockStyle.Fill };
        _clearBtn.Click += (_, _) => ClearSelectedMapping();
        _resetDefaultsBtn = new Button { Text = "Reset to defaults", Dock = DockStyle.Fill };
        _resetDefaultsBtn.Click += (_, _) => ResetToDefaults();
        var ok = new Button { Text = "OK", Dock = DockStyle.Fill, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };

        bottom.Controls.Add(_captureBtn, 0, 0);
        bottom.Controls.Add(_clearBtn, 1, 0);
        bottom.Controls.Add(_resetDefaultsBtn, 2, 0);
        bottom.Controls.Add(ok, 3, 0);
        bottom.Controls.Add(cancel, 4, 0);

        Controls.Add(_grid);
        Controls.Add(_shiftModeCombo);
        Controls.Add(_capturePrompt);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // ProcessCmdKey runs BEFORE Form.KeyDown / DataGridView's built-in
    // navigation, so we intercept all keys here while in capture mode.
    // Without this, DataGridView consumes printable punctuation (',', '.',
    // '/' etc.) before the form's KeyDown event fires, making those keys
    // un-bindable.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);

        var k = keyData & Keys.KeyCode;

        // Modifier-only press: ignore and wait for the actual key. This
        // lets the user press Shift first, then their target key, and
        // we'll capture the combination correctly.
        if (IsModifierKey(k)) return true;

        bool shift = (keyData & Keys.Shift) != 0;
        HandleCapturedKey(k, shift);
        return true;
    }

    private static bool IsModifierKey(Keys k) =>
        k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey ||
        k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey ||
        k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu ||
        k == Keys.LWin || k == Keys.RWin;

    private void HandleCapturedKey(Keys k, bool shift)
    {
        if (k == Keys.Escape)
        {
            EndCapture("Capture cancelled.");
            return;
        }
        if (_grid.SelectedRows.Count == 0) { EndCapture(""); return; }
        var tag = (ValueTuple<int, int, bool>)_grid.SelectedRows[0].Tag!;
        int row = tag.Item1, col = tag.Item2;
        bool shiftedGlyph = tag.Item3;

        // Position-driven default: if user picked a SHIFTED-glyph entry,
        // auto-select Force-shifted regardless of dropdown setting (since
        // that's the whole point of those entries). Otherwise honour the
        // dropdown.
        bool forceShifted, overrideShift;
        if (shiftedGlyph)
        {
            forceShifted = true;
            overrideShift = false;
        }
        else
        {
            int mode = _shiftModeCombo.SelectedIndex;
            forceShifted = mode == MODE_FORCE_SHIFT;
            overrideShift = mode == MODE_FORCE_UNSHIFT;
        }

        // Remove any existing binding for the SAME (PcKey, shift) combo
        // so we don't leave stale duplicates. Other bindings at the same
        // (row,col) are preserved — multiple PC keys can map to one MZ
        // position (and shifted vs unshifted variants are distinct rows).
        Result.Entries.RemoveAll(en => en.PcKey == k.ToString() && en.Shift == shift);
        Result.Entries.Add(new KeyMapping.Entry
        {
            PcKey = k.ToString(),
            Shift = shift,
            OverrideShift = overrideShift,
            ForceShifted = forceShifted,
            Row = row,
            Col = col,
        });
        PopulateGrid();
        SelectRowFor(row, col, shiftedGlyph);
        string display = shift ? $"Shift + {k}" : k.ToString();
        string mode2 = forceShifted ? " (force MZ shifted)"
                     : overrideShift ? " (force MZ unshifted)"
                     : " (auto)";
        EndCapture($"Bound {display}{mode2} → {KeyMapping.LabelFor(row, col, shiftedGlyph)}.");
    }

    private void PopulateGrid()
    {
        _grid.Rows.Clear();
        foreach (var pos in KeyMapping.Positions)
        {
            // Each position represents either an unshifted glyph or a
            // shifted glyph at (row, col). Bindings with ForceShifted are
            // shown under the shifted-glyph row; everything else under
            // the unshifted-glyph row at the same matrix position.
            var matches = new System.Collections.Generic.List<string>();
            foreach (var e in Result.Entries)
            {
                if (e.Row != pos.row || e.Col != pos.col) continue;
                if (e.ForceShifted != pos.shiftedGlyph) continue;
                string disp = e.Shift ? $"Shift + {e.PcKey}" : e.PcKey;
                if (e.Shift && !e.OverrideShift && !e.ForceShifted) disp += "*"; // preserve-shift
                matches.Add(disp);
            }
            string pcKey = matches.Count > 0 ? string.Join(" | ", matches) : "(unmapped)";
            int idx = _grid.Rows.Add(pos.label, $"({pos.row},{pos.col})", pcKey);
            _grid.Rows[idx].Tag = (pos.row, pos.col, pos.shiftedGlyph);
        }
    }

    private void BeginCapture()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            _capturePrompt.Text = "Select a row first.";
            return;
        }
        _capturing = true;
        _capturePrompt.Text = "Press the PC key to bind to this MZ-700 key (Esc to cancel).";
        _captureBtn.Enabled = false;
        // Pull focus away from the DataGridView — DataGridView's own
        // ProcessCmdKey consumes some printable keys (',', '.', etc.) before
        // they reach our override. With focus on the form, the form's
        // ProcessCmdKey is the first to see the key.
        ActiveControl = null;
        Focus();
    }

    private void EndCapture(string message)
    {
        _capturing = false;
        _captureBtn.Enabled = true;
        _capturePrompt.Text = string.IsNullOrEmpty(message)
            ? "Double-click a row, or select then click \"Capture key\", then press any key."
            : message;
    }

    private void ClearSelectedMapping()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var tag = (ValueTuple<int, int, bool>)_grid.SelectedRows[0].Tag!;
        int row = tag.Item1, col = tag.Item2;
        bool shiftedGlyph = tag.Item3;
        // Clear only the bindings whose ForceShifted matches this row's
        // glyph variant — so clearing the unshifted entry doesn't wipe
        // bindings for the shifted-glyph entry at the same position.
        Result.Entries.RemoveAll(en => en.Row == row && en.Col == col && en.ForceShifted == shiftedGlyph);
        PopulateGrid();
        SelectRowFor(row, col, shiftedGlyph);
        _capturePrompt.Text = $"Cleared {KeyMapping.LabelFor(row, col, shiftedGlyph)}.";
    }

    private void ResetToDefaults()
    {
        if (MessageBox.Show(this, "Replace current mappings with built-in defaults?",
                "Reset to defaults", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;
        Result = KeyMapping.CreateDefault();
        PopulateGrid();
        _capturePrompt.Text = "Defaults restored.";
    }

    private void SelectRowFor(int row, int col, bool shiftedGlyph)
    {
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            var t = (ValueTuple<int, int, bool>)_grid.Rows[i].Tag!;
            if (t.Item1 == row && t.Item2 == col && t.Item3 == shiftedGlyph)
            {
                _grid.ClearSelection();
                _grid.Rows[i].Selected = true;
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 5);
                return;
            }
        }
    }
}
