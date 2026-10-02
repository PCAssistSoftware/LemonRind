Imports System.IO
Imports Microsoft.Data.Sqlite
Imports Microsoft.Extensions.Logging
Imports LemonRind.Configuration

Namespace Data

    ''' <summary>
    ''' Owns the single SQLite file that backs the whole app: chats, memory,
    ''' RAG vectors, scheduler jobs. One file rather than a separate vector
    ''' database, since brute-force cosine similarity over a BLOB column is
    ''' enough at this scale and needs no extra infrastructure.
    ''' </summary>
    Public Class AppDatabase

        Private ReadOnly _connectionString As String
        Private ReadOnly _logger As ILogger(Of AppDatabase)

        Public Sub New(settings As AppSettings, logger As ILogger(Of AppDatabase))
            _logger = logger

            Dim dataFolder = settings.AppData.ResolvedDataFolder()
            Directory.CreateDirectory(dataFolder)

            Dim dbPath = Path.Combine(dataFolder, "lemonrind.db")
            _connectionString = New SqliteConnectionStringBuilder With {
                .DataSource = dbPath
            }.ToString()
        End Sub

        ''' <summary>
        ''' Opens a new connection to the app database. SQLite connections are
        ''' cheap to open/close and not meant to be held open long-term or
        ''' shared across threads, so callers open one per unit of work rather
        ''' than this class handing out one shared connection.
        ''' </summary>
        Public Function OpenConnection() As SqliteConnection
            Dim connection As New SqliteConnection(_connectionString)
            connection.Open()
            Return connection
        End Function

        ''' <summary>
        ''' Creates the database file (if it doesn't exist yet) and ensures the
        ''' minimal schema-version bookkeeping table exists. Called once at
        ''' startup. A real migration system can replace this once there's
        ''' more than one schema version to migrate between - not needed yet.
        ''' </summary>
        Public Sub EnsureCreated()
            Using connection = OpenConnection()
                ' WAL mode, not the default rollback journal - a background
                ' fire-and-forget write (fact extraction, context compaction)
                ' would otherwise lock the whole file and block whatever else
                ' the app is doing at that exact moment. Set once here (it
                ' persists in the DB file itself, not per-connection), not in
                ' OpenConnection - that's called for every single unit of
                ' work across the whole app, re-issuing this pragma there
                ' would just be repeated overhead for a setting that never
                ' changes after the first time. See AutoBackupModule's own
                ' comment for why a backup checkpoints before zipping.
                Using command = connection.CreateCommand()
                    command.CommandText = "PRAGMA journal_mode=WAL;"
                    command.ExecuteNonQuery()
                End Using

                Using command = connection.CreateCommand()
                    command.CommandText = "
                        CREATE TABLE IF NOT EXISTS SchemaVersion (
                            Version INTEGER NOT NULL
                        );"
                    command.ExecuteNonQuery()
                End Using

                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM SchemaVersion;"
                    Dim rowCount = CLng(command.ExecuteScalar())
                    If rowCount = 0 Then
                        Using insertCommand = connection.CreateCommand()
                            insertCommand.CommandText = "INSERT INTO SchemaVersion (Version) VALUES (1);"
                            insertCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using

                ' Chat organization tables - Folders and Tags back the
                ' sidebar's folder/tag UI; Sessions references Folders
                ' directly, SessionTags is the many-to-many join for Tags.
                Using command = connection.CreateCommand()
                    command.CommandText = "
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

                        -- External-content FTS5 index over Messages.Content -
                        -- keeps the searchable text out of the main table,
                        -- synced via the triggers below.
                        CREATE VIRTUAL TABLE IF NOT EXISTS MessagesFts USING fts5(
                            Content,
                            content='Messages',
                            content_rowid='Id'
                        );

                        CREATE TRIGGER IF NOT EXISTS Messages_AfterInsert AFTER INSERT ON Messages BEGIN
                            INSERT INTO MessagesFts(rowid, Content) VALUES (new.Id, new.Content);
                        END;

                        -- Keeps the FTS index in sync when a message is
                        -- deleted - an external-content FTS5 table needs the
                        -- special 'delete' command form, not a plain DELETE,
                        -- to stay consistent with its content table.
                        CREATE TRIGGER IF NOT EXISTS Messages_AfterDelete AFTER DELETE ON Messages BEGIN
                            INSERT INTO MessagesFts(MessagesFts, rowid, Content) VALUES('delete', old.Id, old.Content);
                        END;"
                    command.ExecuteNonQuery()
                End Using

                ' CREATE TABLE IF NOT EXISTS above does not add new columns
                ' to a database file that already has this table from an
                ' earlier run, so new columns are added below via a guarded
                ' ALTER TABLE (checking pragma_table_info first, since
                ' SQLite errors on ALTER TABLE ADD COLUMN if the column
                ' already exists) - there's no real migration system yet,
                ' see EnsureCreated's own summary.
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Sessions') WHERE name = 'LastContextTokens';"
                    Dim columnExists = CLng(command.ExecuteScalar()) > 0
                    If Not columnExists Then
                        Using alterCommand = connection.CreateCommand()
                            alterCommand.CommandText = "ALTER TABLE Sessions ADD COLUMN LastContextTokens INTEGER NULL;"
                            alterCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using

                ' Long-term memory. Pinned facts have Embedding = NULL -
                ' they're always injected, never semantically searched, so
                ' there's nothing to embed. General facts store their
                ' embedding as a raw Single()-as-bytes BLOB (MemoryRepository
                ' handles the conversion) and are found via brute-force
                ' cosine similarity, good to roughly 1000 vectors with zero
                ' extra infrastructure - the same approach Knowledge Bases
                ' below reuse.
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        CREATE TABLE IF NOT EXISTS Memories (
                            Id TEXT PRIMARY KEY,
                            Content TEXT NOT NULL,
                            IsPinned INTEGER NOT NULL,
                            Embedding BLOB NULL,
                            CreatedAt TEXT NOT NULL
                        );"
                    command.ExecuteNonQuery()
                End Using

                ' Knowledge bases use the same brute-force-cosine-similarity-
                ' over-a-SQLite-table shape as Memories above. KnowledgeSources
                ' tracks what's been ingested (for the management screen);
                ' KnowledgeChunks is what's actually searched. A chunk
                ' always belongs to exactly one source, deleted together
                ' with it (see KnowledgeRepository.DeleteSource).
                Using command = connection.CreateCommand()
                    command.CommandText = "
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
                        CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_SourceId ON KnowledgeChunks(SourceId);"
                    command.ExecuteNonQuery()
                End Using

                ' Which Knowledge Base (if any) is attached to a chat - one
                ' at a time by design, persisted per-session so switching
                ' back to a chat later remembers it. Same guarded migration
                ' pattern as above.
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Sessions') WHERE name = 'AttachedKnowledgeBaseId';"
                    Dim columnExists = CLng(command.ExecuteScalar()) > 0
                    If Not columnExists Then
                        Using alterCommand = connection.CreateCommand()
                            alterCommand.CommandText = "ALTER TABLE Sessions ADD COLUMN AttachedKnowledgeBaseId TEXT NULL;"
                            alterCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using

                ' Tool-call names, stored as a comma-separated list -
                ' persisted so a chat's Markdown export can show what tools
                ' ran even after being reloaded from the database, not just
                ' in the live session. Same guarded migration pattern as
                ' above.
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name = 'ToolCalls';"
                    Dim columnExists = CLng(command.ExecuteScalar()) > 0
                    If Not columnExists Then
                        Using alterCommand = connection.CreateCommand()
                            alterCommand.CommandText = "ALTER TABLE Messages ADD COLUMN ToolCalls TEXT NULL;"
                            alterCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using

                ' Collapsible sidebar folders - the synthetic "Unfiled"
                ' bucket has no real Folders row, so it's never collapsible;
                ' only real folders persist this. Same guarded migration
                ' pattern as above.
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Folders') WHERE name = 'IsCollapsed';"
                    Dim columnExists = CLng(command.ExecuteScalar()) > 0
                    If Not columnExists Then
                        Using alterCommand = connection.CreateCommand()
                            alterCommand.CommandText = "ALTER TABLE Folders ADD COLUMN IsCollapsed INTEGER NOT NULL DEFAULT 0;"
                            alterCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using

                ' MCP server configs - each row is one external MCP server
                ' this app can connect to over stdio. Arguments/
                ' EnvironmentVariables are stored as JSON text (a list and a
                ' string-to-string dictionary respectively) rather than
                ' their own child tables, since they're only ever read/
                ' written whole, never queried into. IsEnabled here is
                ' per-server (connect to this one or not), separate from the
                ' whole-module "Mcp" toggle in Modules.Enabled that turns
                ' MCP support on/off entirely.
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        CREATE TABLE IF NOT EXISTS McpServers (
                            Id TEXT PRIMARY KEY,
                            Name TEXT NOT NULL,
                            Command TEXT NOT NULL,
                            ArgumentsJson TEXT NOT NULL,
                            EnvironmentVariablesJson TEXT NOT NULL,
                            IsEnabled INTEGER NOT NULL,
                            CreatedAt TEXT NOT NULL
                        );"
                    command.ExecuteNonQuery()
                End Using

                ' Scheduler - a job stores a free-text prompt, run through
                ' the normal tool-enabled chat pipeline when its cron
                ' schedule fires. Full assistant/tool access rather than a
                ' limited fixed action set, since a real use case needs
                ' multiple tools chained - e.g. search the web, then email
                ' the result. NextRunAt is cron-computed and stored (not
                ' recalculated from scratch each poll) so the poller's own
                ' query can just ask "what's due" directly.
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        CREATE TABLE IF NOT EXISTS ScheduledJobs (
                            Id TEXT PRIMARY KEY,
                            Name TEXT NOT NULL,
                            CronExpression TEXT NOT NULL,
                            Prompt TEXT NOT NULL,
                            IsEnabled INTEGER NOT NULL,
                            LastRunAt TEXT NULL,
                            NextRunAt TEXT NULL,
                            CreatedAt TEXT NOT NULL
                        );"
                    command.ExecuteNonQuery()
                End Using

                ' A job can optionally pin a specific model (Nothing/empty
                ' means "use whatever model is currently selected/default",
                ' not a hard failure), since different scheduled jobs may
                ' want different models - e.g. a fast small model for a
                ' quick summary job vs. a stronger one for research and
                ' drafting. Same guarded migration pattern as above.
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ScheduledJobs') WHERE name = 'ModelId';"
                    Dim columnExists = CLng(command.ExecuteScalar()) > 0
                    If Not columnExists Then
                        Using alterCommand = connection.CreateCommand()
                            alterCommand.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN ModelId TEXT NULL;"
                            alterCommand.ExecuteNonQuery()
                        End Using
                    End If
                End Using
            End Using

            _logger.LogInformation("App database ready at {ConnectionString}", _connectionString)
        End Sub

    End Class

End Namespace
