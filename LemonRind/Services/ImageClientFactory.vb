Imports System.ClientModel
Imports OpenAI
Imports OpenAI.Images
Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' Builds the raw OpenAI.Images.ImageClient this app uses for
    ''' text-to-image generation - same "point the official OpenAI.NET SDK
    ''' at Lemonade's own base URL" pattern as LemonadeChatClientFactory/
    ''' EmbeddingClientFactory, just for images.
    '''
    ''' Deliberately the RAW ImageClient, not wrapped as
    ''' Microsoft.Extensions.AI's IImageGenerator - that type's own
    ''' ImageGenerationOptions has no fields at all for steps/cfg_scale/seed
    ''' (only size/count/model/response-format), because it's modelled on
    ''' the standard DALL-E-shaped OpenAI API. Lemonade's own
    ''' /v1/images/generations extends that with steps/cfg_scale/seed as
    ''' real, documented request fields - getting real control over those
    ''' needs ImageClient's low-level protocol method (GenerateImagesAsync(
    ''' BinaryContent, RequestOptions)), which sends whatever raw JSON body
    ''' it's given, rather than the strongly-typed convenience method. See
    ''' ImageGenerationService for where that JSON body actually gets built.
    ''' </summary>
    Public Class ImageClientFactory

        Private ReadOnly _settings As LemonadeSettings
        Private ReadOnly _imageGenSettings As ImageGenSettings

        Public Sub New(settings As AppSettings)
            _settings = settings.Lemonade
            _imageGenSettings = settings.ImageGen
        End Sub

        Public Function CreateImageClient() As ImageClient
            Dim options As New OpenAIClientOptions With {
                .Endpoint = New Uri(_settings.BaseUrl)
            }
            Dim credential As New ApiKeyCredential(If(String.IsNullOrWhiteSpace(_settings.ApiKey), "lemonade", _settings.ApiKey))
            Dim openAiClient As New OpenAIClient(credential, options)
            Return openAiClient.GetImageClient(_imageGenSettings.ModelId)
        End Function

    End Class

End Namespace
