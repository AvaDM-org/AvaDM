using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AvaDM.UI.Services;

/// <summary>
/// Ensures at most one AvaDM instance runs per user, and lets a second launch hand off to the
/// first instance instead of silently doing nothing: a losing process signals the winner over a
/// named pipe and exits, and the winner brings its window to front on receiving that signal.
///
/// Exclusivity is an OS-level exclusive file lock (<see cref="FileShare.None"/>) on a fixed file
/// under the same <c>LocalApplicationData/AvaDM</c> directory the download index and logs already
/// live in - not a named <see cref="Mutex"/>, which was tried first and doesn't work here: .NET's
/// named Mutex/Semaphore on Linux is backed by a file under
/// <c>$TMPDIR/.dotnet/shm/session&lt;id&gt;/</c>, where "session" is a per-process value, not a
/// real shared namespace two independently-launched processes reliably land in - in testing here,
/// two back-to-back launches of the same build both got <c>createdNew: true</c> for the identical
/// mutex name. A plain exclusive lock on a real file has none of that: any process opening the
/// same path competes for the same inode regardless of how it was launched, and the OS releases
/// the lock automatically even if the holding process is killed - no abandoned-mutex handling
/// needed either.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private static readonly string PipeName = $"AvaDM-Activate-{Environment.UserName}";
    private static readonly string LockFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaDM", "avadm.lock");

    /// <summary>Set by <see cref="TryAcquire"/> only for the winning instance, so
    /// <c>App.axaml.cs</c> can reach it without threading it through Avalonia's parameterless
    /// <c>AppBuilder.Configure&lt;App&gt;()</c> construction.</summary>
    public static SingleInstanceService? Instance { get; private set; }

    private readonly FileStream _lockFile;
    private readonly CancellationTokenSource _listenerCts = new();
    private readonly Task _listenerTask;

    /// <summary>Activation requests that arrived before <see cref="SetActivationHandler"/> was
    /// called - i.e. before the main window exists - so a launch that lands in that startup
    /// window (cold JIT, slow disk, the four synchronous preference reads in
    /// <c>App.LoadStoredPreferences</c>) still gets honored instead of silently dropped. There's
    /// no payload, just a count of pending "please activate" requests to replay.</summary>
    private readonly ConcurrentQueue<byte> _pendingActivations = new();

    private readonly object _handlerLock = new();
    private Action? _activationHandler;

    private SingleInstanceService(FileStream lockFile)
    {
        _lockFile = lockFile;
        _listenerTask = ListenLoopAsync(_listenerCts.Token);
    }

    /// <summary>Call once, as early as possible in <c>Main</c> - before Avalonia or logging, so a
    /// losing launch can exit immediately, and (for the winner) so the pipe listener is live from
    /// the very start rather than only once the window is ready to be shown; see
    /// <see cref="SetActivationHandler"/> for how a request that arrives before then is handled.
    /// Returns the service to keep alive for the process lifetime if this is the first instance;
    /// returns <c>null</c> if another instance already holds the lock, after best-effort signalling
    /// it to come to the front.</summary>
    public static SingleInstanceService? TryAcquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockFilePath)!);

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            Log.Information(e, "SingleInstanceService: lock file {LockFilePath} already held", LockFilePath);
            SignalExistingInstance();
            return null;
        }

        Log.Information("SingleInstanceService: acquired lock file {LockFilePath}", LockFilePath);
        Instance = new SingleInstanceService(lockFile);
        return Instance;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 2000);
            Log.Information("SingleInstanceService: signalled running instance via pipe {PipeName}", PipeName);
        }
        catch (Exception e)
        {
            // Best effort: if the running instance's listener isn't up yet or anything else goes
            // wrong, there's nothing more a losing second launch can do beyond exiting anyway.
            Log.Warning(e, "SingleInstanceService: failed to signal running instance via pipe {PipeName}", PipeName);
        }
    }

    /// <summary>Called once the main window exists and is ready to be shown; replays any
    /// activation request that arrived while the listener was up but the UI wasn't
    /// (see <see cref="_pendingActivations"/>), then dispatches every later one straight through.
    /// <paramref name="handler"/> runs on whatever thread received the pipe connection, not the UI
    /// thread - callers touching Avalonia objects must dispatch themselves.</summary>
    public void SetActivationHandler(Action handler)
    {
        lock (_handlerLock)
        {
            _activationHandler = handler;
            while (_pendingActivations.TryDequeue(out _))
                handler();
        }
    }

    private void RaiseActivation()
    {
        lock (_handlerLock)
        {
            if (_activationHandler is { } handler)
                handler();
            else
                _pendingActivations.Enqueue(0);
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        Log.Information("SingleInstanceService: listening for activation requests on pipe {PipeName}", PipeName);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                Log.Information("SingleInstanceService: activation request received on pipe {PipeName}", PipeName);
                RaiseActivation();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // Transient pipe error - back off briefly so a persistent failure (e.g. the pipe
                // name is somehow stuck busy) can't spin this loop at 100% CPU, then recreate the
                // server rather than leaving this instance permanently unreachable.
                Log.Warning(e, "SingleInstanceService: pipe listener error on {PipeName}, retrying", PipeName);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Stops the listener and releases the lock file. Waits briefly for the listener
    /// task to actually finish (not just for cancellation to be requested) before releasing the
    /// lock, so a new instance launched right at shutdown can't win the lock and then fail to
    /// claim the pipe name because this instance's server is still mid-teardown.</summary>
    public void Dispose()
    {
        _listenerCts.Cancel();
        try
        {
            _listenerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort wait only - the lock is released either way below.
        }

        _lockFile.Dispose();
    }
}
