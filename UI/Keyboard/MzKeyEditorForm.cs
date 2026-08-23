using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Phase 2 binding editor — opens when the user clicks an MZ key on the
/// diagram. Shows the target MZ key with up to two slot cards
/// side-by-side (unshifted / shifted, when both glyphs exist). Each card
/// lists the PC keys that currently produce the slot, with Edit and
/// Reset buttons.
///
/// Character keys (1, A, etc.) edit via <see cref="CharMapOverrides"/>
/// — wired here directly through <see cref="KeyBindingEditorForm"/> as
/// the capture sub-flow.
///
/// Fixed-label keys (CR, GRAPH, ALPHA, CTRL, SHIFT, BREAK, INST, DEL,
/// cursor arrows) edit via <see cref="KeyOverride"/>. Layer routing for
/// those lands at P2-6 — until then this form shows the current VK
/// binding read-only with a "coming in P2-6" note.
///
/// Mutations are live: edits commit straight into the override layer
/// the moment the capture flow returns OK. Persistence still rides on
/// the parent SettingsForm's Apply / OK, matching the rest of the
/// dialog.
/// </summary>
public sealed class MzKeyEditorForm : Form
{
    private readonly PhysicalKey _key;
    private readonly IKeyboardEditorContext _context;
    private readonly ICharMapOverridesView _charOverrides;
    private readonly KeyOverride _keyOverrides;

    // Per-slot binding-text labels, kept so Edit/Reset can refresh them
    // without rebuilding the whole UI.
    private Label? _unshiftedBindLabel;
    private Label? _shiftedBindLabel;
    private Label? _vkBindLabel;

    public MzKeyEditorForm(PhysicalKey key, IKeyboardEditorContext context)
    {
        _key = key;
        _context = context;
        _charOverrides = context.CharOverrides;
        _keyOverrides = context.KeyOverrides;

        Text = $"Edit MZ key — {DescribeKey(key, context)}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 400);

        BuildUi();
    }

    // ====================================================================
    // UI construction
    // ====================================================================

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildButtonRow(), 0, 2);

        Controls.Add(root);
    }

    private Label BuildHeader()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Target MZ key: {DescribeKey(_key, _context)}");
        if (_key.Row.HasValue && _key.Col.HasValue)
            sb.Append($"   ·   matrix slot ({_key.Row}, {_key.Col})");
        return new Label
        {
            Text = sb.ToString(),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12),
        };
    }

    private Control BuildBody()
    {
        // BLANK / layout-filler keys.
        if (!_key.Row.HasValue || !_key.Col.HasValue)
            return BuildInfoPanel(
                "This is a layout-only filler key with no MZ matrix slot. There's nothing to edit.");

        // Fixed-label keys (CR, GRAPH, ALPHA, CTRL, SHIFT, BREAK, INST,
        // DEL, cursors). VK editing arrives in P2-6 — display only for
        // now.
        if (!string.IsNullOrEmpty(_key.FixedLabel))
            return BuildVkBindingPanel();

        // Character keys — dual-slot view.
        return BuildCharacterSlotsPanel();
    }

    private static Control BuildInfoPanel(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(480, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 8, 0, 0),
    };

    private Control BuildVkBindingPanel()
    {
        int row = _key.Row!.Value;
        int col = _key.Col!.Value;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };

        flow.Controls.Add(BuildSlotCard(
            title: "VK binding",
            glyphText: _key.FixedLabel ?? "?",
            bindText: FormatVkBinding(row, col),
            defaultText: FormatVkDefault(row, col),
            out _vkBindLabel,
            editEnabled: true,
            resetEnabled: HasVkOverrideAt(row, col),
            onEdit: OnEditVkSlot,
            onReset: OnResetVkSlot));

        return flow;
    }

    private Control BuildCharacterSlotsPanel()
    {
        int row = _key.Row!.Value;
        int col = _key.Col!.Value;

        // Resolve glyph labels for each side. Honour the layout's
        // explicit overrides first (used by the @/' and £/↓ keys where
        // the CharMap can't represent both sides), then fall back to
        // the machine's glyph catalog.
        string? unshifted = _key.UnshiftedLabel
            ?? _context.FindGlyphAt(row, col, false)?.ToString();
        string? shifted = _key.ShiftedLabel
            ?? _context.FindGlyphAt(row, col, true)?.ToString();

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };

        if (!string.IsNullOrEmpty(unshifted))
        {
            flow.Controls.Add(BuildSlotCard(
                title: "Unshifted",
                glyphText: unshifted!,
                bindText: FormatCharBinding(row, col, mzShift: false),
                defaultText: FormatCharDefault(row, col, mzShift: false),
                out _unshiftedBindLabel,
                editEnabled: true,
                resetEnabled: HasOverrideAt(row, col, mzShift: false),
                onEdit: () => OnEditCharSlot(mzShift: false),
                onReset: () => OnResetCharSlot(mzShift: false)));
        }

        if (!string.IsNullOrEmpty(shifted))
        {
            flow.Controls.Add(BuildSlotCard(
                title: "Shifted",
                glyphText: shifted!,
                bindText: FormatCharBinding(row, col, mzShift: true),
                defaultText: FormatCharDefault(row, col, mzShift: true),
                out _shiftedBindLabel,
                editEnabled: true,
                resetEnabled: HasOverrideAt(row, col, mzShift: true),
                onEdit: () => OnEditCharSlot(mzShift: true),
                onReset: () => OnResetCharSlot(mzShift: true)));
        }

        return flow;
    }

    private GroupBox BuildSlotCard(
        string title,
        string glyphText,
        string bindText,
        string defaultText,
        out Label bindLabel,
        bool editEnabled,
        bool resetEnabled,
        Action onEdit,
        Action onReset)
    {
        var box = new GroupBox
        {
            Text = title,
            Width = 220,
            Height = 245,
            Margin = new Padding(4, 4, 8, 4),
            Padding = new Padding(8, 12, 8, 8),
        };

        var glyph = new Label
        {
            Text = glyphText,
            Dock = DockStyle.Top,
            Height = 50,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(FontFamily.GenericSansSerif, 22f, FontStyle.Bold),
        };

        var bindCaption = new Label
        {
            Text = "Currently bound to:",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 18,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };
        var bind = new Label
        {
            Text = bindText,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
        };
        bindLabel = bind;

        // Default binding — shown so the user can see what Reset will
        // restore the slot to. Always rendered, even when the current
        // binding already matches the default.
        var defaultCaption = new Label
        {
            Text = "Default (what Reset restores):",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 18,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };
        var defaultLine = new Label
        {
            Text = defaultText,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Italic),
            ForeColor = SystemColors.ControlText,
        };

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        var editBtn = new Button { Text = "Edit…", Width = 80, Enabled = editEnabled };
        var resetBtn = new Button { Text = "Reset", Width = 80, Enabled = resetEnabled };
        editBtn.Click += (_, _) => onEdit();
        resetBtn.Click += (_, _) => onReset();
        btnRow.Controls.Add(editBtn);
        btnRow.Controls.Add(resetBtn);

        // Order matters: DockStyle.Top stacks in reverse-add order.
        box.Controls.Add(btnRow);
        box.Controls.Add(defaultLine);
        box.Controls.Add(defaultCaption);
        box.Controls.Add(bind);
        box.Controls.Add(bindCaption);
        box.Controls.Add(glyph);
        return box;
    }

    private FlowLayoutPanel BuildButtonRow()
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        var cancelBtn = new Button { Text = "Close", Width = 80, DialogResult = DialogResult.OK };
        cancelBtn.Click += (_, _) => Close();
        row.Controls.Add(cancelBtn);
        AcceptButton = cancelBtn;
        CancelButton = cancelBtn;
        return row;
    }

    // ====================================================================
    // Slot actions — VK layer (fixed-label keys)
    // ====================================================================

    private void OnEditVkSlot()
    {
        using var editor = new VkBindingEditorForm(
            _key.Row!.Value, _key.Col!.Value,
            _key.FixedLabel ?? "?",
            _context);
        if (editor.ShowDialog(this) == DialogResult.OK)
            RefreshVkSlotLabel();
    }

    private void OnResetVkSlot()
    {
        int row = _key.Row!.Value;
        int col = _key.Col!.Value;
        var toRemove = _keyOverrides.All
            .Where(kv => kv.Value.Row == row && kv.Value.Col == col)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in toRemove)
            _keyOverrides.Remove(k);
        RefreshVkSlotLabel();
    }

    private void RefreshVkSlotLabel()
    {
        if (_vkBindLabel == null) return;
        _vkBindLabel.Text = FormatVkBinding(_key.Row!.Value, _key.Col!.Value);
    }

    private bool HasVkOverrideAt(int row, int col)
    {
        foreach (var kv in _keyOverrides.All)
            if (kv.Value.Row == row && kv.Value.Col == col)
                return true;
        return false;
    }

    // ====================================================================
    // Slot actions — character layer
    // ====================================================================

    private void OnEditCharSlot(bool mzShift)
    {
        using var editor = new KeyBindingEditorForm(
            _key.Row!.Value, _key.Col!.Value, mzShift, _context);
        if (editor.ShowDialog(this) == DialogResult.OK)
            RefreshCharSlotLabels();
    }

    private void OnResetCharSlot(bool mzShift)
    {
        int row = _key.Row!.Value;
        int col = _key.Col!.Value;

        // Clear positive overrides that point at this slot.
        var toRemove = _charOverrides.All
            .Where(kv => kv.Value.Row == row && kv.Value.Col == col && kv.Value.MzShift == mzShift)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var c in toRemove)
            _charOverrides.Remove(c);

        // Restore any char-map defaults that pointed at this slot and
        // were suppressed by a previous Save (slot-replace path in
        // KeyBindingEditorForm.OnSave). Without this, Reset clears the
        // override but the slot stays "unbound" because the original
        // default is still flagged as suppressed.
        var toUnsuppress = new List<char>();
        foreach (var def in _context.CharDefaults)
        {
            if (!_charOverrides.IsSuppressed(def.Key)) continue;
            if (def.Value.Row == row && def.Value.Col == col && def.Value.MzShift == mzShift)
                toUnsuppress.Add(def.Key);
        }
        foreach (var c in toUnsuppress)
            _charOverrides.Unsuppress(c);

        RefreshCharSlotLabels();
    }

    private void RefreshCharSlotLabels()
    {
        int row = _key.Row!.Value;
        int col = _key.Col!.Value;
        if (_unshiftedBindLabel != null)
            _unshiftedBindLabel.Text = FormatCharBinding(row, col, false);
        if (_shiftedBindLabel != null)
            _shiftedBindLabel.Text = FormatCharBinding(row, col, true);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static string DescribeKey(PhysicalKey k, IKeyboardEditorContext context)
    {
        if (!string.IsNullOrEmpty(k.FixedLabel)) return k.FixedLabel!;
        if (!string.IsNullOrEmpty(k.UnshiftedLabel) || !string.IsNullOrEmpty(k.ShiftedLabel))
            return $"{k.UnshiftedLabel ?? "·"} / {k.ShiftedLabel ?? "·"}";
        if (k.Row.HasValue && k.Col.HasValue)
        {
            char? un = context.FindGlyphAt(k.Row.Value, k.Col.Value, false);
            char? sh = context.FindGlyphAt(k.Row.Value, k.Col.Value, true);
            if (un.HasValue && sh.HasValue) return $"{un.Value} / {sh.Value}";
            if (un.HasValue) return un.Value.ToString();
            if (sh.HasValue) return sh.Value.ToString();
        }
        return k.Id;
    }

    /// <summary>
    /// PC chars that map to (row, col, mzShift) via the built-in char
    /// defaults alone — what the slot reverts to when the user clicks
    /// Reset and the override layer is cleared.
    /// </summary>
    private string FormatCharDefault(int row, int col, bool mzShift)
    {
        var labels = new List<string>();
        foreach (var kv in _context.CharDefaults)
        {
            if (kv.Value.Row == row && kv.Value.Col == col && kv.Value.MzShift == mzShift)
                labels.Add(PrettyChar(kv.Key));
        }
        var uniq = labels.Distinct().ToList();
        return uniq.Count == 0 ? "(no default — Reset clears the slot)" : string.Join("  ", uniq);
    }

    /// <summary>
    /// PC virtual keys that map to (row, col) via the built-in
    /// special-key defaults alone — VK-side analogue of
    /// <see cref="FormatCharDefault"/>.
    /// </summary>
    private string FormatVkDefault(int row, int col)
    {
        var labels = new List<string>();
        foreach (var kv in _context.SpecialKeyMap)
        {
            if (kv.Value.Row == row && kv.Value.Col == col)
                labels.Add(VkLabel(kv.Key, _context));
        }
        return labels.Count == 0 ? "(no default — Reset clears the slot)" : string.Join("  ", labels);
    }

    private string FormatCharBinding(int row, int col, bool mzShift)
    {
        var labels = new List<string>();
        var overridden = new HashSet<char>();
        foreach (var kv in _charOverrides.All)
        {
            if (kv.Value.Row == row && kv.Value.Col == col && kv.Value.MzShift == mzShift)
            {
                labels.Add(PrettyChar(kv.Key));
                overridden.Add(kv.Key);
            }
        }
        foreach (var kv in _context.CharDefaults)
        {
            if (overridden.Contains(kv.Key)) continue;
            if (_charOverrides.IsSuppressed(kv.Key)) continue;
            if (kv.Value.Row == row && kv.Value.Col == col && kv.Value.MzShift == mzShift)
                labels.Add(PrettyChar(kv.Key));
        }
        // Collapse duplicates (e.g. 'A' / 'a' canonicalised to "A").
        var uniq = labels.Distinct().ToList();
        return uniq.Count == 0 ? "(no PC binding)" : string.Join("  ", uniq);
    }

    private string FormatVkBinding(int row, int col)
    {
        var labels = new List<string>();
        // Any VK that's mentioned in the override layer no longer fires
        // via its SpecialKeyMap default — even if the override targets a
        // different slot. Mark them all so the default-pass below skips
        // them. Without this, a VK rebound to a different slot would
        // still appear as bound to its original SpecialKeyMap slot.
        var claimedVks = new HashSet<Keys>();
        foreach (var kv in _keyOverrides.All)
            claimedVks.Add(kv.Key);
        foreach (var kv in _keyOverrides.All)
        {
            if (kv.Value.Row == row && kv.Value.Col == col)
                labels.Add(VkLabel(kv.Key, _context));
        }
        foreach (var kv in _context.SpecialKeyMap)
        {
            if (claimedVks.Contains(kv.Key)) continue;
            if (kv.Value.Row == row && kv.Value.Col == col)
                labels.Add(VkLabel(kv.Key, _context));
        }
        return labels.Count == 0 ? "(no PC binding)" : string.Join("  ", labels);
    }

    private bool HasOverrideAt(int row, int col, bool mzShift)
    {
        foreach (var kv in _charOverrides.All)
            if (kv.Value.Row == row && kv.Value.Col == col && kv.Value.MzShift == mzShift)
                return true;
        // A previous Save may have suppressed CharMap defaults for this
        // slot without leaving a matching positive override (e.g. the
        // user moved the binding elsewhere, then opened the slot fresh).
        // Reset should restore those — so the button needs to be active
        // whenever any restorable suppression exists at the slot. The
        // per-machine default set lives on the editor context; reading
        // CharMap.Defaults here would silently walk the MZ-700 table on
        // MZ-80A and leave the Reset button greyed out for a live slot.
        foreach (var def in _context.CharDefaults)
        {
            if (!_charOverrides.IsSuppressed(def.Key)) continue;
            if (def.Value.Row == row && def.Value.Col == col && def.Value.MzShift == mzShift)
                return true;
        }
        return false;
    }

    private static string PrettyChar(char c)
    {
        if (char.IsLetter(c)) return c.ToString().ToUpperInvariant();
        if (c == ' ') return "Space";
        return c.ToString();
    }

    private static string VkLabel(Keys k, IKeyboardEditorContext context) =>
        context.SpecialKeyLabels.TryGetValue(k, out var s) ? s : k.ToString();
}
