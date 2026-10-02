using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Services;

public class LemonadeLogEntry
{
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("line")]
    public string Line { get; set; } = "";
}

internal class LemonadeLogSnapshotMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("entries")]
    public List<LemonadeLogEntry> Entries { get; set; } = [];
}

internal class LemonadeLogEntryMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("entry")]
    public LemonadeLogEntry? Entry { get; set; }
}

/// <summary>
/// Lemonade's real-time log streaming WebSocket API (documented
/// `WS /logs/stream`). Severity values are "Info"/"Warn"/"Error"/"Fatal",
/// and the snapshot message arrives split across multiple WebSocket
/// frames, needing accumulation until EndOfMessage rather than a single
/// read.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Services\LemonadeLogClient.vb.
/// </summary>
public class LemonadeLogClient(AppSettings settings, LemonadeManagementClient managementClient)
{
    private readonly Uri _baseUrl = new(settings.Lemonade.BaseUrl);
    private readonly string _apiKey = settings.Lemonade.ApiKey;

    /// <summary>
    /// Connects, subscribes, and streams log data until cancelled or the
    /// connection drops. onSnapshot fires once, with every entry from the
    /// initial snapshot (oldest to newest) - kept separate from onEntry
    /// (one live entry at a time) specifically so the caller can add the
    /// whole snapshot to its UI collection in one batch rather than one
    /// UI-thread round-trip per entry.
    /// </summary>
    public async Task StreamAsync(Action<List<LemonadeLogEntry>> onSnapshot, Action<LemonadeLogEntry> onEntry, CancellationToken cancellationToken)
    {
        var health = await managementClient.GetHealthAsync(cancellationToken)
            ?? throw new InvalidOperationException("Couldn't reach Lemonade to determine its websocket port.");
        var wsUri = new UriBuilder(_baseUrl)
        {
            Scheme = _baseUrl.Scheme == "https" ? "wss" : "ws",
            Port = health.WebsocketPort,
            Path = "/logs/stream",
        };

        using var ws = new ClientWebSocket();
        // Same optional credential as LemonadeManagementClient's REST calls -
        // the WebSocket handshake is a real HTTP request too.
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        }
        await ws.ConnectAsync(wsUri.Uri, cancellationToken);

        var subscribeJson = JsonSerializer.Serialize(new { type = "logs.subscribe", after_seq = (long?)null });
        var subscribeBytes = Encoding.UTF8.GetBytes(subscribeJson);
        await ws.SendAsync(new ArraySegment<byte>(subscribeBytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var messageText = await ReceiveFullMessageAsync(ws, cancellationToken);
            if (messageText is null) break; // server closed the connection

            // Peek at "type" via a lightweight parse rather than guessing
            // which DTO to deserialize into first - the two message shapes
            // are different enough that trying one then falling back to
            // the other would silently misparse either kind.
            using var doc = JsonDocument.Parse(messageText);
            var messageType = doc.RootElement.GetProperty("type").GetString();
            switch (messageType)
            {
                case "logs.snapshot":
                    var snapshot = JsonSerializer.Deserialize<LemonadeLogSnapshotMessage>(messageText);
                    if (snapshot is not null) onSnapshot(snapshot.Entries);
                    break;
                case "logs.entry":
                    var entryMessage = JsonSerializer.Deserialize<LemonadeLogEntryMessage>(messageText);
                    if (entryMessage?.Entry is not null) onEntry(entryMessage.Entry);
                    break;
            }
        }
    }

    /// <summary>Loops ReceiveAsync and accumulates until EndOfMessage - a large snapshot message can arrive split across multiple frames. Null if the server closed the connection.</summary>
    private static async Task<string?> ReceiveFullMessageAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
