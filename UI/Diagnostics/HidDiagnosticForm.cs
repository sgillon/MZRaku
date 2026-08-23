using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Live "what just happened" view of host keyboard + joystick input and
/// the resolved MZ matrix state. Opened from Debug → HID Diagnostic
/// (Ctrl+H); refreshed once per frame by MainForm.Timer_Tick.
///
/// Machine-aware: binds to one <see cref="IMachine"/> at construction
/// and displays its name in the title bar plus a prominent header row.
/// Body sections branch on <see cref="IMachine.Kind"/> for the small
/// number of machine-specific values (shift-bit source, mode-flag
/// source, hardware-joystick presence). Group headings and general
/// labels are machine-agnostic so nothing needs re-labelling if the
/// diagnostic swaps machines.
///
/// Practical-not-polished: a single monospace label per section, redrawn
/// in full each frame. Allocations are tiny (a few short strings) and
/// SmoothLabel suppresses the WM_ERASEBKGND flash.
/// </summary>
internal sealed class HidDiagnosticForm : DiagnosticFormBase
{
    private readonly IMachine _machine;
    private readonly JoystickInput _joystick;

    // Concrete keyboard references — machine-specific but needed for
    // Diag + matrix reads. Populated per Kind at construction.
    private readonly Keyboard? _mz700Kb;
    private readonly Mz80aKeyboard? _mz80aKb;

    private readonly Label _machineBanner = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold),
        Padding = new Padding(4, 2, 4, 2),
    };
    private readonly SmoothLabel _hostLabel = AutoSizeMonoLabel();
    private readonly SmoothLabel _mappingLabel = AutoSizeMonoLabel();
    private readonly SmoothLabel _mzLabel = FillMonoLabel();

    public HidDiagnosticForm(IMachine machine, JoystickInput joystick)
    {
        _machine = machine;
        _joystick = joystick;
        _mz700Kb = machine as MZ700 is { } mz7 ? mz7.Keyboard : null;
        _mz80aKb = machine as MZ80A is { } mz8 ? mz8.Keyboard : null;

        Text = $"HID Diagnostic — {MachineName(machine.Kind)}";
        _machineBanner.Text = $"Monitoring: {MachineName(machine.Kind)}";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(460, 580);
        MinimumSize = new Size(380, 480);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(6),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // machine banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // host input
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // mapping
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // MZ state
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // button row

        root.Controls.Add(_machineBanner, 0, 0);
        root.Controls.Add(AutoGroup("Host input (Windows side)", _hostLabel), 0, 1);
        root.Controls.Add(AutoGroup("Mapping (which layer matched)", _mappingLabel), 0, 2);
        root.Controls.Add(FillGroup("MZ machine state", _mzLabel), 0, 3);
        root.Controls.Add(BuildButtonRow(), 0, 4);
        Controls.Add(root);
    }

    private static string MachineName(MachineType kind) => kind switch
    {
        MachineType.MZ700 => "MZ-700",
        MachineType.MZ80A => "MZ-80A",
        _ => kind.ToString(),
    };

    private Control BuildButtonRow()
    {
        var copyBtn = new Button { Text = "Copy", AutoSize = true, Margin = new Padding(3) };
        copyBtn.Click += (_, _) => CopyDumpToClipboard();
        var saveBtn = new Button { Text = "Save…", AutoSize = true, Margin = new Padding(3) };
        saveBtn.Click += (_, _) => SaveDumpToFile(
            $"hid-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            "Save HID diagnostic snapshot");

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(StatusLabel, 0, 0);
        row.Controls.Add(copyBtn, 1, 0);
        row.Controls.Add(saveBtn, 2, 0);
        return row;
    }

    protected override string BuildFullDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MZRaku HID Diagnostic — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Monitoring: {MachineName(_machine.Kind)}");
        sb.AppendLine();
        sb.AppendLine("=== Host input (Windows side) ===");
        sb.AppendLine(BuildHostText());
        sb.AppendLine("=== Mapping (which layer matched) ===");
        sb.AppendLine(BuildMappingText());
        sb.AppendLine("=== MZ machine state ===");
        sb.AppendLine(BuildMzText());
        return sb.ToString();
    }

    /// <summary>Called once per frame by MainForm's Timer_Tick.</summary>
    public void RefreshIfVisible()
    {
        if (!Visible) return;
        _hostLabel.Text = BuildHostText();
        _mappingLabel.Text = BuildMappingText();
        _mzLabel.Text = BuildMzText();
    }

    private KeyboardDiagnostics Diag => _mz700Kb?.Diag ?? _mz80aKb!.Diag;

    private string BuildHostText()
    {
        var d = Diag;
        var sb = new StringBuilder();
        sb.AppendLine($"Last KeyDown : {FormatKeyData(d.LastKeyDown)}");
        sb.AppendLine($"Last KeyPress: {FormatChar(d.LastKeyChar)}");
        sb.AppendLine($"Last KeyUp   : {FormatKeyData(d.LastKeyUp)}");
        sb.AppendLine($"Mod keys held: {FormatModifiers(Control.ModifierKeys)}");
        sb.AppendLine();

        // Host-side PC gamepad buttons: always shown regardless of
        // whether the active machine has hardware-joystick emulation.
        // Useful for MZ-700 (feeds MZ-1X03), and foreshadows the
        // planned MZ-80A joystick-to-key mapping (v1.3.0 roadmap
        // item). Axis positions live in the MZ-side joystick section
        // (where they exist).
        sb.AppendLine($"PC gamepad buttons (SW1=button {MaskToIndex(_joystick.Sw1ButtonMask)}, " +
                      $"SW2=button {MaskToIndex(_joystick.Sw2ButtonMask)}):");
        for (uint slot = 0; slot < 2; slot++)
        {
            uint btns = _joystick.GetCurrentButtons(slot);
            sb.AppendLine($"  Slot {slot}: buttons=0x{btns:X8}");
        }
        return sb.ToString();
    }

    private static string Bit(bool b) => b ? "1" : "0";

    private static string MaskToIndex(uint mask)
    {
        if (mask == 0) return "-";
        for (int i = 0; i < 32; i++) if (((mask >> i) & 1) == 1) return i.ToString();
        return "-";
    }

    private string BuildMappingText()
    {
        var d = Diag;
        string shift = d.LastMzShift switch
        {
            true => "shift=ON",
            false => "shift=OFF",
            null => "shift=pass-through",
        };
        string resolved = d.LastRow >= 0
            ? $"({LabelRow()} {d.LastRow}, {LabelCol()} {d.LastCol})  {shift}"
            : "(no match)";
        var sb = new StringBuilder();
        sb.AppendLine($"Layer matched: {d.LastLayer}");
        sb.AppendLine($"Resolved     : {resolved}");
        return sb.ToString();
    }

    // MZ-700 uses "row/col"; MZ-80A uses "strobe/bit" terminology in
    // its own docs. Show whichever matches the active machine.
    private string LabelRow() => _machine.Kind == MachineType.MZ700 ? "row" : "strobe";
    private string LabelCol() => _machine.Kind == MachineType.MZ700 ? "col" : "bit";

    private string BuildMzText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Matrix (active-low; 0 = pressed, 10 {LabelRow()}s × 8 {LabelCol()}s):");
        sb.AppendLine("       7 6 5 4 3 2 1 0");
        for (int row = 0; row < 10; row++)
        {
            byte b = PeekMatrixRow(row);
            var bits = new StringBuilder(15);
            for (int bit = 7; bit >= 0; bit--)
            {
                bits.Append((b & (1 << bit)) != 0 ? '1' : '0');
                if (bit > 0) bits.Append(' ');
            }
            sb.AppendLine($"{LabelRow(),-6} {row}: {bits}  (${b:X2})");
        }
        sb.AppendLine();

        var lastScan = Diag.LastScanRow;
        sb.AppendLine($"Last {LabelRow()} scanned by ROM: {(lastScan < 0 ? "-" : lastScan.ToString())}");

        // Shift-bit + mode: source varies per machine, values labelled
        // by concept with the source in parentheses.
        if (_machine.Kind == MachineType.MZ700)
        {
            byte shiftMirror = _machine.Mem.Read(0x1170);
            sb.AppendLine($"MZ shift bit: {Bit((shiftMirror & 1) != 0)}  (source: $1170 RAM mirror = ${shiftMirror:X2})");
            byte modeByte = _machine.Mem.Read(0x0060);
            string mode = (modeByte & 0x10) != 0 ? "GRAPH" : "ALPHA";
            sb.AppendLine($"Mode: {mode}  (source: $0060 bit 4 = ${modeByte:X2} — only valid once BASIC is loaded)");
        }
        else
        {
            byte strobe0 = PeekMatrixRow(0);
            bool shiftPressed = (strobe0 & 0x01) == 0; // active-low
            sb.AppendLine($"MZ shift bit: {Bit(shiftPressed)}  (source: matrix strobe 0 bit 0 = ${strobe0:X2}, active-low)");
            string mode = _mz80aKb!.GraphMode ? "GRAPH" : "ALPHA";
            sb.AppendLine($"Mode: {mode}  (source: F11-driven local tracker)");
        }

        sb.AppendLine();
        sb.AppendLine("=== MZ-side joystick multiplex ===");
        if (_machine is MZ700 mz7)
        {
            for (uint slot = 0; slot < 2; slot++)
            {
                var s = mz7.Joystick.Sticks[slot];
                sb.AppendLine($"  Stick {slot}: " +
                    (s.Active
                        ? $"X={s.AxisX,3} Y={s.AxisY,3}  SW1={Bit(s.Sw1)} SW2={Bit(s.Sw2)}"
                        : "(not connected)"));
            }
        }
        else
        {
            // MZ-80A has no MZ-1X03-equivalent hardware. Placeholder
            // makes the section forward-compatible with the planned
            // v1.3.0 joystick-to-key mapping.
            sb.AppendLine("  MZ-80A: no hardware joystick emulation.");
            sb.AppendLine("  Planned: joystick → key mapping (v1.3.0 roadmap).");
        }
        return sb.ToString();
    }

    private byte PeekMatrixRow(int row)
    {
        if (_mz700Kb != null) return _mz700Kb.PeekMatrixRow(row);
        if (_mz80aKb != null) return _mz80aKb.PeekMatrixRow(row);
        return 0xFF;
    }

    private static string FormatKeyData(Keys k) =>
        k == Keys.None ? "(none)" : $"{k}  (0x{(int)k:X4})";

    private static string FormatChar(char ch)
    {
        if (ch == '\0') return "(none)";
        return ch >= 0x20 && ch < 0x7F
            ? $"'{ch}'  (0x{(int)ch:X2})"
            : $"0x{(int)ch:X2}";
    }

    private static string FormatModifiers(Keys mods)
    {
        if (mods == Keys.None) return "(none)";
        var parts = new System.Collections.Generic.List<string>();
        if ((mods & Keys.Shift) != 0) parts.Add("Shift");
        if ((mods & Keys.Control) != 0) parts.Add("Control");
        if ((mods & Keys.Alt) != 0) parts.Add("Alt");
        return string.Join(", ", parts);
    }
}
