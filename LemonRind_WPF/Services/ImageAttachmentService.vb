Imports System.IO
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports LemonRind.Configuration
Imports Microsoft.Extensions.AI

Namespace Services

    ''' <summary>
    ''' Takes a user-picked image file, downscales it and saves a local copy
    ''' under Data\Workspace\AttachedImages (same
    ''' "Workspace" root/naming convention as ImageGenerationService's own
    ''' GeneratedImages folder), and builds the Microsoft.Extensions.AI
    ''' DataContent a chat request actually sends. Downscaling before saving -
    ''' not just before sending - means a re-opened session's persisted copy
    ''' is already the small version, so nothing needs to reprocess it again
    ''' on every reload.
    '''
    ''' Uses WPF's own imaging stack (BitmapDecoder/TransformedBitmap/
    ''' JpegBitmapEncoder), not System.Drawing - this is already a WPF app, so
    ''' no extra dependency, and System.Drawing.Common has known cross-
    ''' platform/GDI+ caveats this avoids entirely.
    ''' </summary>
    Public Class ImageAttachmentService

        Public ReadOnly Property SupportedExtensions As String() = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"}

        ' 1568px longest edge - the point past which most vision models stop
        ' extracting additional useful detail from an image (a widely-cited
        ' rule of thumb for vision-model inputs generally, not a Lemonade-
        ' specific documented limit), so resizing beyond this trades context/
        ' token cost for detail the model wouldn't use anyway.
        Private Const MaxLongestEdge = 1568
        Private Const JpegQuality = 85

        Private ReadOnly _imagesFolder As String

        Public Sub New(settings As AppSettings)
            _imagesFolder = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "AttachedImages")
        End Sub

        Public Function IsSupported(filePath As String) As Boolean
            Return SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        ''' <summary>Downscales (if needed) and re-encodes as JPEG, saving a fresh copy under AttachedImages. Returns the new copy's full local path.</summary>
        Public Function SaveResizedCopy(sourceFilePath As String) As String
            Dim frame As BitmapFrame
            Using stream = File.OpenRead(sourceFilePath)
                frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames(0)
            End Using

            Dim longestEdge = Math.Max(frame.PixelWidth, frame.PixelHeight)
            Dim source As BitmapSource = frame
            If longestEdge > MaxLongestEdge Then
                Dim scale = MaxLongestEdge / CDbl(longestEdge)
                source = New TransformedBitmap(frame, New ScaleTransform(scale, scale))
            End If

            Directory.CreateDirectory(_imagesFolder)
            Dim fileName = $"img_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.jpg"
            Dim fullPath = Path.Combine(_imagesFolder, fileName)

            Dim encoder As New JpegBitmapEncoder With {.QualityLevel = JpegQuality}
            encoder.Frames.Add(BitmapFrame.Create(source))
            Using outputStream = File.Create(fullPath)
                encoder.Save(outputStream)
            End Using

            Return fullPath
        End Function

        ''' <summary>Reads a previously-saved copy's bytes and wraps them as the multi-modal content a chat message actually sends - shared by the live-send path and by LoadSession restoring an older turn's image.</summary>
        Public Function BuildDataContent(imagePath As String) As DataContent
            Return New DataContent(File.ReadAllBytes(imagePath), "image/jpeg")
        End Function

        ''' <summary>Parses the "[Attached image: {path}]" + blank line + question marker MainViewModel.SendAsync persists back into the image's local path and the remaining question text. Returns (Nothing, content unchanged) when content doesn't start with the marker at all. Shared, not instance state, since MainViewModel.LoadSession and ChatMarkdownExporter both need it and neither needs an image folder resolved to use it.</summary>
        Public Shared Function TryParseAttachedImageMarker(content As String) As (ImagePath As String, RemainingText As String)
            Const prefix = "[Attached image: "
            If Not content.StartsWith(prefix) Then Return (Nothing, content)

            Dim closingBracketIndex = content.IndexOf("]"c)
            If closingBracketIndex < 0 Then Return (Nothing, content)

            Dim imagePath = content.Substring(prefix.Length, closingBracketIndex - prefix.Length)
            Dim remainingText = content.Substring(closingBracketIndex + 1).TrimStart(vbCr, vbLf)
            Return (imagePath, remainingText)
        End Function

    End Class

End Namespace
