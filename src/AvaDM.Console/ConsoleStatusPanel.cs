using AvaDM.Core;

namespace AvaDM.Console;

/// <summary>
/// Renders a fixed status panel (one row per tracked download) that's redrawn in place above
/// the input prompt, instead of the REPL printing a new scrolling line every progress tick.
/// Log/notice messages are queued and flushed just before the next prompt is shown, so they
/// never collide with a line the user is mid-typing.
///
/// Falls back to plain sequential <see cref="System.Console.WriteLine(string)"/> output when
/// stdin/stdout is redirected (piped input, CI, automated testing) - cursor-positioning tricks
/// only make sense against a real terminal.
/// </summary>
internal sealed class ConsoleStatusPanel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    private readonly Lock _consoleLock = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, DownloadProgress?> _latest = new();
    private readonly Queue<string> _pendingLog = new();
    private readonly Timer _refreshTimer;

    private int _footerTopRow;
    private int _footerHeight;
    private bool _footerOnScreen;

    public ConsoleStatusPanel()
    {
        Interactive = !System.Console.IsOutputRedirected && !System.Console.IsInputRedirected;
        _refreshTimer = new Timer(_ => RefreshPanelRows(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Interactive { get; }

    public void Track(string id)
    {
        lock (_consoleLock)
        {
            _order.Add(id);
            _latest[id] = null;
        }
    }

    public void UpdateProgress(string id, DownloadProgress progress)
    {
        if (!Interactive)
        {
            System.Console.WriteLine(FormatLine(id, progress));
            return;
        }

        lock (_consoleLock)
        {
            _latest[id] = progress;
        }
    }

    public void Log(string message)
    {
        if (!Interactive)
        {
            System.Console.WriteLine(message);
            return;
        }

        lock (_consoleLock)
        {
            _pendingLog.Enqueue(message);
        }
    }

    /// <summary>Flushes pending log lines, (re)draws the status panel and prompt, then blocks
    /// reading one line of input while the panel refreshes itself in place in the background.</summary>
    public string? ReadCommand()
    {
        if (!Interactive)
        {
            System.Console.Write("> ");
            return System.Console.ReadLine();
        }

        lock (_consoleLock)
        {
            ClearPreviousFooter();
            FlushPendingLogLocked();

            _footerTopRow = System.Console.CursorTop;
            DrawPanelRowsLocked();
            _footerHeight = _order.Count;

            System.Console.Write("> ");
            _footerOnScreen = true;
        }

        _refreshTimer.Change(RefreshInterval, RefreshInterval);
        var line = System.Console.ReadLine();
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        // Take the lock once to guarantee any in-flight refresh tick has finished before the
        // caller starts mutating state (e.g. adding a new tracked download) or we redraw again.
        lock (_consoleLock) { }

        return line;
    }

    private void ClearPreviousFooter()
    {
        if (!_footerOnScreen)
            return;

        var blank = new string(' ', ContentWidth());
        System.Console.SetCursorPosition(0, _footerTopRow);
        for (var i = 0; i < _footerHeight + 1; i++) // panel rows + prompt row
            System.Console.WriteLine(blank);
        System.Console.SetCursorPosition(0, _footerTopRow);
    }

    private void FlushPendingLogLocked()
    {
        while (_pendingLog.Count > 0)
            System.Console.WriteLine(_pendingLog.Dequeue());
    }

    private void DrawPanelRowsLocked()
    {
        foreach (var id in _order)
            System.Console.WriteLine(FormatLine(id, _latest[id]));
    }

    private void RefreshPanelRows()
    {
        lock (_consoleLock)
        {
            if (!_footerOnScreen || _footerHeight == 0)
                return;

            var (savedLeft, savedTop) = System.Console.GetCursorPosition();
            var width = ContentWidth();
            for (var i = 0; i < _footerHeight; i++)
            {
                var id = _order[i];
                System.Console.SetCursorPosition(0, _footerTopRow + i);
                System.Console.Write(PadOrTruncate(FormatLine(id, _latest[id]), width));
            }
            // Never touches the prompt row itself, so the user's in-progress input is untouched.
            System.Console.SetCursorPosition(savedLeft, savedTop);
        }
    }

    private static int ContentWidth() => Math.Max(10, System.Console.WindowWidth - 1);

    private static string PadOrTruncate(string text, int width) =>
        text.Length >= width ? text[..width] : text.PadRight(width);

    private static string FormatLine(string id, DownloadProgress? progress)
    {
        if (progress is null)
            return $"[{id}] pending...";

        var speed = progress.SpeedBytesPerSecond is { } s ? $"{s:N0} B/s" : "-";
        var total = progress.TotalBytes > 0 ? $"{progress.TotalBytes:N0}" : "???";
        return $"[{id}] {progress.State,-10} {progress.BytesDownloaded:N0}/{total} bytes @ {speed}";
    }
}
