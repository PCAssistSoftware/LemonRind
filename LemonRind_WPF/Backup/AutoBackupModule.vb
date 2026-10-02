Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports Microsoft.Extensions.AI
Imports Microsoft.Extensions.Logging
Imports LemonRind.Configuration
Imports LemonRind.Data
Imports LemonRind.Modules

Namespace Backup

    ''' <summary>
    ''' Periodically zips the whole portable data folder (SQLite DB,
    ''' generated/attached images, workspace files, and appsettings.json -
    ''' all of it lives under the one data folder, so a plain recursive zip
    ''' of dataDir is a complete disaster-recovery safety net with nothing
    ''' extra to special-case). Zero AI tools (GetTools returns empty) - this
    ''' is pure background infrastructure, not an assistant capability, but
    ''' still implements IAssistantModule to get the same free enable/disable
    ''' + live-reload plumbing (ModuleRegistry.ReconcileEnabledModulesAsync,
    ''' a real row in Settings' Modules list) every other module already has,
    ''' rather than inventing a second, parallel on/off mechanism just for
    ''' this one feature.
    ''' </summary>
    Public Class AutoBackupModule
        Implements IAssistantModule

        Private Const MarkerFileName = "last_backup.txt"
        Private Const ZipPrefix = "lemonrind_backup_"

        ' A backup is day-granularity by design (IntervalDays), so checking
        ' every few hours is generous - no need for Scheduler's tighter
        ' 30-second poll, which exists to make minute-level cron jobs feel
        ' responsive. This also re-reads IntervalDays/BackupFolder/KeepCount
        ' fresh from settings on every tick (never cached), so a changed
        ' Settings value takes effect on the very next tick with no restart
        ' needed - same live-reload principle as everything else in this app.
        Private Const PollIntervalHours = 6

        Private ReadOnly _appDataSettings As AppDataSettings
        Private ReadOnly _backupSettings As BackupSettings
        Private ReadOnly _moduleSettings As ModuleSettings
        Private ReadOnly _database As AppDatabase
        Private ReadOnly _logger As ILogger(Of AutoBackupModule)

        Private _pollTimer As Timer
        ' Same re-entrancy guard shape as SchedulerModule._isPolling - a
        ' large data folder could take a while to zip, and a plain Timer
        ' doesn't wait for its own callback to finish before scheduling the
        ' next tick.
        Private _isRunning As Boolean = False

        Public Sub New(settings As AppSettings, database As AppDatabase, logger As ILogger(Of AutoBackupModule))
            _appDataSettings = settings.AppData
            _backupSettings = settings.Backup
            _moduleSettings = settings.Modules
            _database = database
            _logger = logger
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "Auto-backup"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "Backup"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Periodically zips your whole data folder (including all settings) as a safety net. No AI involvement - purely operational."
            End Get
        End Property

        Public ReadOnly Property IsEnabled As Boolean Implements IAssistantModule.IsEnabled
            Get
                Return _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, False)
            End Get
        End Property

        Public Function OnStartupAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnStartupAsync
            _pollTimer = New Timer(AddressOf OnPollTimerTick, Nothing, TimeSpan.FromMinutes(1), TimeSpan.FromHours(PollIntervalHours))
            Return Task.CompletedTask
        End Function

        Public Function OnShutdownAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnShutdownAsync
            _pollTimer?.Dispose()
            Return Task.CompletedTask
        End Function

        ''' <summary>Genuinely empty - see the class summary for why this module still exists as an IAssistantModule despite offering nothing to the model.</summary>
        Public Function GetTools() As IEnumerable(Of AITool) Implements IAssistantModule.GetTools
            Return Array.Empty(Of AITool)()
        End Function

        Private Async Sub OnPollTimerTick(state As Object)
            If _isRunning Then Return
            _isRunning = True
            Try
                Dim dataDir = _appDataSettings.ResolvedDataFolder()
                Dim backupDir = _backupSettings.ResolvedBackupFolder()

                ' Defensive, not just theoretical - BackupFolder is a free-text
                ' Settings field, so a user could point it inside DataFolder by
                ' mistake (or by copy-pasting the wrong path). Backing up a
                ' folder that also contains its own previous backups means
                ' every new zip re-includes every old one, compounding
                ' forever - refuse rather than let that quietly start.
                If PathsOverlap(dataDir, backupDir) Then
                    _logger.LogWarning("Auto-backup folder ({BackupDir}) is inside the data folder being backed up ({DataDir}) - skipping to avoid runaway nested backups. Change the Backup folder in Settings.", backupDir, dataDir)
                    Return
                End If

                Dim intervalDays = _backupSettings.IntervalDays
                If intervalDays <= 0 Then Return
                If Not IsBackupDue(backupDir, intervalDays) Then Return
                If Not Directory.Exists(dataDir) Then Return ' nothing to back up yet

                Await Task.Run(Sub() PerformBackup(dataDir, backupDir))
            Catch ex As Exception
                _logger.LogError(ex, "Auto-backup failed")
            Finally
                _isRunning = False
            End Try
        End Sub

        Private Shared Function PathsOverlap(dataDir As String, backupDir As String) As Boolean
            Dim normalizedData = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            Dim normalizedBackup = Path.GetFullPath(backupDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            Return normalizedBackup.StartsWith(normalizedData, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' A plain text marker file (last_backup.txt, ISO 8601 UTC) living
        ''' alongside the zips themselves - deliberately not a new SQLite
        ''' table or app-settings field, since this is exactly the kind of
        ''' small, infrequently-read state that doesn't need a heavier store
        ''' (same "don't reach for a heavier tool than the problem needs"
        ''' reasoning already applied to RAG's hand-rolled cosine similarity).
        ''' No marker file at all means "never backed up yet" - due immediately
        ''' rather than waiting a full IntervalDays after first being enabled.
        ''' </summary>
        Private Shared Function IsBackupDue(backupDir As String, intervalDays As Integer) As Boolean
            Dim markerPath = Path.Combine(backupDir, MarkerFileName)
            If Not File.Exists(markerPath) Then Return True

            Dim lastBackupUtc As DateTime
            If Not DateTime.TryParse(File.ReadAllText(markerPath), Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, lastBackupUtc) Then
                Return True
            End If

            Return DateTime.UtcNow >= lastBackupUtc.AddDays(intervalDays)
        End Function

        ''' <summary>
        ''' Builds the zip by hand, file by file, rather than the simpler
        ''' ZipFile.CreateFromDirectory, since that method opens each source
        ''' file with FileShare.Read only, and the live SQLite database
        ''' (lemonrind.db) can't be opened under those sharing rules while
        ''' the app itself holds it open - the resulting exception aborts
        ''' the whole archive with zero entries written, with no per-file
        ''' opportunity to catch and continue. This version opens each file
        ''' with FileShare.ReadWrite instead (matches how SQLite's own
        ''' byte-range locking, not a whole-file exclusive handle, actually
        ''' works - a second reader with permissive sharing flags can read
        ''' it fine even mid-use) and wraps each file in its own Try/Catch,
        ''' so one unreadable file skips and logs a warning instead of
        ''' blanking out the entire backup.
        ''' </summary>
        Private Sub PerformBackup(dataDir As String, backupDir As String)
            Directory.CreateDirectory(backupDir)
            CheckpointDatabase()

            Dim zipPath = Path.Combine(backupDir, $"{ZipPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.zip")
            Dim filesIncluded = 0

            Using zipStream As New FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Using archive As New ZipArchive(zipStream, ZipArchiveMode.Create)
                    For Each filePath In Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories)
                        Try
                            Using sourceStream As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                                Dim entryName = Path.GetRelativePath(dataDir, filePath).Replace(Path.DirectorySeparatorChar, "/"c)
                                Dim entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
                                Using entryStream = entry.Open()
                                    sourceStream.CopyTo(entryStream)
                                End Using
                            End Using
                            filesIncluded += 1
                        Catch ex As Exception
                            _logger.LogWarning(ex, "Auto-backup: skipped a file that couldn't be read ({FilePath})", filePath)
                        End Try
                    Next
                End Using
            End Using

            File.WriteAllText(Path.Combine(backupDir, MarkerFileName), DateTime.UtcNow.ToString("o"))

            PruneOldBackups(backupDir)

            _logger.LogInformation("Auto-backup completed: {ZipPath} ({FilesIncluded} files)", zipPath, filesIncluded)
        End Sub

        ''' <summary>
        ''' AppDatabase.EnsureCreated turns on WAL mode - recent writes can sit
        ''' in the sidecar -wal file for a while before SQLite folds them back
        ''' into the main .db file on its own schedule. Without this, a backup
        ''' taken mid-way could zip a main file that's missing whatever's
        ''' still sitting in the WAL. TRUNCATE checkpoints everything back into
        ''' the main file AND shrinks the WAL to empty, so the file this backup
        ''' actually zips a moment later is genuinely complete. Best-effort,
        ''' like the rest of this class - a failed checkpoint still leaves a
        ''' usable (if very slightly stale) backup, not a broken one.
        ''' </summary>
        Private Sub CheckpointDatabase()
            Try
                Using connection = _database.OpenConnection()
                    Using command = connection.CreateCommand()
                        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"
                        command.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Auto-backup: WAL checkpoint failed, backup will proceed anyway")
            End Try
        End Sub

        ''' <summary>Prevents an unbounded number of timestamped zips, a real, easy-to-hit problem for anything left running for months.</summary>
        Private Sub PruneOldBackups(backupDir As String)
            Dim keepCount = Math.Max(1, _backupSettings.KeepCount)
            Dim zips = Directory.GetFiles(backupDir, $"{ZipPrefix}*.zip").
                OrderByDescending(Function(f) File.GetCreationTimeUtc(f)).
                ToList()

            For Each staleZip In zips.Skip(keepCount)
                Try
                    File.Delete(staleZip)
                Catch
                    ' Best-effort - a file locked by another process (e.g. an
                    ' antivirus scan mid-flight) just gets picked up on the
                    ' next successful backup's prune pass instead.
                End Try
            Next
        End Sub

    End Class

End Namespace
