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

            CREATE TABLE IF NOT EXISTS profile_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                has_been_initialized INTEGER NOT NULL
            );

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

            CREATE TABLE IF NOT EXISTS scheduler_tasks (
                id TEXT PRIMARY KEY,
                state INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_scheduler_tasks_state_updated
                ON scheduler_tasks(state, updated_at DESC);

            CREATE TABLE IF NOT EXISTS scheduler_repartitions (
                task_group_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                recorded_at TEXT NOT NULL,
                trigger INTEGER NOT NULL,
                decision INTEGER NOT NULL,
                reason INTEGER NOT NULL,
                work_summary TEXT NOT NULL,
                worker_identity TEXT NULL,
                result TEXT NULL,
                package_id TEXT NULL,
                working_directory TEXT NULL,
                package_kind TEXT NULL,
                declared_scopes_json TEXT NULL,
                cost_window_index INTEGER NULL,
                PRIMARY KEY(task_group_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_scheduler_repartitions_group_sequence
                ON scheduler_repartitions(task_group_id, sequence ASC);

            CREATE TABLE IF NOT EXISTS work_package_leases (
                lease_id TEXT PRIMARY KEY,
                package_id TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                status INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_work_package_leases_active
                ON work_package_leases(package_id, working_directory, status, created_at DESC);

            CREATE TABLE IF NOT EXISTS agent_projects (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                is_archived INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_projects_name
                ON agent_projects(name COLLATE NOCASE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Keep upgrades idempotent for databases created before lease metadata
        // was introduced. SQLite has no portable ADD COLUMN IF NOT EXISTS.
        foreach (var (name, definition) in new[]
        {
            ("package_id", "TEXT NULL"),
            ("working_directory", "TEXT NULL"),
            ("package_kind", "TEXT NULL"),
            ("declared_scopes_json", "TEXT NULL"),
            ("cost_window_index", "INTEGER NULL"),
        })
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = $"ALTER TABLE scheduler_repartitions ADD COLUMN {name} {definition}";
            try
            {
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Already present; this is the expected second initialization.
            }
        }
    }
}
