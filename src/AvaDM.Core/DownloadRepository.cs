using Dapper;
using Microsoft.Data.Sqlite;

namespace AvaDM.Core;

/// <summary>Simple/aggregate metadata for one download, as persisted in the SQLite index. Never
/// carries per-chunk progress - that lives only in the <c>.avadm</c> footer while a download is
/// incomplete (see <see cref="DownloadFooter"/>).</summary>
public sealed record DownloadRecord(
    Guid Id,
    string Uri,
    string DestinationPath,
    DownloadState State,
    long TotalBytes,
    long BytesDownloaded,
    DateTime CreatedAt,
    DateTime? LastModifiedAt);

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

    public async Task<DownloadRecord> InsertAsync(Guid id, string uri, string destinationPath, DownloadState state, long totalBytes)
    {
        var createdAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO Downloads (Id, Uri, DestinationPath, State, TotalBytes, BytesDownloaded, CreatedAt, LastModifiedAt)
            VALUES (@Id, @Uri, @DestinationPath, @State, @TotalBytes, 0, @CreatedAt, NULL)
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

        return new DownloadRecord(id, uri, destinationPath, state, totalBytes, 0, createdAt, null);
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

        public DownloadRecord ToRecord() => new(
            Guid.Parse(Id),
            Uri,
            DestinationPath,
            (DownloadState)State,
            TotalBytes,
            BytesDownloaded,
            DateTime.Parse(CreatedAt).ToUniversalTime(),
            LastModifiedAt is null ? null : DateTime.Parse(LastModifiedAt).ToUniversalTime());
    }
}
