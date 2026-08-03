using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteDatabase(string databasePath)
{
    public string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = false,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                is_default INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_used_at TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_profiles_default
                ON profiles(is_default) WHERE is_default = 1;

            CREATE TABLE IF NOT EXISTS providers (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                enabled INTEGER NOT NULL,
                credential_reference TEXT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS task_groups (
                id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS usage_snapshots (
                id TEXT PRIMARY KEY,
                task_group_id TEXT NOT NULL,
                worker_job_id TEXT NULL,
                captured_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(task_group_id) REFERENCES task_groups(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS controlled_tasks (
                id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
