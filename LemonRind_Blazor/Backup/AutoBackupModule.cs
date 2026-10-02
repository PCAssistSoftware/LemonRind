using System.IO.Compression;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using LemonRindBlazor.Configuration;
using LemonRindBlazor.Data;
using LemonRindBlazor.Modules;

namespace LemonRindBlazor.Backup;

/// <summary>
/// Periodically zips the whole portable data folder (SQLite DB, generated/
/// attached images, workspace files, and appsettings.json - all of it lives
/// under the one data folder, so a plain recursive zip of dataDir is a
/// complete disaster-recovery safety net with nothing extra to special-
/// case). Zero AI tools (GetTools returns empty) - this is pure background
/// infrastructure, not an assistant capability, but still implements
/// IAssistantModule to get the same free enable/disable + live-reload
/// plumbing (ModuleRegistry.ReconcileEnabledModulesAsync, a real row in
/// Settings' Modules list) every other module already has, rather than
/// inventing a second, parallel on/off mechanism just for this one feature.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Backup\AutoBackupModule.vb.
/// </summary>
public class AutoBackupModule(AppSettings settings, AppDatabase database, ILogger<AutoBackupModule> logger) : IAssistantModule
{
    private const string MarkerFileName = "last_backup.txt";
    private const string ZipPrefix = "lemonrind_backup_";

    // A backup is day-granularity by design (IntervalDays), so checking
    // every few hours is generous - no need for Scheduler's tighter 30-
    // second poll, which exists to make minute-level cron jobs feel
    // responsive. This also re-reads IntervalDays/BackupFolder/KeepCount
    // fresh from settings on every tick (never cached), so a changed
    // Settings value takes effect on the very next tick with no restart
    // needed - same live-reload principle as everything else in this app.
    private const int PollIntervalHours = 6;

    private readonly AppDataSettings _appDataSettings = settings.AppData;
    private readonly BackupSettings _backupSettings = settings.Backup;
    private readonly ModuleSettings _moduleSettings = settings.Modules;

    private Timer? _pollTimer;

    // Same re-entrancy guard shape as SchedulerModule's own polling - a
    // large data folder could take a while to zip, and a plain Timer
    // doesn't wait for its own callback to finish before scheduling the
    // next tick.
    private bool _isRunning;

    public string Name => "Auto-backup";
    public string ConfigKey => "Backup";
    public string Description => "Periodically zips your whole data folder (including all settings) as a safety net. No AI involvement - purely operational.";
    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    public Task OnStartupAsync(CancellationToken cancellationToken)
    {
        _pollTimer = new Timer(OnPollTimerTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(PollIntervalHours));
        return Task.CompletedTask;
    }

    public Task OnShutdownAsync(CancellationToken cancellationToken)
    {
        _pollTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Genuinely empty - see the class summary for why this module still exists as an IAssistantModule despite offering nothing to the model.</summary>
    public IEnumerable<AITool> GetTools() => [];

    private async void OnPollTimerTick(object? state)
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            var dataDir = _appDataSettings.ResolvedDataFolder();
            var backupDir = _backupSettings.ResolvedBackupFolder();

            // Defensive, not just theoretical - BackupFolder is a free-text
            // Settings field, so a user could point it inside DataFolder by
            // mistake (or by copy-pasting the wrong path). Backing up a
            // folder that also contains its own previous backups means
            // every new zip re-includes every old one, compounding forever -
            // refuse rather than let that quietly start.
            if (PathsOverlap(dataDir, backupDir))
            {
                logger.LogWarning("Auto-backup folder ({BackupDir}) is inside the data folder being backed up ({DataDir}) - skipping to avoid runaway nested backups. Change the Backup folder in Settings.", backupDir, dataDir);
                return;
            }

            var intervalDays = _backupSettings.IntervalDays;
            if (intervalDays <= 0) return;
            if (!IsBackupDue(backupDir, intervalDays)) return;
            if (!Directory.Exists(dataDir)) return; // nothing to back up yet

            await Task.Run(() => PerformBackup(dataDir, backupDir));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-backup failed");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private static bool PathsOverlap(string dataDir, string backupDir)
    {
        var normalizedData = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedBackup = Path.GetFullPath(backupDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedBackup.StartsWith(normalizedData, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A plain text marker file (last_backup.txt, ISO 8601 UTC) living
    /// alongside the zips themselves - deliberately not a new SQLite table
    /// or app-settings field, since this is exactly the kind of small,
    /// infrequently-read state that doesn't need a heavier store (same
    /// "don't reach for a heavier tool than the problem needs" reasoning
    /// already applied to RAG's hand-rolled cosine similarity). No marker
    /// file at all means "never backed up yet" - due immediately rather
    /// than waiting a full IntervalDays after first being enabled.
    /// </summary>
    private static bool IsBackupDue(string backupDir, int intervalDays)
    {
        var markerPath = Path.Combine(backupDir, MarkerFileName);
        if (!File.Exists(markerPath)) return true;

        if (!DateTime.TryParse(File.ReadAllText(markerPath), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var lastBackupUtc))
        {
            return true;
        }

        return DateTime.UtcNow >= lastBackupUtc.AddDays(intervalDays);
    }

    /// <summary>
    /// Builds the zip by hand, file by file, rather than the simpler
    /// ZipFile.CreateFromDirectory, since that method opens each source
    /// file with FileShare.Read only, and the live SQLite database
    /// (lemonrind.db) can't be opened under those sharing rules while the
    /// app itself holds it open - the resulting exception aborts the whole
    /// archive with zero entries written, with no per-file opportunity to
    /// catch and continue. This version opens each file with
    /// FileShare.ReadWrite instead (matches how SQLite's own byte-range
    /// locking, not a whole-file exclusive handle, actually works - a
    /// second reader with permissive sharing flags can read it fine even
    /// mid-use) and wraps each file in its own try/catch, so one unreadable
    /// file skips and logs a warning instead of blanking out the entire
    /// backup.
    /// </summary>
    private void PerformBackup(string dataDir, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        CheckpointDatabase();

        var zipPath = Path.Combine(backupDir, $"{ZipPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        var filesIncluded = 0;

        using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var filePath in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var entryName = Path.GetRelativePath(dataDir, filePath).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    sourceStream.CopyTo(entryStream);
                    filesIncluded++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Auto-backup: skipped a file that couldn't be read ({FilePath})", filePath);
                }
            }
        }

        File.WriteAllText(Path.Combine(backupDir, MarkerFileName), DateTime.UtcNow.ToString("o"));

        PruneOldBackups(backupDir);

        logger.LogInformation("Auto-backup completed: {ZipPath} ({FilesIncluded} files)", zipPath, filesIncluded);
    }

    /// <summary>
    /// AppDatabase.EnsureCreated turns on WAL mode - recent writes can sit
    /// in the sidecar -wal file for a while before SQLite folds them back
    /// into the main .db file on its own schedule. Without this, a backup
    /// taken mid-way could zip a main file that's missing whatever's still
    /// sitting in the WAL. TRUNCATE checkpoints everything back into the
    /// main file AND shrinks the WAL to empty, so the file this backup
    /// actually zips a moment later is genuinely complete. Best-effort,
    /// like the rest of this class - a failed checkpoint still leaves a
    /// usable (if very slightly stale) backup, not a broken one.
    /// </summary>
    private void CheckpointDatabase()
    {
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-backup: WAL checkpoint failed, backup will proceed anyway");
        }
    }

    /// <summary>Prevents an unbounded number of timestamped zips, a real, easy-to-hit problem for anything left running for months.</summary>
    private void PruneOldBackups(string backupDir)
    {
        var keepCount = Math.Max(1, _backupSettings.KeepCount);
        var zips = Directory.GetFiles(backupDir, $"{ZipPrefix}*.zip")
            .OrderByDescending(File.GetCreationTimeUtc)
            .ToList();

        foreach (var staleZip in zips.Skip(keepCount))
        {
            try
            {
                File.Delete(staleZip);
            }
            catch
            {
                // Best-effort - a file locked by another process (e.g. an
                // antivirus scan mid-flight) just gets picked up on the next
                // successful backup's prune pass instead.
            }
        }
    }
}
