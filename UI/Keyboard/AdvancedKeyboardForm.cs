using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MZ700Emul.Hardware;

namespace MZ700Emul;

/// <summary>
/// Advanced keyboard view, opened from Settings → Keyboard → Advanced
/// settings… Hosts the matrix grid and the raw overrides list — both
/// matrix-coord views that don't belong in the primary diagram-driven
/// flow. Runs as a resizable modal so the fixed-size Settings dialog
/// doesn't have to grow to fit the full matrix.
///
/// Edits made here mutate the shared <see cref="CharMapOverrides"/> /
/// <see cref="KeyOverride"/> instances directly; the parent Settings
/// dialog re-reads them once this form closes. Persistence still rides
/// on the parent's Apply / OK.
/// </summary>
public sealed class AdvancedKeyboardForm : Form
{
    private readonly MZ700? _machine;
    private readonly CharMapOverrides _charOverrides;
    private readonly KeyOverride _keyOverrides;
    private KeyboardMatrixGrid? _kbdGrid;
    private System.Windows.Forms.Timer? _kbdGridTimer;
    private ListView? _overridesList;
    private ListView? _unboundList;

    public AdvancedKeyboardForm(
        MZ700? machine,
        CharMapOverrides charOverrides,
        KeyOverride keyOverrides)
    {
        _machine = machine;
        _charOverrides = charOverrides;
        _keyOverrides = keyOverrides;

        Text = "Advanced Keyboard Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(820, 820);
        ShowInTaskbar = false;
        MaximizeBox = true;
        MinimizeBox = false;

        BuildUi();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10),
            AutoScroll = true,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // caption
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // matrix
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // unbound-slot panel
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // overrides list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // close button row

        root.Controls.Add(new Label
        {
            Text = "Matrix-coord view of the MZ-700 keyboard. Click a cell to edit "
                 + "its character binding. The overrides list below shows everything "
                 + "currently in effect across both layers.",
            AutoSize = true,
            MaximumSize = new Size(780, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        if (_machine != null)
        {
            _kbdGrid = new KeyboardMatrixGrid(_machine)
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Margin = new Padding(0, 0, 0, 10),
            };
            _kbdGrid.CellClicked += OnCellClicked;
            root.Controls.Add(_kbdGrid, 0, 1);

            // 10 Hz repaint so the live highlight tracks if anything
            // happens to assert matrix bits while the dialog is open
            // (auto-typer mid-sequence, etc.).
            _kbdGridTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _kbdGridTimer.Tick += (_, _) => _kbdGrid?.Invalidate();
            Load += (_, _) => _kbdGridTimer.Start();
            FormClosed += (_, _) =>
            {
                _kbdGridTimer.Stop();
                _kbdGridTimer.Dispose();
            };
        }
        else
        {
            root.Controls.Add(new Label
            {
                Text = "Matrix grid unavailable — emulator instance not provided.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }, 0, 1);
        }

        root.Controls.Add(BuildUnboundPanel(), 0, 2);

        _overridesList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        _overridesList.Columns.Add("Layer",      80);
        _overridesList.Columns.Add("PC trigger", 160);
        _overridesList.Columns.Add("MZ slot",    80);
        _overridesList.Columns.Add("Shift",      100);
        PopulateOverridesList();
        root.Controls.Add(_overridesList, 0, 3);

        var closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Width = 90,
            Anchor = AnchorStyles.Right,
        };
        var closeRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0),
        };
        closeRow.Controls.Add(closeBtn);
        root.Controls.Add(closeRow, 0, 4);
        AcceptButton = closeBtn;
        CancelButton = closeBtn;

        Controls.Add(root);
    }

    private void OnCellClicked(object? sender, CellClickedEventArgs e)
    {
        // Matrix cells edit character bindings only. Fixed-label slots
        // (Enter, cursors, SHIFT etc.) are reachable from the diagram
        // editor in the parent dialog.
        using var editor = new KeyBindingEditorForm(
            e.Row, e.Col, e.MzShift, _charOverrides);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _kbdGrid?.RefreshBindings();
        PopulateOverridesList();
        PopulateUnboundList();
    }

    private Control BuildUnboundPanel()
    {
        var caption = new Label
        {
            Text = "Unbound slots — reference cells nothing currently reaches:",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 4),
        };
        _unboundList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            Height = 110,
            Dock = DockStyle.Fill,
        };
        _unboundList.Columns.Add("MZ slot", 80);
        _unboundList.Columns.Add("Kind",    80);
        _unboundList.Columns.Add("Label",   240);
        PopulateUnboundList();

        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8),
            Dock = DockStyle.Fill,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 110f));
        panel.Controls.Add(caption, 0, 0);
        panel.Controls.Add(_unboundList, 0, 1);
        return panel;
    }

    private void PopulateUnboundList()
    {
        if (_unboundList == null) return;
        _unboundList.Items.Clear();
        var unbound = MatrixCoverage.FindUnbound(_charOverrides, _keyOverrides);
        foreach (var slot in unbound)
        {
            _unboundList.Items.Add(new ListViewItem(new[]
            {
                $"({slot.Row},{slot.Col})",
                slot.Kind.ToString(),
                BuildSlotLabel(slot),
            }));
        }
        if (_unboundList.Items.Count == 0)
        {
            var ok = new ListViewItem(new[]
            {
                "—",
                "—",
                "(every bindable reference cell has at least one PC binding)",
            })
            {
                ForeColor = SystemColors.GrayText,
            };
            _unboundList.Items.Add(ok);
        }
    }

    private static string BuildSlotLabel(Mz700MatrixReference.Slot slot)
    {
        // For Char slots prefer the glyph pair (e.g. "1 / !") so the user
        // immediately recognises which key is missing. Non-Char slots use
        // the Id directly (e.g. "F5", "BREAK", "←").
        if (slot.Kind == Mz700MatrixReference.SlotKind.Char)
        {
            var unshifted = slot.UnshiftedGlyph ?? "";
            var shifted = slot.ShiftedGlyph;
            return string.IsNullOrEmpty(shifted)
                ? $"{slot.Id}   '{unshifted}'"
                : $"{slot.Id}   '{unshifted}' / '{shifted}'";
        }
        return slot.Id;
    }

    private void PopulateOverridesList()
    {
        if (_overridesList == null) return;
        _overridesList.Items.Clear();

        foreach (var kv in _charOverrides.All.OrderBy(k => (int)k.Key))
        {
            var p = kv.Value;
            _overridesList.Items.Add(new ListViewItem(new[]
            {
                "CharMap",
                $"'{kv.Key}' (U+{(int)kv.Key:X4})",
                $"({p.Row},{p.Col})",
                p.MzShift ? "shifted" : "unshifted",
            }));
        }

        foreach (var kv in _keyOverrides.All.OrderBy(k => k.Key.ToString()))
        {
            var b = kv.Value;
            var shiftLabel = b.MzShift switch
            {
                true  => "shifted",
                false => "unshifted",
                _     => "pass-through",
            };
            _overridesList.Items.Add(new ListViewItem(new[]
            {
                "Key",
                kv.Key.ToString(),
                $"({b.Row},{b.Col})",
                shiftLabel,
            }));
        }

        if (_overridesList.Items.Count == 0)
        {
            var empty = new ListViewItem(new[] { "—", "(no overrides set)", "—", "—" })
            {
                ForeColor = SystemColors.GrayText,
            };
            _overridesList.Items.Add(empty);
        }
    }
}
