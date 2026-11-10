Imports System.IO
Imports System.Threading
Imports Microsoft.Extensions.AI

Namespace Services

    ''' <summary>A picture could not be read. The message is fit to show the person.</summary>
    Public Class PictureReadException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Reads a picture for a knowledge base: a vision model writes out any text in it and describes what it shows, and that
    ''' description is what gets searched (a knowledge base can only search text).
    '''
    ''' Which model? Whatever chat model Lemonade has loaded right now, provided it can see pictures (Lemonade labels those
    ''' "vision"). Nothing is loaded or swapped for the purpose: loading another model could push out the one a chat or a
    ''' scheduled job is using. If the loaded model cannot see pictures the person is told which ones can.
    ''' </summary>
    Public Class PictureReader

        Public Shared ReadOnly PictureExtensions As String() = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"}

        Public Const Prompt As String =
            "Describe this picture so that someone who cannot see it could find it by searching later. " &
            "First write out, word for word, any text that appears in it. " &
            "Then describe what it shows: the objects, people, layout, and any charts, tables or numbers. " &
            "Be factual and specific, and do not guess at anything that is not visible."

        Private Const MaxDescriptionTokens As Integer = 4096 ' a thinking model's thinking counts towards this, so it needs room

        Private ReadOnly _management As LemonadeManagementClient
        Private ReadOnly _chatClient As IChatClient
        Private ReadOnly _images As ImageAttachmentService

        Public Sub New(management As LemonadeManagementClient, chatClient As IChatClient, images As ImageAttachmentService)
            _management = management
            _chatClient = chatClient
            _images = images
        End Sub

        Public Shared Function IsPicture(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Return PictureExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())
        End Function

        Private Shared Function HasLabel(model As LemonadeModelInfo, label As String) As Boolean
            Return model.Labels IsNot Nothing AndAlso model.Labels.Any(Function(l) String.Equals(l, label, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>The name of the loaded chat model that can see pictures; throws PictureReadException saying what to do if there is none.</summary>
        Public Async Function LoadedVisionModelAsync(cancellationToken As CancellationToken) As Task(Of String)
            Dim models As List(Of LemonadeModelInfo)
            Dim health As LemonadeHealthInfo
            Try
                models = Await _management.ListDownloadedModelsAsync(cancellationToken)
                health = Await _management.GetHealthAsync(cancellationToken)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                Throw New PictureReadException($"Could not ask Lemonade which model is loaded: {ex.Message}")
            End Try

            Dim byName As New Dictionary(Of String, LemonadeModelInfo)(StringComparer.OrdinalIgnoreCase)
            For Each model In models
                If Not byName.ContainsKey(model.Id) Then byName(model.Id) = model
            Next

            Dim loaded As New List(Of String)
            If health IsNot Nothing Then
                If health.AllModelsLoaded IsNot Nothing Then loaded.AddRange(health.AllModelsLoaded.Select(Function(m) m.ModelName))
                loaded.Add(health.ModelLoaded)
            End If
            Dim chatLoaded = loaded.Where(Function(name) Not String.IsNullOrWhiteSpace(name)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                Where(Function(name) byName.ContainsKey(name) AndAlso HasLabel(byName(name), "chat")).
                ToList()

            For Each name In chatLoaded
                If HasLabel(byName(name), "vision") Then Return name
            Next

            Dim canSee = models.Where(Function(m) HasLabel(m, "vision")).Select(Function(m) m.Id).OrderBy(Function(id) id, StringComparer.OrdinalIgnoreCase).Take(5).ToList()
            Dim examples = If(canSee.Count > 0, $" Models that can: {String.Join(", ", canSee)}.", "")
            If chatLoaded.Count > 0 Then
                Throw New PictureReadException($"The loaded model '{chatLoaded(0)}' cannot read pictures. Load one that can and add the picture again.{examples}")
            End If
            Throw New PictureReadException($"No chat model is loaded. Load one that can read pictures and add the picture again.{examples}")
        End Function

        ''' <summary>The picture as a JPEG of at most 1568 pixels on its longest side (what vision models read well), as bytes.</summary>
        Public Function ShrinkPicture(path As String) As Byte()
            Dim saved As String = Nothing
            Try
                saved = _images.SaveResizedCopy(path)
                Return File.ReadAllBytes(saved)
            Catch ex As Exception
                Throw New PictureReadException($"'{System.IO.Path.GetFileName(path)}' could not be read as a picture: {ex.Message}")
            Finally
                If saved IsNot Nothing Then
                    Try
                        File.Delete(saved)
                    Catch
                        ' a leftover temporary copy is harmless
                    End Try
                End If
            End Try
        End Function

        ''' <summary>Asks the loaded vision model to read the picture. Returns the description and the model's name.</summary>
        Public Async Function DescribeAsync(image As Byte(), name As String, cancellationToken As CancellationToken) As Task(Of (Text As String, Model As String))
            Dim model = Await LoadedVisionModelAsync(cancellationToken)
            Dim contents As New List(Of AIContent) From {New TextContent(Prompt), New DataContent(image, "image/jpeg")}
            Dim message As New ChatMessage(ChatRole.User, contents)
            Dim options As New ChatOptions With {.ModelId = model, .MaxOutputTokens = MaxDescriptionTokens}

            Dim response As ChatResponse
            Try
                response = Await _chatClient.GetResponseAsync({message}, options, cancellationToken)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                Throw New PictureReadException(ex.Message)
            End Try

            Dim text = If(response.Text, "").Trim()
            If text.Length = 0 Then
                Throw New PictureReadException($"'{model}' did not write a description of '{name}' (a model that thinks may have used its whole allowance thinking). Try again, or load a model that thinks less.")
            End If
            Return (text, model)
        End Function

    End Class

End Namespace
