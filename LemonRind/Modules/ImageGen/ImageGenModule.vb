Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration
Imports LemonRind.Services

Namespace Modules.ImageGen

    ''' <summary>
    ''' Text-to-image generation, exposed as a single generate_image
    ''' chat tool so a real chat model can ask for an image mid-conversation
    ''' without switching models. The other, more direct way to generate
    ''' images - picking an image-labeled model straight in the main chat
    ''' dropdown, which skips the LLM entirely - lives in MainViewModel
    ''' instead (see its SendImageGenerationTurnAsync); both share the actual
    ''' generation work via ImageGenerationService rather than duplicating
    ''' the request-building/response-parsing logic.
    ''' </summary>
    Public Class ImageGenModule
        Implements IAssistantModule

        Private ReadOnly _imageGenerationService As ImageGenerationService
        Private ReadOnly _imageGenSettings As ImageGenSettings
        Private ReadOnly _moduleSettings As ModuleSettings

        Public Sub New(settings As AppSettings, imageGenerationService As ImageGenerationService)
            _imageGenerationService = imageGenerationService
            _imageGenSettings = settings.ImageGen
            _moduleSettings = settings.Modules
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "Image generation"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "ImageGen"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Generates images from a text prompt using Lemonade's configured image model, shown inline in the chat. Asks you to confirm the image size before each generation."
            End Get
        End Property

        ''' <summary>Read live off the shared Modules.Enabled dictionary each time, not cached at construction - toggling this module in Settings takes effect immediately, no restart needed.</summary>
        Public ReadOnly Property IsEnabled As Boolean Implements IAssistantModule.IsEnabled
            Get
                Return _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, False)
            End Get
        End Property

        Public Function OnStartupAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnStartupAsync
            Return Task.CompletedTask
        End Function

        Public Function OnShutdownAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnShutdownAsync
            Return Task.CompletedTask
        End Function

        Public Function GetTools() As IEnumerable(Of AITool) Implements IAssistantModule.GetTools
            Return {
                AIFunctionFactory.Create(
                    method:=Function(prompt As String) GenerateImageAsync(prompt),
                    name:="generate_image",
                    description:="Generates an image from a text description using a local image-generation model. " &
                        "The user will be asked to confirm the image size before it actually generates - if they " &
                        "cancel, no image is created. Returns ready-made Markdown image syntax on success - include " &
                        "that exact Markdown, unchanged, somewhere in your reply so the user actually sees the " &
                        "image; do not just describe the image in words.")
            }
        End Function

        Private Async Function GenerateImageAsync(prompt As String) As Task(Of String)
            If String.IsNullOrWhiteSpace(prompt) Then Return "Provide a text description of the image to generate."

            Dim sizeChoice = ImageSizeDialog.Show(prompt, _imageGenSettings.DefaultWidth, _imageGenSettings.DefaultHeight)
            If Not sizeChoice.Confirmed Then Return "The user cancelled image generation."

            Try
                Dim imageUri = Await _imageGenerationService.GenerateAsync(
                    prompt, _imageGenSettings.ModelId, sizeChoice.Width, sizeChoice.Height,
                    _imageGenSettings.Steps, _imageGenSettings.CfgScale, _imageGenSettings.Seed,
                    CancellationToken.None)

                Dim altText = If(prompt.Length > 80, prompt.Substring(0, 80) & "...", prompt)
                Return $"Image generated. Include this exact Markdown in your reply: ![{altText}]({imageUri})"
            Catch ex As Exception
                Return $"Couldn't generate that image: {ex.Message}"
            End Try
        End Function

    End Class

End Namespace
