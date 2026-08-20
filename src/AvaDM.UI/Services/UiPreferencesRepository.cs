using Dapper;
using Microsoft.Data.Sqlite;

namespace AvaDM.UI.Services;

/// <summary>
/// Dapper + <c>Microsoft.Data.Sqlite</c> key/value store for UI-only preferences (currently just
/// the theme-variant choice), persisted in the same SQLite file as <c>AvaDM.Core</c>'s download
/// index (<c>DownloadSettings.GetResolvedRepositoryPath()</c>). Mirrors
/// <see cref="AvaDM.Core.DownloadRepository"/>'s connection/initialization pattern exactly so the
/// two repositories behave consistently against the same file.
/// </summary>
public sealed class UiPreferencesRepository(string dbPath)
{
    public const string ThemeVariantKey = "ThemeVariant";
    public const string CloseToTrayKey = "CloseToTray";
    public const string DoubleClickActionKey = "DoubleClickAction";

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

            CREATE TABLE IF NOT EXISTS UiPreferences (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """,
            cancellationToken: ct);
        await connection.ExecuteAsync(command);
    }

    public async Task<string?> GetValueAsync(string key)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT Value FROM UiPreferences WHERE Key = @key",
            new { key });
    }

    public async Task SetValueAsync(string key, string value)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO UiPreferences (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = @value
            """,
            new { key, value });
    }
}
