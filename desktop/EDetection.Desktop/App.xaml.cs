using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using EDetection.Desktop.Services;

namespace EDetection.Desktop;

public partial class App : Application
{
    public const string StartupMinimizedArgument = "--startup-minimized";

    private const string SingleInstanceKey = "EDetection.Desktop.MainInstance";

    private AppInstance? _mainInstance;
    private volatile bool _pendingExternalActivation;

    public static MainWindow? CurrentWindow { get; private set; }

    public App()
    {
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var commandLineArgs = Environment.GetCommandLineArgs();
            var commandLineStartupMinimized = commandLineArgs.Contains(
                StartupMinimizedArgument,
                StringComparer.OrdinalIgnoreCase);
            if (await TryRedirectToExistingInstanceAsync(commandLineStartupMinimized))
            {
                return;
            }

            var savedSettings = new SettingsService().Load();
            var startMinimized = commandLineStartupMinimized || savedSettings.StartMinimizedToTray;
            CurrentWindow = new EDetection.Desktop.MainWindow();
            CurrentWindow.Closed += (_, _) => CurrentWindow = null;
            var preparedForStartupHide = startMinimized
                && CurrentWindow.PrepareForStartupHidden();

            CurrentWindow.Activate();

            if (preparedForStartupHide)
            {
                CurrentWindow.HideForStartup();
            }

            if (_pendingExternalActivation)
            {
                _pendingExternalActivation = false;
                CurrentWindow.RestoreFromExternalActivation();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.WriteException("OnLaunched failed", ex);
            throw;
        }
    }

    private async Task<bool> TryRedirectToExistingInstanceAsync(bool quietStartup)
    {
        AppInstance mainInstance;
        try
        {
            mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.WriteException("Single instance registration failed", ex);
            return false;
        }

        if (mainInstance.IsCurrent)
        {
            RegisterCurrentAsMainInstance(mainInstance);
            return false;
        }

        if (!quietStartup)
        {
            try
            {
                var activatedEventArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedEventArgs is not null)
                {
                    await mainInstance.RedirectActivationToAsync(activatedEventArgs);
                }
            }
            catch (Exception ex)
            {
                StartupDiagnostics.WriteException("Single instance activation redirect failed", ex);
            }
        }

        Environment.Exit(0);
        return true;
    }

    private void RegisterCurrentAsMainInstance(AppInstance appInstance)
    {
        if (_mainInstance is not null)
        {
            return;
        }

        _mainInstance = appInstance;
        _mainInstance.Activated += OnAppInstanceActivated;
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        var window = CurrentWindow;
        if (window is not null
            && window.DispatcherQueue.TryEnqueue(window.RestoreFromExternalActivation))
        {
            return;
        }

        _pendingExternalActivation = true;
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.WriteException("WinUI unhandled exception", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            StartupDiagnostics.WriteException("AppDomain unhandled exception", exception);
        }
        else
        {
            StartupDiagnostics.Write($"AppDomain unhandled exception: {e.ExceptionObject}");
        }
    }
}
