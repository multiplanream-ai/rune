using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace Rune;

/// <summary>
/// Owns process-level app instancing. Packaged file activations are redirected
/// to the first Rune process so Explorer opens additional PDFs as tabs.
/// </summary>
public static class Program
{
    private const string MainInstanceKey = "Rune.Main";

    private static AppInstance? _mainInstance;
    private static DispatcherQueue? _dispatcher;

    internal static AppActivationArguments? InitialActivation { get; private set; }
    internal static ConcurrentQueue<AppActivationArguments> RedirectedActivations { get; } = new();

    [STAThread]
    public static async Task Main(string[] args)
    {
        // A custom WinUI entry point must initialize WinRT before using Windows
        // App SDK APIs.
        ComWrappersSupport.InitializeComWrappers();

        AppInstance current = AppInstance.GetCurrent();
        AppActivationArguments? activation = current.GetActivatedEventArgs();
        AppInstance main = AppInstance.FindOrRegisterForKey(MainInstanceKey);

        // A packaged file activation carries its PDF path in the activation
        // payload and can be redirected losslessly. The portable build can still
        // be launched as `Rune.exe file.pdf`; Windows App SDK does not wrap that
        // raw argv path in a rich File activation, so keep the existing fallback
        // behavior rather than silently dropping the requested file.
        bool rawCommandLineFileLaunch = IsRawCommandLineFileLaunch(activation);

        if (!main.IsCurrent && !rawCommandLineFileLaunch)
        {
            if (activation is not null)
            {
                await main.RedirectActivationToAsync(activation);
            }
            return;
        }

        InitialActivation = activation;

        if (main.IsCurrent)
        {
            _mainInstance = main;
            _mainInstance.Activated += MainInstance_Activated;
        }

        Application.Start(_ =>
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(_dispatcher));
            _ = new App();
        });
    }

    private static void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        RedirectedActivations.Enqueue(args);

        _dispatcher?.TryEnqueue(() =>
        {
            if (Application.Current is App app)
            {
                app.HandleRedirectedActivations();
            }
        });
    }

    private static bool IsRawCommandLineFileLaunch(AppActivationArguments? activation)
    {
        if (activation?.Kind == ExtendedActivationKind.File)
        {
            return false;
        }

        string[] commandLine = Environment.GetCommandLineArgs();
        return commandLine.Length > 1 && File.Exists(commandLine[1]);
    }
}
