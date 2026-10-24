using System;
using System.Drawing;
using System.Windows.Forms;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// Modal "press a button" dialog used from the Settings → Joystick tab
/// so the user can capture a controller button rather than know its
/// numeric index. Polls <see cref="IJoystickSource.GetCurrentButtons"/>
/// at ~30 Hz; the first 0→pressed transition wins. Already-held buttons
/// at open time are masked out so the dialog doesn't immediately fire
/// on whatever the user happened to be holding.
/// </summary>
public sealed class JoystickCaptureForm : Form
{
    private readonly IJoystickSource _input;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private uint _prevButtons;

    public int CapturedButtonIndex { get; private set; } = -1;

    public JoystickCaptureForm(IJoystickSource input)
    {
        _input = input;
        Text = "Capture button";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 130);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        ControlBox = false;

        var label = new Label
        {
            Text = "Press a button on your game controller…",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };
        Controls.Add(label);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Bottom,
            Height = 32,
        };
        Controls.Add(cancel);
        CancelButton = cancel;

        // Seed prev with whatever is held right now so an already-pressed
        // button doesn't immediately register on the very first tick.
        _prevButtons = ReadCombined();
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private uint ReadCombined() =>
        _input.GetCurrentButtons(0) | _input.GetCurrentButtons(1);

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var cur = ReadCombined();
        var pressed = cur & ~_prevButtons;
        _prevButtons = cur;
        if (pressed == 0) return;
        for (int i = 0; i < 32; i++)
        {
            if ((pressed & (1u << i)) == 0) continue;
            CapturedButtonIndex = i;
            _timer.Stop();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }
}
