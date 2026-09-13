using Dapper;
using Microsoft.Data.Sqlite;

namespace AvaDM.Core;

/// <summary>Simple/aggregate metadata for one download, as persisted in the SQLite index. Never
/// carries per-chunk progress - that lives only in the <c>.avadm</c> footer while a download is
/// incomplete (see <see cref="DownloadFooter"/>).</summary>
/// <summary><paramref name="QueueOrder"/> is the download's position in the queue: assigned once,
/// when the row is first inserted, as one past the current maximum - and left untouched
/// afterward except by an explicit user reorder (<see cref="DownloadRepository.UpdateQueueOrderAsync"/>).
/// It is not recomputed on resume/restart, so a download's place in the queue survives an app
/// restart exactly as it was.</summary>
public sealed record DownloadRecord(
    Guid Id,
    string Uri,
    string DestinationPath,
    DownloadState State,
    long TotalBytes,
    long BytesDownloaded,
    DateTime CreatedAt,
    DateTime? LastModifiedAt,
    int QueueOrder,
    DateTime? ScheduledStartAtUtc);

public sealed record ConflictCheckResult(bool HasConflict, DownloadRecord? ExistingRecord);

/// <summary>
/// Dapper + <c>Microsoft.Data.Sqlite</c> data access for the download index: one row per known
/// download, keyed on the compound (Uri, DestinationPath) identity used for dedupe-on-add. Not
/// EF Core - the schema is tiny and hand-written SQL keeps the dependency footprint small.
/// Callers are expected to consistently pass <c>uri.AbsoluteUri</c> and
/// <c>Path.GetFullPath(destinationPath)</c> for the identity columns; this type does no
/// normalization itself.
/// </summary>
public sealed class DownloadRepository(string dbPath)
{
    private string ConnectionString { get; } = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = new CommandDefinition(
            """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS Downloads (
                Id TEXT PRIMARY KEY,
                Uri TEXT NOT NULL,
                DestinationPath TEXT NOT NULL,
                State INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                BytesDownloaded INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                LastModifiedAt TEXT,
                UNIQUE(Uri, DestinationPath)
            );
            """,
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
        await MigrateQueueOrderColumnAsync(connection, ct);
        await MigrateScheduledStartAtUtcColumnAsync(connection, ct);
    }

    /// <summary>Adds the <c>QueueOrder</c> column to a database created before the download queue
    /// feature existed. Existing rows are backfilled in <c>CreatedAt</c> order so pre-existing
    /// downloads get a stable, sensible starting position rather than all defaulting to 0.</summary>
    private static async Task MigrateQueueOrderColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await connection.QueryAsync<string>(
            new CommandDefinition("SELECT name FROM pragma_table_info('Downloads')", cancellationToken: ct));
        if (columns.Contains("QueueOrder"))
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            ALTER TABLE Downloads ADD COLUMN QueueOrder INTEGER NOT NULL DEFAULT 0;

            UPDATE Downloads SET QueueOrder = (
                SELECT COUNT(*) FROM Downloads AS earlier WHERE earlier.CreatedAt <= Downloads.CreatedAt
            );
            """,
            cancellationToken: ct));
    }

    /// <summary>Adds the <c>ScheduledStartAtUtc</c> column to a database created before the
    /// scheduled-downloads feature existed, following <see cref="MigrateQueueOrderColumnAsync"/>'s
    /// exact pattern. Nullable and left <c>NULL</c> for every existing row - only a
    /// <see cref="DownloadState.Scheduled"/> row ever has a value.</summary>
    private static async Task MigrateScheduledStartAtUtcColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await connection.QueryAsync<string>(
            new CommandDefinition("SELECT name FROM pragma_table_info('Downloads')", cancellationToken: ct));
        if (columns.Contains("ScheduledStartAtUtc"))
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE Downloads ADD COLUMN ScheduledStartAtUtc TEXT",
            cancellationToken: ct));
    }

    public async Task<ConflictCheckResult> CheckConflictAsync(string uri, string destinationPath)
    {
        using var connection = OpenConnection();
        var existing = await connection.QuerySingleOrDefaultAsync<DownloadRow>(
            "SELECT * FROM Downloads WHERE Uri = @uri AND DestinationPath = @destinationPath",
            new { uri, destinationPath });

        return existing is null
            ? new ConflictCheckResult(false, null)
            : new ConflictCheckResult(true, existing.ToRecord());
    }

    /// <summary>Inserts a new row, assigning it the next <c>QueueOrder</c> (one past the current
    /// maximum) in the same statement as the insert itself - so two inserts on the same
    /// connection can never race each other into computing the same "next" value.</summary>
    public async Task<DownloadRecord> InsertAsync(Guid id, string uri, string destinationPath, DownloadState state, long totalBytes)
    {
        var createdAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        var queueOrder = await connection.QuerySingleAsync<int>(
            """
            INSERT INTO Downloads (Id, Uri, DestinationPath, State, TotalBytes, BytesDownloaded, CreatedAt, LastModifiedAt, QueueOrder)
            VALUES (@Id, @Uri, @DestinationPath, @State, @TotalBytes, 0, @CreatedAt, NULL,
                (SELECT COALESCE(MAX(QueueOrder), 0) + 1 FROM Downloads))
            RETURNING QueueOrder
            """,
            new
            {
                Id = id.ToString(),
                Uri = uri,
                DestinationPath = destinationPath,
                State = (int)state,
                TotalBytes = totalBytes,
                CreatedAt = createdAt.ToString("O"),
            });

        return new DownloadRecord(id, uri, destinationPath, state, totalBytes, 0, createdAt, null, queueOrder, null);
    }

    /// <summary>Inserts a new <see cref="DownloadState.Scheduled"/> row. Unlike
    /// <see cref="InsertAsync"/>, <c>QueueOrder</c> is left at its column default (0) rather than
    /// assigned the next position - a scheduled item shouldn't occupy or compete for a queue slot
    /// until it's actually due (see <see cref="PromoteScheduledToQueuedAsync"/>, which assigns the
    /// real position at that point instead).</summary>
    public async Task<DownloadRecord> InsertScheduledAsync(Guid id, string uri, string destinationPath, DateTime scheduledStartAtUtc)
    {
        var createdAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO Downloads (Id, Uri, DestinationPath, State, TotalBytes, BytesDownloaded, CreatedAt, LastModifiedAt, QueueOrder, ScheduledStartAtUtc)
            VALUES (@Id, @Uri, @DestinationPath, @State, 0, 0, @CreatedAt, NULL, 0, @ScheduledStartAtUtc)
            """,
            new
            {
                Id = id.ToString(),
                Uri = uri,
                DestinationPath = destinationPath,
                State = (int)DownloadState.Scheduled,
                CreatedAt = createdAt.ToString("O"),
                ScheduledStartAtUtc = scheduledStartAtUtc.ToString("O"),
            });

        return new DownloadRecord(id, uri, destinationPath, DownloadState.Scheduled, 0, 0, createdAt, null, 0, scheduledStartAtUtc);
    }

    /// <summary>The due-time transition for a <see cref="DownloadState.Scheduled"/> row: flips it
    /// to <see cref="DownloadState.Queued"/> and, in the same statement, assigns it a fresh
    /// <c>QueueOrder</c> (one past the current maximum, same subquery <see cref="InsertAsync"/>
    /// uses) - so a schedule created long ago doesn't jump the line just because it was left at
    /// <c>QueueOrder</c> 0 the whole time it waited; it joins the back of the queue at the moment
    /// it actually becomes due, exactly like a brand-new add would. <c>ScheduledStartAtUtc</c> is
    /// left in place (harmless once the row is no longer <see cref="DownloadState.Scheduled"/>,
    /// and lets a caller still see when it was originally due).</summary>
    public async Task PromoteScheduledToQueuedAsync(Guid id)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            """
            UPDATE Downloads SET State = @State, QueueOrder = (SELECT COALESCE(MAX(QueueOrder), 0) + 1 FROM Downloads), LastModifiedAt = @LastModifiedAt
            WHERE Id = @Id
            """,
            new
            {
                Id = id.ToString(),
                State = (int)DownloadState.Queued,
                LastModifiedAt = DateTime.UtcNow.ToString("O"),
            });
    }

    /// <summary>Re-arms an existing row as a fresh <see cref="DownloadState.Scheduled"/> row -
    /// the schedule counterpart to <see cref="ResetForRestartAsync"/>, used when
    /// <see cref="DownloadManager.ScheduleDownloadAsync"/> resolves a conflict via Resume or
    /// Overwrite against an existing (Uri, DestinationPath) row. Same <paramref name="id"/> and
    /// <c>CreatedAt</c> as before; size/progress reset to 0 and <c>QueueOrder</c> left untouched
    /// (unused while <see cref="DownloadState.Scheduled"/>, and reassigned by
    /// <see cref="PromoteScheduledToQueuedAsync"/> once due regardless of its current value).</summary>
    public async Task<DownloadRecord> ResetForScheduleAsync(Guid id, DateTime scheduledStartAtUtc)
    {
        using var connection = OpenConnection();
        var lastModifiedAt = DateTime.UtcNow;
        await connection.ExecuteAsync(
            "UPDATE Downloads SET State = @State, TotalBytes = 0, BytesDownloaded = 0, LastModifiedAt = @LastModifiedAt, ScheduledStartAtUtc = @ScheduledStartAtUtc WHERE Id = @Id",
            new
            {
                Id = id.ToString(),
                State = (int)DownloadState.Scheduled,
                LastModifiedAt = lastModifiedAt.ToString("O"),
                ScheduledStartAtUtc = scheduledStartAtUtc.ToString("O"),
            });

        var row = await GetByIdAsync(id)
            ?? throw new InvalidOperationException($"ResetForScheduleAsync: no row found for id {id} - caller must pass the id of an existing record.");
        return row;
    }

    /// <summary>Re-arms an existing row for a resume/overwrite restart: same <paramref name="id"/>,
    /// <c>CreatedAt</c>, and <c>QueueOrder</c> as before (the row's identity, history, and queue
    /// position don't change just because the process restarted), but state/size/progress reset
    /// to reflect the fresh attempt. Used instead of <see cref="InsertAsync"/> when
    /// <see cref="DownloadManager.AddDownloadAsync"/> resolves a conflict via Resume or Overwrite,
    /// since the conflicting row (same Uri + DestinationPath) is still present and a second
    /// INSERT would trip the UNIQUE constraint - and would also hand back a new Id, orphaning any
    /// UI row already keyed on the old one. Also clears <c>ScheduledStartAtUtc</c> back to
    /// <c>NULL</c>: a row reaching this method is moving to <see cref="DownloadState.Running"/> or
    /// <see cref="DownloadState.Queued"/>, never back to <see cref="DownloadState.Scheduled"/>, so
    /// a stale schedule timestamp left over from a previous <see cref="DownloadState.Scheduled"/>
    /// row at this same identity would be misleading if ever surfaced.</summary>
    public async Task<DownloadRecord> ResetForRestartAsync(Guid id, DownloadState state, long totalBytes)
    {
        using var connection = OpenConnection();
        var lastModifiedAt = DateTime.UtcNow;
        await connection.ExecuteAsync(
            "UPDATE Downloads SET State = @State, TotalBytes = @TotalBytes, BytesDownloaded = 0, LastModifiedAt = @LastModifiedAt, ScheduledStartAtUtc = NULL WHERE Id = @Id",
            new
            {
                Id = id.ToString(),
                State = (int)state,
                TotalBytes = totalBytes,
                LastModifiedAt = lastModifiedAt.ToString("O"),
            });

        var row = await GetByIdAsync(id)
            ?? throw new InvalidOperationException($"ResetForRestartAsync: no row found for id {id} - caller must pass the id of an existing record.");
        return row;
    }

    /// <summary>Best-effort progress checkpoint. Callers driving this from a fire-and-forget
    /// event handler are expected to catch/log any exception themselves - this method lets
    /// failures propagate rather than swallowing them, so a caller that does want to observe
    /// them still can. <paramref name="totalBytes"/> is included because it's 0/unknown at
    /// <see cref="InsertAsync"/> time (the HEAD request hasn't completed yet when the row is
    /// first written) and only becomes accurate once the first progress report arrives.</summary>
    public async Task UpdateProgressAsync(Guid id, DownloadState state, long bytesDownloaded, long totalBytes)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            "UPDATE Downloads SET State = @State, BytesDownloaded = @BytesDownloaded, TotalBytes = @TotalBytes, LastModifiedAt = @LastModifiedAt WHERE Id = @Id",
            new
            {
                Id = id.ToString(),
                State = (int)state,
                BytesDownloaded = bytesDownloaded,
                TotalBytes = totalBytes,
                LastModifiedAt = DateTime.UtcNow.ToString("O"),
            });
    }

    /// <summary>Updates only <c>State</c> - unlike <see cref="ResetForRestartAsync"/>, leaves
    /// <c>TotalBytes</c>/<c>BytesDownloaded</c>/<c>QueueOrder</c> untouched. Used to move a row
    /// between <see cref="DownloadState.Queued"/> and back without disturbing progress already
    /// recorded for it (e.g. re-queuing an interrupted download that was previously
    /// <see cref="DownloadState.Running"/>, which should resume from its <c>.avadm</c> footer
    /// rather than from 0) or its place in line.</summary>
    public async Task UpdateStateAsync(Guid id, DownloadState state)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            "UPDATE Downloads SET State = @State, LastModifiedAt = @LastModifiedAt WHERE Id = @Id",
            new
            {
                Id = id.ToString(),
                State = (int)state,
                LastModifiedAt = DateTime.UtcNow.ToString("O"),
            });
    }

    /// <summary>Explicit user reorder - the only thing, besides <see cref="InsertAsync"/> assigning
    /// the initial value, that ever changes a row's <c>QueueOrder</c>.</summary>
    public async Task UpdateQueueOrderAsync(Guid id, int queueOrder)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            "UPDATE Downloads SET QueueOrder = @QueueOrder WHERE Id = @Id",
            new { Id = id.ToString(), QueueOrder = queueOrder });
    }

    public async Task<IReadOnlyList<DownloadRecord>> GetAllAsync()
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<DownloadRow>("SELECT * FROM Downloads ORDER BY CreatedAt");
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<DownloadRecord?> GetByIdAsync(Guid id)
    {
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<DownloadRow>(
            "SELECT * FROM Downloads WHERE Id = @id",
            new { id = id.ToString() });
        return row?.ToRecord();
    }

    public async Task DeleteAsync(Guid id)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync("DELETE FROM Downloads WHERE Id = @id", new { id = id.ToString() });
    }

    // Dapper-friendly flat shape matching the table's column types exactly (Id/State as their
    // SQLite storage types, dates as ISO-8601 text); mapped to the public DownloadRecord after
    // the query so callers never see the storage representation.
    private sealed class DownloadRow
    {
        public string Id { get; init; } = "";
        public string Uri { get; init; } = "";
        public string DestinationPath { get; init; } = "";
        public int State { get; init; }
        public long TotalBytes { get; init; }
        public long BytesDownloaded { get; init; }
        public string CreatedAt { get; init; } = "";
        public string? LastModifiedAt { get; init; }
        public int QueueOrder { get; init; }
        public string? ScheduledStartAtUtc { get; init; }

        public DownloadRecord ToRecord() => new(
            Guid.Parse(Id),
            Uri,
            DestinationPath,
            (DownloadState)State,
            TotalBytes,
            BytesDownloaded,
            DateTime.Parse(CreatedAt).ToUniversalTime(),
            LastModifiedAt is null ? null : DateTime.Parse(LastModifiedAt).ToUniversalTime(),
            QueueOrder,
            ScheduledStartAtUtc is null ? null : DateTime.Parse(ScheduledStartAtUtc).ToUniversalTime());
    }
}
