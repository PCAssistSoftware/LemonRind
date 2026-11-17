using System.Text.Json;
using LemonRindBlazor.Data;

namespace LemonRindBlazor.Mcp;

/// <summary>One configured external MCP server - connected to via stdio, see McpModule.</summary>
public class McpServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    /// <summary>Set for a server reached at a web address (streamable HTTP) instead of started by a command.</summary>
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Data access for configured MCP servers - see McpModule for the
/// stdio-transport design this feeds into.
///
/// Ported from the VB.NET/WPF LemonRind app's Mcp\McpServerRepository.vb.
/// </summary>
public class McpServerRepository(AppDatabase database)
{
    public string CreateServer(string name, string command, List<string> arguments, Dictionary<string, string> environmentVariables)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = """
            INSERT INTO McpServers (Id, Name, Command, ArgumentsJson, EnvironmentVariablesJson, IsEnabled, CreatedAt)
            VALUES (@id, @name, @command, @argumentsJson, @envJson, 1, @createdAt);
            """;
        dbCommand.Parameters.AddWithValue("@id", id);
        dbCommand.Parameters.AddWithValue("@name", name);
        dbCommand.Parameters.AddWithValue("@command", command);
        dbCommand.Parameters.AddWithValue("@argumentsJson", JsonSerializer.Serialize(arguments));
        dbCommand.Parameters.AddWithValue("@envJson", JsonSerializer.Serialize(environmentVariables));
        dbCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        dbCommand.ExecuteNonQuery();
        return id;
    }

    /// <summary>Adds a server that is already running and reached at a web address.</summary>
    public string CreateHttpServer(string name, string url, Dictionary<string, string> headers)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = """
            INSERT INTO McpServers (Id, Name, Command, ArgumentsJson, EnvironmentVariablesJson, Url, HeadersJson, IsEnabled, CreatedAt)
            VALUES (@id, @name, '', '[]', '{}', @url, @headersJson, 1, @createdAt);
            """;
        dbCommand.Parameters.AddWithValue("@id", id);
        dbCommand.Parameters.AddWithValue("@name", name);
        dbCommand.Parameters.AddWithValue("@url", url);
        dbCommand.Parameters.AddWithValue("@headersJson", JsonSerializer.Serialize(headers));
        dbCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        dbCommand.ExecuteNonQuery();
        return id;
    }

    public bool ServerNameExists(string name) =>
        ListServers().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public void SetEnabled(string id, bool isEnabled)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE McpServers SET IsEnabled = @isEnabled WHERE Id = @id;";
        command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteServer(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM McpServers WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public List<McpServerConfig> ListServers()
    {
        var results = new List<McpServerConfig>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Command, ArgumentsJson, EnvironmentVariablesJson, IsEnabled, CreatedAt, Url, HeadersJson FROM McpServers ORDER BY CreatedAt ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new McpServerConfig
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Command = reader.GetString(2),
                Arguments = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? [],
                IsEnabled = reader.GetInt32(5) != 0,
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(6)),
                Url = reader.GetString(7),
                Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8)) ?? [],
            });
        }
        return results;
    }
}
