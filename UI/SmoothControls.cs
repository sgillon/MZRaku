using System;
using System.Windows.Forms;

namespace MZ700Emul;

// Local WinForms control subclasses that turn off the WM_ERASEBKGND →
// WM_PAINT erase-then-redraw flicker that bites every time we set new
// text on a Label, or repaint an owner-drawn ListBox at 60 fps.
//
// Recipe per type:
//   - DoubleBuffered = true so .NET wraps WM_PAINT in a back-buffer.
//   - For ListBox, also swallow WM_ERASEBKGND, because the Win32
//     LISTBOX class doesn't honour the .NET buffering hint for its
//     internal item-paint loop — the system would otherwise clear the
//     row to white before our DrawItem handler runs, producing a
//     visible flash on dense rows (memory viewer hex+ASCII).
//   - Label/TableLayoutPanel: DoubleBuffered alone is enough because
//     they're managed WinForms controls without external Win32 paint
//     paths.

internal sealed class SmoothListBox : ListBox
{
    public SmoothListBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_ERASEBKGND = 0x14;
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
    }
}

internal sealed class SmoothLabel : Label
{
    public SmoothLabel() { DoubleBuffered = true; }
}

internal sealed class SmoothTableLayoutPanel : TableLayoutPanel
{
    public SmoothTableLayoutPanel() { DoubleBuffered = true; }
}
