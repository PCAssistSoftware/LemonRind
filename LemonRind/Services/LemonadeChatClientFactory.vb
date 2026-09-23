Imports System.ClientModel
Imports Microsoft.Extensions.AI
Imports OpenAI
Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' Builds the IChatClient this app talks to the model through.
    ''' Lemonade speaks the OpenAI wire protocol, so this points the official
    ''' OpenAI.NET SDK at Lemonade's own base URL instead of api.openai.com -
    ''' an in-process inference library would lose the AMD NPU acceleration
    ''' Lemonade already provides.
    ''' Lemonade doesn't check the API key, so any non-empty string works -
    ''' the OpenAI SDK just requires one to be present. If a real key has
    ''' been configured (Lemonade's docs recommend one even though it's not
    ''' enforced), it's used instead of the placeholder.
    ''' </summary>
    Public Class LemonadeChatClientFactory

        Private ReadOnly _settings As LemonadeSettings

        Public Sub New(settings As AppSettings)
            _settings = settings.Lemonade
        End Sub

        Public Function CreateChatClient() As IChatClient
            Dim options As New OpenAIClientOptions With {
                .Endpoint = New Uri(_settings.BaseUrl)
            }
            Dim credential As New ApiKeyCredential(If(String.IsNullOrWhiteSpace(_settings.ApiKey), "lemonade", _settings.ApiKey))
            Dim openAiClient As New OpenAIClient(credential, options)

            ' Falls back to a placeholder when no chat model is configured
            ' yet (e.g. first run) - the OpenAI SDK throws on an empty model
            ' name at client-construction time, but the real model is always
            ' set per-request via ChatOptions.ModelId (see MainViewModel.
            ' SendAsync), so this placeholder is never actually sent to
            ' Lemonade.
            Dim chatModel = If(String.IsNullOrWhiteSpace(_settings.ChatModel), "unset", _settings.ChatModel)

            ' .UseFunctionInvocation() adds the automatic tool-calling loop:
            ' when the model asks to call a tool, this handles invoking it and
            ' feeding the result back, without the app writing that loop by
            ' hand. The actual tools attached to a given request come from
            ' ModuleRegistry.GetEnabledTools() (see MainViewModel).
            '
            ' IncludeDetailedErrors defaults to False, which would replace a
            ' thrown exception's own Message with a generic wrapper before
            ' the model ever sees it - defeating the point of a module
            ' writing a specific, actionable error message (e.g.
            ' SsrfSafeHttpFetcher's/PathSandbox's, already written "safe to
            ' show the model directly" per WebReaderModule's own comment).
            Return New ChatClientBuilder(openAiClient.GetChatClient(chatModel).AsIChatClient()).
                UseFunctionInvocation(configure:=Sub(client) client.IncludeDetailedErrors = True).
                Build()
        End Function

    End Class

End Namespace
