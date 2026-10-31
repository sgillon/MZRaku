namespace MZRaku.Core;

/// <summary>
/// Host keyboard input into a machine: the core's input contract (v1.4
/// design decision D2). A front end forwards three events, in the order
/// the host raises them:
///   - <see cref="OnKeyDown"/> with the key code plus the modifier
///     flags held at the time (e.g. <c>Control | G</c>);
///   - <see cref="OnKeyPress"/> with the character the host's keyboard
///     layout produced, if any (WinForms KeyPress, SDL_TEXTINPUT);
///   - <see cref="OnKeyUp"/> with the key code plus the modifiers still
///     held;
///   - <see cref="ReleaseHeldKeys"/> when the host window loses keyboard
///     focus, because the key-ups for keys held then go elsewhere.
///
/// Each machine's keyboard pairs a key-down with the character that
/// follows it, so the host layout, AltGr and dead keys decide what is
/// typed. Front-end hotkeys (full screen, pause, menu shortcuts) are the
/// front end's business and never reach this interface, but their
/// modifier key-downs do: a Ctrl+Shift+K that opens a dialog leaves Ctrl
/// and Shift held here until <see cref="ReleaseHeldKeys"/>.
/// </summary>
public interface IKeyboardInput
{
    /// <summary>
    /// Returns true if the machine used the key, so the front end should
    /// treat the event as handled.
    /// </summary>
    bool OnKeyDown(HostKey keyData);

    void OnKeyPress(char ch);

    /// <summary>Returns true if the key released a held matrix slot.</summary>
    bool OnKeyUp(HostKey keyData);

    /// <summary>
    /// Release every key the host is holding down, as if each came up.
    /// Call when the host window loses keyboard focus. Matrix bits the
    /// machine's own auto-typer set are left alone.
    /// </summary>
    void ReleaseHeldKeys();
}
