using Avalonia;
using AvaDM.Core.Diagnostics;
using AvaDM.UI.Services;
using Serilog;
using System;
using System.Linq;

namespace AvaDM.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    //
    // AppLogging.Initialize() runs first (ahead of even the single-instance check) since
    // everything below it, including a losing second launch, writes through it. The
    // AppDomain/TaskScheduler handlers cover non-UI-thread crashes; Dispatcher.UIThread.
    // UnhandledException (registered in App.OnFrameworkInitializationCompleted) covers the UI
    // thread; the try/catch below is the last line of defense for anything that still reaches
    // Main - see https://docs.avaloniaui.net/docs/app-development/setting-unhandled-exceptions.
    [STAThread]
    public static void Main(string[] args)
    {
        // The Windows installer's uninstaller invokes the installed exe with this flag (see
        // packaging/windows/setup.iss's [UninstallRun]) so uninstalling also clears the
        // HKCU Run entry AutoStartService wrote - otherwise a stale entry would point at a now-
        // deleted exe. Handled before any Avalonia/logging init: this is a headless one-shot,
        // not a real app launch.
        if (args.Contains("--unregister-autostart"))
        {
            AutoStartService.SetEnabled(false);
            return;
        }

        // Logging is initialized before the single-instance check (rather than after, as the
        // comment above once said) specifically so a losing second launch still leaves a trail -
        // otherwise a launch that silently redirects to an already-running instance would leave
        // no evidence in the log that it ever ran.
        AppLogging.Initialize();

        var singleInstance = SingleInstanceService.TryAcquire();
        if (singleInstance is null)
        {
            Log.CloseAndFlush();
            return;
        }

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
            singleInstance.Dispose();
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
