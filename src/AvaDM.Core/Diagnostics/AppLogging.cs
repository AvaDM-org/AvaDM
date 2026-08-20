using Serilog;
using Serilog.Events;

namespace AvaDM.Core.Diagnostics;

/// <summary>
/// Process-wide crash/diagnostic logging, shared by every AvaDM entry point (console harness and
/// desktop UI) so a crash on a client machine leaves behind a log a developer can actually use.
/// Writes to a size- and count-bounded rolling file rather than app-level download progress -
/// per-chunk byte counters belong in the UI, not on disk - so the file stays small (default cap
/// ~50 MB) while still capturing the state transitions and warnings leading up to a crash.
/// </summary>
public static class AppLogging
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const int RetainedFileCount = 5;

    public static string LogDirectory { get; } = ResolveLogDirectory();

    /// <summary>Configures the process-wide Serilog logger. Call once, as early as possible in
    /// each entry point's Main, before any other AvaDM code runs.</summary>
    public static void Initialize()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDirectory, "avadm-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCount,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information(
            "AvaDM starting - version {Version}, OS {OS}, .NET {Runtime}",
            typeof(AppLogging).Assembly.GetName().Version,
            Environment.OSVersion,
            Environment.Version);
    }

    /// <summary>Registers handlers for exceptions that would otherwise crash the process with no
    /// trace: unhandled exceptions on any thread (including the UI thread - Avalonia has no
    /// WPF-style DispatcherUnhandledException hook, so an exception that escapes the UI thread's
    /// message loop surfaces here like any other unhandled exception) and faulted background
    /// tasks nobody awaited. Both log at Fatal/Error and flush immediately, since the process may
    /// terminate right after the handler returns. <paramref name="onFatal"/> runs after the crash
    /// is durably on disk - e.g. to offer the user a way to report it.</summary>
    public static void InstallGlobalExceptionHandlers(Action<Exception>? onFatal = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            Log.Fatal(exception, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
            Log.CloseAndFlush();

            if (exception is not null)
                onFatal?.Invoke(exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>Path to the most recently written log file, for attaching to a crash report.
    /// <c>null</c> if the log directory can't be read (e.g. permissions) or is empty.</summary>
    public static string? GetLatestLogFilePath()
    {
        try
        {
            return Directory.EnumerateFiles(LogDirectory, "avadm-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLogDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaDM", "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
