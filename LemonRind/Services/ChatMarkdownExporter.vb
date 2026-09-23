Imports System.Text
Imports Microsoft.Extensions.AI
Imports LemonRind.Data

Namespace Services

    ''' <summary>
    ''' Turns one session's full stored history into a single Markdown
    ''' document. Stateless (a Module, like FileTextExtractor), reads
    ''' straight from StoredChatMessage rows rather than the live Messages
    ''' collection, so a right-clicked sidebar session can be exported even
    ''' if it isn't the one currently open.
    ''' </summary>
    Public Module ChatMarkdownExporter

        ''' <summary>Reasoning and tool-call chips are included, each clearly labeled and visually separated from the actual reply - collapsible <details> for thinking (present, but out of the way by default), a bold tool-use line right before the answer.</summary>
        Public Function BuildMarkdown(sessionTitle As String, messages As IEnumerable(Of StoredChatMessage)) As String
            Dim builder As New StringBuilder()
            builder.AppendLine($"# {sessionTitle}")
            builder.AppendLine()
            builder.AppendLine($"*Exported {DateTime.Now:dd/MM/yyyy HH:mm}*")
            builder.AppendLine()

            For Each message In messages
                Dim roleLabel = If(message.Role = ChatRole.User.Value, "You", "Assistant")

                builder.AppendLine("---")
                builder.AppendLine()
                builder.AppendLine($"**{roleLabel}** · {message.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}")
                builder.AppendLine()

                If Not String.IsNullOrEmpty(message.ToolCalls) Then
                    builder.AppendLine($"🔧 *Tools used: {message.ToolCalls.Replace(",", ", ")}*")
                    builder.AppendLine()
                End If

                If Not String.IsNullOrEmpty(message.ReasoningContent) Then
                    builder.AppendLine("<details>")
                    builder.AppendLine("<summary>Thinking</summary>")
                    builder.AppendLine()
                    builder.AppendLine(message.ReasoningContent)
                    builder.AppendLine()
                    builder.AppendLine("</details>")
                    builder.AppendLine()
                End If

                ' An attached image's path marker becomes a real Markdown
                ' image link (same file:// convention generated images
                ' already use) rather than showing the raw "[Attached
                ' image: ...]" text - a Markdown viewer that resolves local
                ' file links (VS Code, Obsidian, ...) will actually display
                ' the photo inline.
                Dim content = message.Content
                Dim parsedImage = ImageAttachmentService.TryParseAttachedImageMarker(content)
                If parsedImage.ImagePath IsNot Nothing Then
                    builder.AppendLine($"![attached image]({New Uri(parsedImage.ImagePath).AbsoluteUri})")
                    builder.AppendLine()
                    content = parsedImage.RemainingText
                End If

                builder.AppendLine(content)
                builder.AppendLine()
            Next

            Return builder.ToString()
        End Function

    End Module

End Namespace
