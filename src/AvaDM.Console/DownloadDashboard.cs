using AvaDM.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AvaDM.Console;

/// <summary>
/// Terminal.Gui dashboard: one block per tracked download (each its own file, each with its own
/// parallel chunks under the hood), showing the aggregate progress Core reports plus one line
/// per chunk with that chunk's own byte range and progress, a scrolling log pane below it, and
/// a command input line pinned to the bottom.
///
/// The downloads pane is a plain read-only <see cref="TextView"/> rather than a <see cref="ListView"/>:
/// each download contributes a variable number of rows (one header + one per chunk, and the
/// chunk count isn't known until Core's HEAD request returns), which doesn't fit a ListView's
/// fixed one-row-per-item model. Rebuilding the whole block of text on every update is simple
/// and cheap enough at console-refresh rates.
///
/// Replaces the old <c>ConsoleStatusPanel</c> hand-rolled cursor-positioning approach.
/// Terminal.Gui owns the screen, layout, and redraw - there's no more manual row-count/width
/// bookkeeping to get out of sync, which was the root cause of the old panel's corruption bug.
///
/// All public methods are safe to call from any thread. Download progress/log events fire from
/// background download threads, so every one of them is marshaled onto the Terminal.Gui main
/// loop via <see cref="IApplication.Invoke(Action)"/> before touching a view.
///
/// Uses v2's instance-based <see cref="IApplication"/> model, not the static
/// <c>Application.Init/Run/Invoke/RequestStop/Shutdown</c> members - those are all marked
/// [Obsolete] as of 2.4.17 ("the legacy static Application object is going away").
/// </summary>
internal sealed class DownloadDashboard
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, DownloadProgress?> _latest = new();
    private readonly Dictionary<string, IReadOnlyList<ChunkProgress>> _chunks = new();

    private readonly IApplication _app;
    private readonly TextView _downloadsView;
    private readonly TextView _log;
    private readonly TextField _commandField;
    private readonly Window _window;

    /// <summary>Raised on the UI thread when the user presses Enter in the command field.
    /// The field is already cleared by the time this fires.</summary>
    public event Action<string>? CommandEntered;

    public DownloadDashboard()
    {
        // Application.Create() is the v2 entry point; everything else (Run/Invoke/RequestStop/
        // Dispose) goes through the IApplication instance it returns, not the static Application
        // class.
        _app = Application.Create().Init();

        _downloadsView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(40),
            ReadOnly = true,
        };

        _log = new TextView
        {
            X = 0,
            Y = Pos.Bottom(_downloadsView),
            Width = Dim.Fill(),
            Height = Dim.Fill(2), // leaves room for the prompt row below
            ReadOnly = true,
        };

        var prompt = new Label { Text = "> ", X = 0, Y = Pos.AnchorEnd(1) };
        _commandField = new TextField
        {
            X = Pos.Right(prompt),
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
        };
        // RISK SPOT 1: if `Key` doesn't compare directly to `Key.Enter` on your installed
        // version, try `key.KeyCode != KeyCode.Enter` instead - both forms have existed across
        // v2 pre-releases.
        _commandField.KeyDown += (_, key) =>
        {
            if (key != Key.Enter)
                return;

            var line = _commandField.Text;
            _commandField.Text = string.Empty;
            key.Handled = true;

            if (!string.IsNullOrWhiteSpace(line))
                CommandEntered?.Invoke(line);
        };

        _window = new Window { Title = "AvaDM" };
        _window.Add(_downloadsView, _log, prompt, _commandField);
        _commandField.SetFocus();
    }

    /// <summary>Blocks until the app quits (Esc, or a 'quit'/'exit' command routed through
    /// <see cref="RequestQuit"/>), then disposes the application.</summary>
    public void Run()
    {
        using (_app)
            _app.Run(_window);
    }

    public void RequestQuit() => _app.Invoke(() => _app.RequestStop());

    public void Track(string id)
    {
        _app.Invoke(() =>
        {
            _order.Add(id);
            _latest[id] = null;
            _chunks[id] = [];
            RenderDownloadsLocked();
        });
    }

    public void UpdateProgress(string id, DownloadProgress progress)
    {
        _app.Invoke(() =>
        {
            _latest[id] = progress;
            RenderDownloadsLocked();
        });
    }

    /// <summary>Per-chunk counterpart to <see cref="UpdateProgress"/> - wire this to
    /// <see cref="DownloadHandle.ChunksChanged"/> to show each chunk's own byte range and
    /// progress under its download's header line.</summary>
    public void UpdateChunks(string id, IReadOnlyList<ChunkProgress> chunks)
    {
        _app.Invoke(() =>
        {
            _chunks[id] = chunks;
            RenderDownloadsLocked();
        });
    }

    public void Log(string message)
    {
        _app.Invoke(() =>
        {
            _log.Text += message + Environment.NewLine;
            // RISK SPOT 3: if there's no MoveEnd() on your version, this is cosmetic only
            // (auto-scroll-to-bottom) - safe to delete if it doesn't compile.
            _log.MoveEnd();
        });
    }

    // Must only be called on the Terminal.Gui main loop (i.e. from inside _app.Invoke) - it
    // reads _order/_latest/_chunks without its own locking, relying on the main loop being
    // single-threaded.
    private void RenderDownloadsLocked()
    {
        var lines = new List<string>();
        foreach (var id in _order)
        {
            lines.Add(FormatLine(id, _latest[id]));
            foreach (var chunk in _chunks[id])
                lines.Add(FormatChunkLine(chunk));
        }

        _downloadsView.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatLine(string id, DownloadProgress? progress)
    {
        if (progress is null)
            return $"[{id}] pending...";

        var speed = progress.SpeedBytesPerSecond is { } s ? $"{s:N0} B/s" : "-";
        var total = progress.TotalBytes > 0 ? $"{progress.TotalBytes:N0}" : "???";
        return $"[{id}] {progress.State,-10} {progress.BytesDownloaded:N0}/{total} bytes @ {speed}";
    }

    private static string FormatChunkLine(ChunkProgress chunk)
    {
        var percent = chunk.TotalBytes > 0 ? chunk.BytesDownloaded * 100.0 / chunk.TotalBytes : 0.0;
        var speed = chunk.Status == ChunkStatus.Downloading && chunk.SpeedBytesPerSecond is { } s ? $"{s:N0} B/s" : "-";
        var total = chunk.TotalBytes > 0 ? $"{chunk.TotalBytes:N0}" : "???";
        var endByte = chunk.EndByte >= chunk.StartByte ? $"{chunk.EndByte:N0}" : "???";
        return $"    chunk {chunk.Index,-3} {chunk.Status,-11} {chunk.BytesDownloaded:N0}/{total} bytes ({percent:0.0}%) @ {speed} " +
               $"[{chunk.StartByte:N0}-{endByte}]";
    }
}