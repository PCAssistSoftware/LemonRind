using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Scheduler;

/// <summary>One scheduled job - see ScheduledJobRunner for what firing it actually does.</summary>
public class ScheduledJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool IsEnabled { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Null/empty means "use whatever model is currently selected/default" - not every job needs to pin one.</summary>
    public string? ModelId { get; set; }
}

/// <summary>
/// Data access for scheduled jobs - an in-process, app-must-be-running
/// design (see SchedulerModule).
///
/// Ported from the VB.NET/WPF LemonRind app's Scheduler\SchedulerRepository.vb.
/// </summary>
public class SchedulerRepository(AppDatabase database)
{
    public string CreateJob(string name, string cronExpressionText, string prompt, DateTime? nextRunAt, string? modelId = null)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScheduledJobs (Id, Name, CronExpression, Prompt, IsEnabled, LastRunAt, NextRunAt, CreatedAt, ModelId)
            VALUES (@id, @name, @cron, @prompt, 1, NULL, @nextRunAt, @createdAt, @modelId);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@cron", cronExpressionText);
        command.Parameters.AddWithValue("@prompt", prompt);
        command.Parameters.AddWithValue("@nextRunAt", (object?)nextRunAt?.ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@modelId", string.IsNullOrEmpty(modelId) ? DBNull.Value : modelId);
        command.ExecuteNonQuery();
        return id;
    }

    /// <summary>Edits an existing job's name/schedule/prompt/model - nextRunAt should be freshly recomputed by the caller if cronExpressionText changed (this doesn't touch LastRunAt).</summary>
    public void UpdateJob(string id, string name, string cronExpressionText, string prompt, DateTime? nextRunAt, string? modelId = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Name = @name, CronExpression = @cron, Prompt = @prompt, NextRunAt = @nextRunAt, ModelId = @modelId WHERE Id = @id;";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@cron", cronExpressionText);
        command.Parameters.AddWithValue("@prompt", prompt);
        command.Parameters.AddWithValue("@nextRunAt", (object?)nextRunAt?.ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@modelId", string.IsNullOrEmpty(modelId) ? DBNull.Value : modelId);
        command.ExecuteNonQuery();
    }

    public void SetEnabled(string id, bool isEnabled)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET IsEnabled = @isEnabled WHERE Id = @id;";
        command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Called by the poller right after a job fires - records when it ran and when it's due next (null if the schedule has no future occurrence).</summary>
    public void RecordRun(string id, DateTime ranAt, DateTime? nextRunAt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET LastRunAt = @ranAt, NextRunAt = @nextRunAt WHERE Id = @id;";
        command.Parameters.AddWithValue("@ranAt", ranAt.ToString("o"));
        command.Parameters.AddWithValue("@nextRunAt", (object?)nextRunAt?.ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteJob(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public List<ScheduledJob> ListJobs()
    {
        var results = new List<ScheduledJob>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CronExpression, Prompt, IsEnabled, LastRunAt, NextRunAt, CreatedAt, ModelId FROM ScheduledJobs ORDER BY CreatedAt ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ScheduledJob
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                CronExpression = reader.GetString(2),
                Prompt = reader.GetString(3),
                IsEnabled = reader.GetInt32(4) != 0,
                LastRunAt = reader.IsDBNull(5) ? null : DateTimeHelpers.ParseUtc(reader.GetString(5)),
                NextRunAt = reader.IsDBNull(6) ? null : DateTimeHelpers.ParseUtc(reader.GetString(6)),
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(7)),
                ModelId = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return results;
    }
}
