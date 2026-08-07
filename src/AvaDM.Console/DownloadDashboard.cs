using System.Collections.ObjectModel;
using AvaDM.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AvaDM.Console;

/// <summary>
/// Terminal.Gui dashboard: one row per tracked download (each its own file, each with its own
/// parallel chunks under the hood - the row just shows the aggregate progress Core reports), a
/// scrolling log pane below it, and a command input line pinned to the bottom.
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
    private readonly ObservableCollection<string> _rows = [];

    private readonly IApplication _app;
    private readonly ListView _downloadsList;
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

        _downloadsList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(40),
        };
        _downloadsList.SetSource(_rows);

        _log = new TextView
        {
            X = 0,
            Y = Pos.Bottom(_downloadsList),
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
        _window.Add(_downloadsList, _log, prompt, _commandField);
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
            _rows.Add(FormatLine(id, null)); // ObservableCollection.Add notifies the ListView directly
        });
    }

    public void UpdateProgress(string id, DownloadProgress progress)
    {
        _app.Invoke(() =>
        {
            _latest[id] = progress;
            var index = _order.IndexOf(id);
            if (index >= 0)
                _rows[index] = FormatLine(id, progress);
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

    private static string FormatLine(string id, DownloadProgress? progress)
    {
        if (progress is null)
            return $"[{id}] pending...";

        var speed = progress.SpeedBytesPerSecond is { } s ? $"{s:N0} B/s" : "-";
        return $"[{id}] {progress.State,-10} {progress.BytesDownloaded:N0}/{progress.TotalBytes:N0} bytes @ {speed}";
    }
}