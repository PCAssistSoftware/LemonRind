Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' One model entry from GET /v1/models. Property names use
    ''' JsonPropertyName rather than relying on System.Text.Json's default
    ''' naming policy, so the mapping to Lemonade's actual JSON field names
    ''' (snake_case) is explicit and doesn't silently break if the default
    ''' policy ever changes.
    ''' </summary>
    Public Class LemonadeModelInfo
        <JsonPropertyName("id")>
        Public Property Id As String

        <JsonPropertyName("labels")>
        Public Property Labels As List(Of String)

        <JsonPropertyName("downloaded")>
        Public Property Downloaded As Boolean

        <JsonPropertyName("size")>
        Public Property SizeGb As Double

        <JsonPropertyName("max_context_window")>
        Public Property MaxContextWindow As Integer

        ''' <summary>Backend family, e.g. "llamacpp"/"sd-cpp"/"thenoise" - the catalog-level value, present for every downloaded model whether currently loaded or not.</summary>
        <JsonPropertyName("recipe")>
        Public Property Recipe As String
    End Class

    Friend Class LemonadeModelsResponse
        <JsonPropertyName("data")>
        Public Property Data As List(Of LemonadeModelInfo)
    End Class

    ''' <summary>
    ''' Recipe-specific runtime detail for one currently-loaded model - only
    ''' meaningful fields for an "llamacpp" LLM are mapped (ctx_size,
    ''' llamacpp_args); image-recipe fields (cfg_scale/steps/...) aren't
    ''' needed here, that's Settings' own ImageGenSettings concern.
    ''' </summary>
    Public Class LemonadeLoadedModelRecipeOptions
        <JsonPropertyName("ctx_size")>
        Public Property CtxSize As Integer?

        ''' <summary>The exact llama-server CLI flags Lemonade launched this model with - temperature/top-k/top-p/repeat-penalty/etc - the only place these are exposed at all.</summary>
        <JsonPropertyName("llamacpp_args")>
        Public Property LlamacppArgs As String
    End Class

    ''' <summary>One entry in GET /v1/health's all_models_loaded array - the currently-running instance's real detail, richer than the static /v1/models catalog entry for the same model.</summary>
    Public Class LemonadeLoadedModelInfo
        <JsonPropertyName("model_name")>
        Public Property ModelName As String

        <JsonPropertyName("device")>
        Public Property Device As String

        <JsonPropertyName("recipe")>
        Public Property Recipe As String

        <JsonPropertyName("recipe_options")>
        Public Property RecipeOptions As LemonadeLoadedModelRecipeOptions

        <JsonPropertyName("max_context_window")>
        Public Property MaxContextWindow As Integer?
    End Class

    ''' <summary>
    ''' The parts of GET /v1/health this app currently uses. Lemonade's real
    ''' response has more fields (system stats, ...) - only what's needed
    ''' right now is mapped; more gets added alongside the feature that first
    ''' needs it rather than guessed ahead of time.
    ''' </summary>
    Public Class LemonadeHealthInfo
        <JsonPropertyName("status")>
        Public Property Status As String

        <JsonPropertyName("model_loaded")>
        Public Property ModelLoaded As String

        <JsonPropertyName("websocket_port")>
        Public Property WebsocketPort As Integer

        ''' <summary>Every currently-loaded model's real runtime detail - not just the active chat model; the embedding/image models loaded alongside it show up here too.</summary>
        <JsonPropertyName("all_models_loaded")>
        Public Property AllModelsLoaded As List(Of LemonadeLoadedModelInfo)
    End Class

    Friend Class LemonadeLoadRequest
        <JsonPropertyName("model_name")>
        Public Property ModelName As String
    End Class

    Friend Class LemonadeLoadResponse
        <JsonPropertyName("status")>
        Public Property Status As String

        <JsonPropertyName("message")>
        Public Property Message As String
    End Class

    Friend Class LemonadeTokenizeRequest
        <JsonPropertyName("content")>
        Public Property Content As String
    End Class

    ''' <summary>Only the token COUNT is ever needed here, never the actual IDs/pieces, so nothing maps "with_pieces" output.</summary>
    Friend Class LemonadeTokenizeResponse
        <JsonPropertyName("tokens")>
        Public Property Tokens As List(Of Integer)
    End Class

    ''' <summary>
    ''' GET /v1/stats - performance figures for the last request, measured
    ''' server-side by Lemonade itself (not timed client-side), which is more
    ''' accurate since it isn't skewed by network/UI latency. Only the
    ''' per-request fields are mapped here, not the *_total cumulative
    ''' counters - not needed yet.
    ''' </summary>
    Public Class LemonadeStatsInfo
        <JsonPropertyName("time_to_first_token")>
        Public Property TimeToFirstToken As Double

        <JsonPropertyName("tokens_per_second")>
        Public Property TokensPerSecond As Double

        ''' <summary>
        ''' Tokens actually PROCESSED this request - excludes anything
        ''' served from the backend's prefix cache (see PromptTokens' own
        ''' comment). Legitimate for the Stats tab's per-turn "tokens in"
        ''' figure (it's genuinely what cost time/compute this turn), but
        ''' NOT what the context-usage bar should use - confirmed via
        ''' Lemonade's own docs (lemonade-server.ai/docs/api/lemonade)
        ''' this is "number of tokens processed", a different thing from
        ''' the full prompt size.
        ''' </summary>
        <JsonPropertyName("input_tokens")>
        Public Property InputTokens As Integer

        <JsonPropertyName("output_tokens")>
        Public Property OutputTokens As Integer

        ''' <summary>
        ''' "Total prompt tokens including cached tokens" (Lemonade's own
        ''' docs) - the real full prompt size for the last request,
        ''' unlike InputTokens which excludes whatever was served from the
        ''' prefix cache. This is what ContextUsageCurrent needs: confirmed
        ''' live that InputTokens alone can badly UNDER-report real context
        ''' usage on any turn that benefits from cache reuse (the system
        ''' prompt/tools/earlier history staying unchanged turn to turn is
        ''' exactly the common case, not an edge case) - a real drop from
        ''' 18% to 1% "used" between two turns in the same growing
        ''' conversation, which should never happen, was traced to this.
        ''' </summary>
        <JsonPropertyName("prompt_tokens")>
        Public Property PromptTokens As Integer
    End Class

    ''' <summary>
    ''' Talks to Lemonade's own management endpoints (not the OpenAI-compatible
    ''' chat surface). There's no official .NET client for this, so it's a
    ''' thin HttpClient wrapper against the documented endpoints at
    ''' lemonade-server.ai/docs/api.
    ''' </summary>
    Public Class LemonadeManagementClient

        Private ReadOnly _httpClient As HttpClient

        Public Sub New(settings As AppSettings)
            _httpClient = New HttpClient With {
                .BaseAddress = New Uri(settings.Lemonade.BaseUrl)
            }

            ' This plain HttpClient (unlike the OpenAI-SDK-based chat/
            ' embedding/image clients) only sets an Authorization header when
            ' a key has actually been configured - Lemonade doesn't require
            ' one, so an unset key here should mean "no Authorization
            ' header", not a harmless placeholder like the SDK clients need.
            If Not String.IsNullOrWhiteSpace(settings.Lemonade.ApiKey) Then
                _httpClient.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", settings.Lemonade.ApiKey)
            End If
        End Sub

        ''' <summary>
        ''' Models Lemonade already has on disk, ready to load - the model
        ''' selector only offers these, not the full downloadable catalog
        ''' (pulling new models is a separate, not-yet-built concern).
        ''' </summary>
        Public Async Function ListDownloadedModelsAsync(cancellationToken As CancellationToken) As Task(Of List(Of LemonadeModelInfo))
            Dim response = Await _httpClient.GetFromJsonAsync(Of LemonadeModelsResponse)("models", cancellationToken)
            Return response.Data.Where(Function(m) m.Downloaded).ToList()
        End Function

        Public Async Function GetHealthAsync(cancellationToken As CancellationToken) As Task(Of LemonadeHealthInfo)
            Return Await _httpClient.GetFromJsonAsync(Of LemonadeHealthInfo)("health", cancellationToken)
        End Function

        ''' <summary>
        ''' Stats for the most recent request only - call this right after a
        ''' chat completion finishes, not on a timer, or the numbers shown
        ''' won't correspond to the turn the user just saw.
        ''' </summary>
        Public Async Function GetStatsAsync(cancellationToken As CancellationToken) As Task(Of LemonadeStatsInfo)
            Return Await _httpClient.GetFromJsonAsync(Of LemonadeStatsInfo)("stats", cancellationToken)
        End Function

        ''' <summary>
        ''' Explicitly loads a model into memory. Can take a long time for a
        ''' large model (a 21GB model took roughly two minutes in testing) -
        ''' callers should show a busy/loading state, not assume this returns
        ''' quickly.
        ''' </summary>
        Public Async Function LoadModelAsync(modelName As String, cancellationToken As CancellationToken) As Task
            Dim request As New LemonadeLoadRequest With {.ModelName = modelName}
            Dim httpResponse = Await _httpClient.PostAsJsonAsync("load", request, cancellationToken)
            httpResponse.EnsureSuccessStatusCode()

            Dim result = Await httpResponse.Content.ReadFromJsonAsync(Of LemonadeLoadResponse)(cancellationToken)
            If result.Status <> "success" Then
                Throw New InvalidOperationException($"Lemonade failed to load model '{modelName}': {result.Message}")
            End If
        End Function

        ''' <summary>
        ''' Real tokenization via the currently-loaded model's own
        ''' tokenizer (llama.cpp-compatible POST /v1/tokenize) - an exact
        ''' count, not the chars/4 approximation used elsewhere in this app
        ''' before this existed. Docs note this call doesn't count toward
        ''' the model's own context window (a stateless utility, not a
        ''' real generation request), so it's safe to call as often as
        ''' estimation needs. Returns just the count (Tokens.Count), never
        ''' the actual token IDs - nothing here needs the pieces themselves.
        ''' </summary>
        Public Async Function TokenizeAsync(content As String, cancellationToken As CancellationToken) As Task(Of Integer)
            If String.IsNullOrEmpty(content) Then Return 0

            Dim request As New LemonadeTokenizeRequest With {.Content = content}
            Dim httpResponse = Await _httpClient.PostAsJsonAsync("tokenize", request, cancellationToken)
            httpResponse.EnsureSuccessStatusCode()

            Dim result = Await httpResponse.Content.ReadFromJsonAsync(Of LemonadeTokenizeResponse)(cancellationToken)
            Return If(result?.Tokens?.Count, 0)
        End Function

    End Class

End Namespace
