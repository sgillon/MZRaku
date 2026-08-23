using System.Windows.Forms;

namespace MZRaku;

/// <summary>
/// Base for debugger-side tool windows. Owns the shared "user-close
/// hides but keeps the instance alive, real dispose only at app
/// shutdown" protocol both <see cref="DebuggerForm"/> and
/// <see cref="MemoryViewerForm"/> want, plus a
/// <see cref="PersistState"/> hook subclasses override to snapshot
/// geometry (and anything else) to settings on every close.
///
/// Kept minimal on purpose: subclasses still own their own layout,
/// their own machine reference, their own refresh cadence. The base
/// only claims the two things that were verbatim on both forms.
/// </summary>
internal abstract class DebugToolForm : Form
{
    /// <summary>
    /// Called from <see cref="OnFormClosing"/> on every close (user
    /// or app-shutdown). Subclasses snapshot their window rectangle
    /// into the appropriate <see cref="Settings.WindowState"/> field
    /// plus any pane-specific state (Debugger persists the
    /// breakpoint list too), then call <c>Settings.Save()</c>.
    /// </summary>
    protected abstract void PersistState();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // Snapshot on every close so the state lives through to the
        // next launch — including the user-hides-the-window path.
        PersistState();

        // Closing via the window's close button just hides the form
        // — keep state alive so reopening is instant. MainForm
        // disposes it for real at app shutdown.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
