using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Data;

/// <summary>
/// Owns the single SQLite file that backs the whole app: chats, memory, RAG
/// vectors, scheduler jobs. One file rather than a separate vector
/// database, since brute-force cosine similarity over a BLOB column is
/// enough at this scale and needs no extra infrastructure.
///
/// Ported from the VB.NET/WPF LemonRind app's Data\AppDatabase.vb, brought
/// over with its FULL current schema (every stage's tables/migrations, not
/// just the Stage-1 SchemaVersion-only version it started as) - same "small,
/// self-contained, non-UI file, no benefit to slicing it per stage"
/// reasoning as Configuration\AppSettings.cs. The tables for features not
/// ported yet (Memories, KnowledgeBases, McpServers, ScheduledJobs, ...)
/// simply sit unused until their own stage's C# repository code exists.
/// </summary>
public class AppDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<AppDatabase> _logger;

    public AppDatabase(AppSettings settings, ILogger<AppDatabase> logger)
    {
        _logger = logger;

        var dataFolder = settings.AppData.ResolvedDataFolder();
        Directory.CreateDirectory(dataFolder);

        var dbPath = Path.Combine(dataFolder, "lemonrind.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>
    /// Opens a new connection to the app database. SQLite connections are
    /// cheap to open/close and not meant to be held open long-term or
    /// shared across threads, so callers open one per unit of work rather
    /// than this class handing out one shared connection.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates the database file (if it doesn't exist yet) and ensures the
    /// schema exists. Called once at startup. A real migration system can
    /// replace the guarded ALTER TABLE calls below once there's a real need
    /// - deliberately not built yet, to keep this small personal app simple.
    /// </summary>
    public void EnsureCreated()
    {
        using var connection = OpenConnection();

        // WAL mode, not the default rollback journal - a background
        // fire-and-forget write would otherwise lock the whole file and
        // block whatever else the app is doing. Set once here (persists in
        // the DB file itself), not in OpenConnection.
        Exec(connection, "PRAGMA journal_mode=WAL;");

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER NOT NULL
            );
            """);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
            var rowCount = (long)command.ExecuteScalar()!;
            if (rowCount == 0)
            {
                Exec(connection, "INSERT INTO SchemaVersion (Version) VALUES (1);");
            }
        }

        // Chat organization tables - Folders/Tags back the sidebar's
        // folder/tag UI; Sessions references Folders directly, SessionTags
        // is the many-to-many join for Tags.
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS Folders (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                FolderId TEXT NULL REFERENCES Folders(Id),
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Tags (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS SessionTags (
                SessionId TEXT NOT NULL REFERENCES Sessions(Id),
                TagId TEXT NOT NULL REFERENCES Tags(Id),
                PRIMARY KEY (SessionId, TagId)
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL REFERENCES Sessions(Id),
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                ReasoningContent TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_SessionId ON Messages(SessionId);

            -- External-content FTS5 index over Messages.Content - keeps the
            -- searchable text out of the main table, synced via triggers.
            CREATE VIRTUAL TABLE IF NOT EXISTS MessagesFts USING fts5(
                Content,
                content='Messages',
                content_rowid='Id'
            );

            CREATE TRIGGER IF NOT EXISTS Messages_AfterInsert AFTER INSERT ON Messages BEGIN
                INSERT INTO MessagesFts(rowid, Content) VALUES (new.Id, new.Content);
            END;

            CREATE TRIGGER IF NOT EXISTS Messages_AfterDelete AFTER DELETE ON Messages BEGIN
                INSERT INTO MessagesFts(MessagesFts, rowid, Content) VALUES('delete', old.Id, old.Content);
            END;
            """);

        AddColumnIfMissing(connection, "Sessions", "LastContextTokens", "INTEGER NULL");

        // Long-term memory. Pinned facts have Embedding = NULL - they're
        // always injected, never semantically searched. General facts store
        // their embedding as a raw float[]-as-bytes BLOB and are found via
        // brute-force cosine similarity - the same approach Knowledge Bases
        // below reuse.
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS Memories (
                Id TEXT PRIMARY KEY,
                Content TEXT NOT NULL,
                IsPinned INTEGER NOT NULL,
                Embedding BLOB NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS KnowledgeBases (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KnowledgeSources (
                Id TEXT PRIMARY KEY,
                KnowledgeBaseId TEXT NOT NULL REFERENCES KnowledgeBases(Id),
                SourceType TEXT NOT NULL,
                Reference TEXT NULL,
                DisplayName TEXT NOT NULL,
                Status TEXT NOT NULL,
                ErrorMessage TEXT NULL,
                ChunkCount INTEGER NOT NULL,
                AddedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_KnowledgeSources_KnowledgeBaseId ON KnowledgeSources(KnowledgeBaseId);

            CREATE TABLE IF NOT EXISTS KnowledgeChunks (
                Id TEXT PRIMARY KEY,
                KnowledgeBaseId TEXT NOT NULL REFERENCES KnowledgeBases(Id),
                SourceId TEXT NOT NULL REFERENCES KnowledgeSources(Id),
                ChunkIndex INTEGER NOT NULL,
                Content TEXT NOT NULL,
                Embedding BLOB NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_KnowledgeBaseId ON KnowledgeChunks(KnowledgeBaseId);
            CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_SourceId ON KnowledgeChunks(SourceId);
            """);

        AddColumnIfMissing(connection, "Sessions", "AttachedKnowledgeBaseId", "TEXT NULL");
        AddColumnIfMissing(connection, "Messages", "ToolCalls", "TEXT NULL");
        AddColumnIfMissing(connection, "Folders", "IsCollapsed", "INTEGER NOT NULL DEFAULT 0");

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS McpServers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Command TEXT NOT NULL,
                ArgumentsJson TEXT NOT NULL,
                EnvironmentVariablesJson TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS ScheduledJobs (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                CronExpression TEXT NOT NULL,
                Prompt TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                LastRunAt TEXT NULL,
                NextRunAt TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        AddColumnIfMissing(connection, "ScheduledJobs", "ModelId", "TEXT NULL");

        _logger.LogInformation("App database ready at {ConnectionString}", _connectionString);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS above does not add new columns to a
    /// database file that already has the table from an earlier run, so new
    /// columns go through this guarded ALTER TABLE instead (SQLite errors on
    /// ALTER TABLE ADD COLUMN if the column already exists).
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        var columnExists = (long)checkCommand.ExecuteScalar()! > 0;
        if (!columnExists)
        {
            Exec(connection, $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition};");
        }
    }
}
