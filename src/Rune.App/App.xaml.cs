using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Rune.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Rune;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private bool _handlingRedirectedActivations;

    public App()
    {
        InitializeComponent();

        // Safety net. An `async void` handler (every WinUI event handler is one)
        // has no Task to park an exception in, so the runtime rethrows it on the
        // dispatcher and the process dies unless Handled is set here. Log it and
        // keep running: a reader losing an hour of annotations to a transient
        // failure is far worse than a stale error message.
        UnhandledException += (_, e) =>
        {
            ErrorLog.Default.Write("UnhandledException", e.Exception);
            e.Handled = true;
            ReportToUser(e.Exception.Message);
        };

        // The other half: `_ = SomeAsync()` parks a failure in a Task nobody
        // awaits, which is silently swallowed. This surfaces those too.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Default.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Surfaces a background failure in the main window's InfoBar. Never a
    /// dialog: the very bug this net was added for was "a second ContentDialog
    /// was shown", so opening one from the handler could re-trigger it.
    /// </summary>
    private static void ReportToUser(string message)
    {
        try
        {
            (MainWindow as MainWindow)?.ReportBackgroundError(message);
        }
        catch
        {
            // Window may be closing or not built yet. The log already has it.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        List<string> activatedPaths = GetActivatedPdfPaths(Program.InitialActivation);
        var commandLine = GetCommandLineRequest();

        // The unpackaged/portable build receives its file through argv instead
        // of rich file activation. Keep that existing behavior for first launch.
        if (activatedPaths.Count == 0 && commandLine.Path is not null)
        {
            activatedPaths.Add(commandLine.Path);
        }

        // Opening a specific PDF is an explicit request. Start a fresh workspace
        // instead of resurrecting the previous session first. A normal Rune
        // launch continues to honor the user's RestoreSession setting.
        var window = new MainWindow(restoreSessionOnStartup: activatedPaths.Count == 0);
        MainWindow = window;

        // Window/taskbar icon (the exe icon comes from ApplicationIcon in the csproj).
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "rune.ico");
        if (File.Exists(icon))
        {
            window.AppWindow.SetIcon(icon);
        }

        window.Activate();

        foreach (string path in activatedPaths)
        {
            bool isCommandLineTarget = commandLine.Path is not null &&
                string.Equals(path, commandLine.Path, StringComparison.OrdinalIgnoreCase);

            await window.LoadDocumentAsync(
                path,
                isCommandLineTarget ? commandLine.Page : null,
                isCommandLineTarget ? commandLine.Zoom : null);
        }

        // A second launch can be redirected while the first window is still
        // being constructed. Drain anything that arrived during startup.
        HandleRedirectedActivations();
    }

    /// <summary>
    /// Opens files redirected from later Explorer launches in the existing Rune
    /// window. Program marshals this call onto the UI thread.
    /// </summary>
    internal async void HandleRedirectedActivations()
    {
        if (_handlingRedirectedActivations)
        {
            return;
        }

        _handlingRedirectedActivations = true;
        try
        {
            while (Program.RedirectedActivations.TryDequeue(out AppActivationArguments? activation))
            {
                if (MainWindow is not MainWindow window)
                {
                    continue;
                }

                // Bring the existing window forward for any redirected launch,
                // even if the activation did not carry a document.
                window.AppWindow.Show(true);

                foreach (string path in GetActivatedPdfPaths(activation))
                {
                    await window.LoadDocumentAsync(path);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write("RedirectedActivation", ex);
            ReportToUser(ex.Message);
        }
        finally
        {
            _handlingRedirectedActivations = false;

            // Close the small race where another activation arrives after the
            // queue looked empty but before the flag was cleared.
            if (!Program.RedirectedActivations.IsEmpty)
            {
                HandleRedirectedActivations();
            }
        }
    }

    private static List<string> GetActivatedPdfPaths(AppActivationArguments? activation)
    {
        var paths = new List<string>();

        if (activation?.Kind != ExtendedActivationKind.File ||
            activation.Data is not IFileActivatedEventArgs fileArgs)
        {
            return paths;
        }

        foreach (var item in fileArgs.Files)
        {
            if (item is StorageFile file &&
                string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(file.Path);
            }
        }

        return paths;
    }

    private static (string? Path, int? Page, double? Zoom) GetCommandLineRequest()
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length <= 1 || !File.Exists(commandLine[1]))
        {
            return (null, null, null);
        }

        int? page = null;
        double? zoom = null;
        for (int i = 2; i < commandLine.Length - 1; i++)
        {
            if (commandLine[i] == "--page" && int.TryParse(commandLine[i + 1], out int p))
            {
                page = p;
            }
            if (commandLine[i] == "--zoom" && double.TryParse(commandLine[i + 1], out double z))
            {
                zoom = z;
            }
        }

        return (commandLine[1], page, zoom);
    }
}
