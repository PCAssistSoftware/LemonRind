Imports System.IO
Imports System.Text.Json
Imports System.Threading
Imports OpenAI.Images
Imports LemonRind.Configuration

Namespace Services

    ''' <summary>
    ''' The actual "call Lemonade, save the file, return a file:// URI" work,
    ''' shared by ImageGenModule's generate_image tool and MainViewModel's
    ''' direct-model-selection path (an image-labeled model picked straight
    ''' in the main chat dropdown skips the LLM entirely and calls this
    ''' directly, matching how Lemonade's own web UI behaves) without
    ''' duplicating the request-building/response-parsing logic in two
    ''' places.
    '''
    ''' Sends a hand-built JSON request via ImageClient's low-level protocol
    ''' method (GenerateImagesAsync(BinaryContent, RequestOptions)) rather
    ''' than the SDK's strongly-typed convenience method, because Lemonade's
    ''' /v1/images/generations extends the standard OpenAI request shape with
    ''' steps/cfg_scale/seed that the typed OpenAI.Images.ImageGenerationOptions
    ''' class has no fields for at all (it's modelled on the standard DALL-E
    ''' API, which doesn't have these).
    ''' </summary>
    Public Class ImageGenerationService

        Private ReadOnly _createImageClient As Func(Of ImageClient)
        Private ReadOnly _imagesFolder As String

        Public Sub New(imageClientFactory As ImageClientFactory, settings As AppSettings)
            ' Func(Of ImageClient), not the client directly - same lazy-
            ' construction reasoning as ImageGenModule's own DI registration:
            ' an empty ImageModel setting must only fail when a generation
            ' is actually attempted, not eagerly at DI-graph-construction
            ' time, since every module gets constructed eagerly regardless
            ' of whether it's enabled (see Application.xaml.vb's comment).
            _createImageClient = Function() imageClientFactory.CreateImageClient()
            _imagesFolder = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "GeneratedImages")
        End Sub

        ''' <summary>Generates one image and saves it to disk, returning its local file:// URI. Throws on any failure - callers decide how to surface that (a tool-result string vs. a chat bubble's error text differ).</summary>
        Public Async Function GenerateAsync(prompt As String, modelId As String, width As Integer, height As Integer,
                                             steps As Integer, cfgScale As Double, seed As Integer,
                                             cancellationToken As CancellationToken) As Task(Of String)
            Dim imageClient = _createImageClient()

            Dim requestBody As New Dictionary(Of String, Object) From {
                {"model", modelId},
                {"prompt", prompt},
                {"size", $"{width}x{height}"},
                {"steps", steps},
                {"cfg_scale", cfgScale},
                {"seed", seed},
                {"response_format", "b64_json"}
            }
            Dim requestJson = JsonSerializer.Serialize(requestBody)

            Dim result = Await imageClient.GenerateImagesAsync(
                System.ClientModel.BinaryContent.CreateJson(requestJson),
                New System.ClientModel.Primitives.RequestOptions With {.CancellationToken = cancellationToken})

            Dim responseText = result.GetRawResponse().Content.ToString()
            Using doc = JsonDocument.Parse(responseText)
                Dim dataArray = doc.RootElement.GetProperty("data")
                If dataArray.GetArrayLength() = 0 Then
                    Throw New InvalidOperationException("Image generation returned no image data.")
                End If

                Dim base64 = dataArray(0).GetProperty("b64_json").GetString()
                Dim imageBytes = Convert.FromBase64String(base64)

                Directory.CreateDirectory(_imagesFolder)
                Dim fileName = $"img_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.png"
                Dim fullPath = Path.Combine(_imagesFolder, fileName)
                Await File.WriteAllBytesAsync(fullPath, imageBytes, cancellationToken)

                ' file:// URI, not a bare Windows path - Markdig.Wpf's image
                ' renderer builds a BitmapImage from the link URL, which
                ' needs a well-formed URI (a raw path with spaces/backslashes
                ' wouldn't parse), and New Uri(...).AbsoluteUri handles that
                ' escaping correctly.
                Return New Uri(fullPath).AbsoluteUri
            End Using
        End Function

    End Class

End Namespace
