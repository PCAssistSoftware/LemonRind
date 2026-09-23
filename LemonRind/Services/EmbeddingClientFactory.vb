Imports System.ClientModel
Imports Microsoft.Extensions.AI
Imports OpenAI
Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' Builds the embedding generator this app uses for both long-term
    ''' memory and Knowledge Bases - same "point an OpenAI-compatible client
    ''' at Lemonade's embeddings endpoint" pattern as LemonadeChatClientFactory,
    ''' just for embeddings instead of chat.
    ''' </summary>
    Public Class EmbeddingClientFactory

        Private ReadOnly _settings As LemonadeSettings

        Public Sub New(settings As AppSettings)
            _settings = settings.Lemonade
        End Sub

        Public Function CreateEmbeddingGenerator() As IEmbeddingGenerator(Of String, Embedding(Of Single))
            Dim options As New OpenAIClientOptions With {
                .Endpoint = New Uri(_settings.BaseUrl)
            }
            Dim credential As New ApiKeyCredential(If(String.IsNullOrWhiteSpace(_settings.ApiKey), "lemonade", _settings.ApiKey))
            Dim openAiClient As New OpenAIClient(credential, options)

            ' Falls back to a placeholder when no embedding model is
            ' configured yet (e.g. first run) - the OpenAI SDK throws on an
            ' empty model name at client-construction time, and unlike chat,
            ' there's no per-request override for the embedding model, so
            ' this only matters until a real one is set in Settings.
            Dim embeddingModel = If(String.IsNullOrWhiteSpace(_settings.EmbeddingModel), "unset", _settings.EmbeddingModel)
            Return openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator()
        End Function

    End Class

End Namespace
