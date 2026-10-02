Imports LemonRind.Data

Namespace Scheduler

    ''' <summary>One scheduled job - see ScheduledJobRunner for what firing it actually does.</summary>
    Public Class ScheduledJob
        Public Property Id As String
        Public Property Name As String
        Public Property CronExpression As String
        Public Property Prompt As String
        Public Property IsEnabled As Boolean
        Public Property LastRunAt As DateTime?
        Public Property NextRunAt As DateTime?
        Public Property CreatedAt As DateTime

        ''' <summary>Nothing/empty means "use whatever model is currently selected/default" - not every job needs to pin one.</summary>
        Public Property ModelId As String
    End Class

    ''' <summary>Data access for scheduled jobs - an in-process, app-must-be-running design (see SchedulerModule).</summary>
    Public Class SchedulerRepository

        Private ReadOnly _database As AppDatabase

        Public Sub New(database As AppDatabase)
            _database = database
        End Sub

        ''' <summary>Parameter named cronExpressionText, not cronExpression, for consistency with SchedulerModule's own naming (see its comment on why that one matters there - here it's just consistency, not a compile requirement, since this file never references Cronos.CronExpression directly).</summary>
        Public Function CreateJob(name As String, cronExpressionText As String, prompt As String, nextRunAt As DateTime?, Optional modelId As String = Nothing) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO ScheduledJobs (Id, Name, CronExpression, Prompt, IsEnabled, LastRunAt, NextRunAt, CreatedAt, ModelId)
                        VALUES (@id, @name, @cron, @prompt, 1, NULL, @nextRunAt, @createdAt, @modelId);"
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@name", name)
                    command.Parameters.AddWithValue("@cron", cronExpressionText)
                    command.Parameters.AddWithValue("@prompt", prompt)
                    command.Parameters.AddWithValue("@nextRunAt", If(nextRunAt Is Nothing, CObj(DBNull.Value), nextRunAt.Value.ToString("o")))
                    command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"))
                    command.Parameters.AddWithValue("@modelId", If(String.IsNullOrEmpty(modelId), CObj(DBNull.Value), modelId))
                    command.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        ''' <summary>Edits an existing job's name/schedule/prompt/model - nextRunAt should be freshly recomputed by the caller if cronExpressionText changed (this doesn't touch LastRunAt).</summary>
        Public Sub UpdateJob(id As String, name As String, cronExpressionText As String, prompt As String, nextRunAt As DateTime?, Optional modelId As String = Nothing)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE ScheduledJobs SET Name = @name, CronExpression = @cron, Prompt = @prompt, NextRunAt = @nextRunAt, ModelId = @modelId WHERE Id = @id;"
                    command.Parameters.AddWithValue("@name", name)
                    command.Parameters.AddWithValue("@cron", cronExpressionText)
                    command.Parameters.AddWithValue("@prompt", prompt)
                    command.Parameters.AddWithValue("@nextRunAt", If(nextRunAt Is Nothing, CObj(DBNull.Value), nextRunAt.Value.ToString("o")))
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@modelId", If(String.IsNullOrEmpty(modelId), CObj(DBNull.Value), modelId))
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub SetEnabled(id As String, isEnabled As Boolean)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE ScheduledJobs SET IsEnabled = @isEnabled WHERE Id = @id;"
                    command.Parameters.AddWithValue("@isEnabled", If(isEnabled, 1, 0))
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Called by the poller right after a job fires - records when it ran and when it's due next (Nothing if the schedule has no future occurrence).</summary>
        Public Sub RecordRun(id As String, ranAt As DateTime, nextRunAt As DateTime?)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE ScheduledJobs SET LastRunAt = @ranAt, NextRunAt = @nextRunAt WHERE Id = @id;"
                    command.Parameters.AddWithValue("@ranAt", ranAt.ToString("o"))
                    command.Parameters.AddWithValue("@nextRunAt", If(nextRunAt Is Nothing, CObj(DBNull.Value), nextRunAt.Value.ToString("o")))
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub DeleteJob(id As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Function ListJobs() As List(Of ScheduledJob)
            Dim results As New List(Of ScheduledJob)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Name, CronExpression, Prompt, IsEnabled, LastRunAt, NextRunAt, CreatedAt, ModelId FROM ScheduledJobs ORDER BY CreatedAt ASC;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New ScheduledJob With {
                                .Id = reader.GetString(0),
                                .Name = reader.GetString(1),
                                .CronExpression = reader.GetString(2),
                                .Prompt = reader.GetString(3),
                                .IsEnabled = reader.GetInt32(4) <> 0,
                                .LastRunAt = If(reader.IsDBNull(5), CType(Nothing, DateTime?), ParseUtc(reader.GetString(5))),
                                .NextRunAt = If(reader.IsDBNull(6), CType(Nothing, DateTime?), ParseUtc(reader.GetString(6))),
                                .CreatedAt = ParseUtc(reader.GetString(7)),
                                .ModelId = If(reader.IsDBNull(8), Nothing, reader.GetString(8))
                            })
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

    End Class

End Namespace
