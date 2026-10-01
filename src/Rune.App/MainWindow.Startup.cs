using Rune.Services;

namespace Rune;

public sealed partial class MainWindow
{
    /// <summary>
    /// Creates the main window with explicit control over whether the saved tab
    /// session should be restored. File activation starts a fresh workspace;
    /// launching Rune normally keeps the existing restore-session preference.
    /// </summary>
    internal MainWindow(bool restoreSessionOnStartup) : this()
    {
        if (!restoreSessionOnStartup)
        {
            // RestoreSessionAsync runs only after the window is activated, so
            // clearing this in-memory snapshot prevents stale tabs from being
            // resurrected without changing the user's RestoreSession setting.
            _state.Session = new SessionState();
        }
    }
}
