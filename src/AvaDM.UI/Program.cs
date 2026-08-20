using Avalonia;
using AvaDM.Core.Diagnostics;
using Serilog;
using System;

namespace AvaDM.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    //
    // AppLogging.Initialize() runs first since everything below (including Avalonia's own
    // LogToDelegate sink) writes through it. The AppDomain/TaskScheduler handlers cover
    // non-UI-thread crashes; Dispatcher.UIThread.UnhandledException (registered in
    // App.OnFrameworkInitializationCompleted) covers the UI thread; the try/catch below is the
    // last line of defense for anything that still reaches Main - see
    // https://docs.avaloniaui.net/docs/app-development/setting-unhandled-exceptions.
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize();
        AppLogging.InstallGlobalExceptionHandlers(CrashReporter.Report);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Application terminated unexpectedly");
            CrashReporter.Report(e);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Routes Avalonia's own binding/layout/render warnings (Warning level and above,
            // the same default LogToTrace uses) into the same rolling log file as everything
            // else, instead of System.Diagnostics.Trace - invisible outside an attached debugger.
            .LogToDelegate(message => Log.Warning("[Avalonia] {Message}", message));
}
