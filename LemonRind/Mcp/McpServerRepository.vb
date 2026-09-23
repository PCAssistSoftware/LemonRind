Imports System.Text.Json
Imports LemonRind.Data

Namespace Mcp

    ''' <summary>One configured external MCP server - connected to via stdio, see McpModule.</summary>
    Public Class McpServerConfig
        Public Property Id As String
        Public Property Name As String
        Public Property Command As String
        Public Property Arguments As List(Of String)
        Public Property EnvironmentVariables As Dictionary(Of String, String)
        Public Property IsEnabled As Boolean
        Public Property CreatedAt As DateTime
    End Class

    ''' <summary>Data access for configured MCP servers - see McpModule for the stdio-transport design this feeds into.</summary>
    Public Class McpServerRepository

        Private ReadOnly _database As AppDatabase

        Public Sub New(database As AppDatabase)
            _database = database
        End Sub

        Public Function CreateServer(name As String, command As String, arguments As List(Of String), environmentVariables As Dictionary(Of String, String)) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using dbCommand = connection.CreateCommand()
                    dbCommand.CommandText = "
                        INSERT INTO McpServers (Id, Name, Command, ArgumentsJson, EnvironmentVariablesJson, IsEnabled, CreatedAt)
                        VALUES (@id, @name, @command, @argumentsJson, @envJson, 1, @createdAt);"
                    dbCommand.Parameters.AddWithValue("@id", id)
                    dbCommand.Parameters.AddWithValue("@name", name)
                    dbCommand.Parameters.AddWithValue("@command", command)
                    dbCommand.Parameters.AddWithValue("@argumentsJson", JsonSerializer.Serialize(arguments))
                    dbCommand.Parameters.AddWithValue("@envJson", JsonSerializer.Serialize(environmentVariables))
                    dbCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"))
                    dbCommand.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        Public Sub SetEnabled(id As String, isEnabled As Boolean)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE McpServers SET IsEnabled = @isEnabled WHERE Id = @id;"
                    command.Parameters.AddWithValue("@isEnabled", If(isEnabled, 1, 0))
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub DeleteServer(id As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "DELETE FROM McpServers WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Function ListServers() As List(Of McpServerConfig)
            Dim results As New List(Of McpServerConfig)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Name, Command, ArgumentsJson, EnvironmentVariablesJson, IsEnabled, CreatedAt FROM McpServers ORDER BY CreatedAt ASC;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New McpServerConfig With {
                                .Id = reader.GetString(0),
                                .Name = reader.GetString(1),
                                .Command = reader.GetString(2),
                                .Arguments = JsonSerializer.Deserialize(Of List(Of String))(reader.GetString(3)),
                                .EnvironmentVariables = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(reader.GetString(4)),
                                .IsEnabled = reader.GetInt32(5) <> 0,
                                .CreatedAt = ParseUtc(reader.GetString(6))
                            })
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

    End Class

End Namespace
