using Avalonia;
using System;

namespace StreamVODTracker;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLogger.Error("Unhandled", $"Fatal={eventArgs.IsTerminating}", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            AppLogger.Error("UnobservedTask", "An unobserved task exception occurred.", eventArgs.Exception);
        AppLogger.Info("Application", $"Starting. Version={typeof(Program).Assembly.GetName().Version}; LogDirectory={AppLogger.LogDirectory}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Application", "Application exited with an exception.", exception);
            throw;
        }
        finally
        {
            AppLogger.Info("Application", "Application exited.");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
