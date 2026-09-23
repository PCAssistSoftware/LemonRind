Imports System.IO
Imports LemonRind.Configuration

Namespace Modules.FileSystem

    ''' <summary>
    ''' Confines file access to the folders configured in
    ''' AppSettings.FileSystem.AllowedRoots. Every tool method in
    ''' FileSystemModule routes its path through ResolveSafePath before
    ''' touching disk - there's no other way into these folders from the
    ''' model's side.
    ''' </summary>
    Public Class PathSandbox

        Private ReadOnly _fileSystemSettings As FileSystemSettings

        Public Sub New(settings As AppSettings)
            _fileSystemSettings = settings.FileSystem
        End Sub

        ''' <summary>
        ''' A relative AllowedRoots entry (e.g. "data\Workspace") resolves
        ''' against AppContext.BaseDirectory, not Environment.CurrentDirectory -
        ''' Path.GetFullPath on its own resolves against the latter, which
        ''' can differ depending on how the app was launched. An absolute
        ''' entry is returned unchanged either way (Path.Combine with an
        ''' already-rooted second argument just returns that second
        ''' argument).
        ''' </summary>
        Private Shared Function ResolveRootPath(entry As String) As String
            Dim expanded = Environment.ExpandEnvironmentVariables(entry)
            Return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded)).TrimEnd(Path.DirectorySeparatorChar)
        End Function

        ''' <summary>
        ''' Resolved and directories-created fresh every call, not cached at
        ''' construction, so adding/removing an allowed folder in Settings
        ''' takes effect on the very next tool call, no app restart needed.
        ''' Cheap enough to redo every
        ''' time (a handful of string operations plus an idempotent
        ''' Directory.CreateDirectory per root - a no-op if it already
        ''' exists) for how infrequently this is actually called compared to
        ''' how often it would otherwise need to be invalidated.
        ''' </summary>
        Public ReadOnly Property AllowedRoots As IReadOnlyList(Of String)
            Get
                Dim resolvedRoots = _fileSystemSettings.AllowedRoots.Select(Function(r) ResolveRootPath(r)).ToList()
                For Each root In resolvedRoots
                    Directory.CreateDirectory(root)
                Next
                Return resolvedRoots
            End Get
        End Property

        ''' <summary>
        ''' Resolves a model-supplied path to a real full path, refusing it if
        ''' it falls outside every configured allowed root. A relative path is
        ''' resolved against the first configured root; an absolute path is
        ''' accepted only if it's still inside one of the roots after
        ''' normalization (blocks "..\..\" traversal tricks, since
        ''' Path.GetFullPath collapses those before the containment check
        ''' runs).
        ''' </summary>
        ''' <summary>
        ''' Local named currentAllowedRoots, not allowedRoots - VB is
        ''' case-insensitive, so a local differing from the AllowedRoots
        ''' property only by case would shadow it within its own
        ''' initializer, breaking type inference.
        ''' </summary>
        Public Function ResolveSafePath(requestedPath As String) As String
            Dim currentAllowedRoots = AllowedRoots
            If currentAllowedRoots.Count = 0 Then
                Throw New InvalidOperationException("No folders are configured for file system access.")
            End If

            Dim candidate As String
            If Path.IsPathRooted(requestedPath) Then
                candidate = Path.GetFullPath(requestedPath)
            Else
                candidate = Path.GetFullPath(Path.Combine(currentAllowedRoots(0), requestedPath))
            End If

            Dim isInsideAnAllowedRoot = currentAllowedRoots.Any(
                Function(root) candidate.Equals(root, StringComparison.OrdinalIgnoreCase) OrElse
                               candidate.StartsWith(root & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

            If Not isInsideAnAllowedRoot Then
                Throw New InvalidOperationException($"'{requestedPath}' is outside the allowed folder(s).")
            End If

            Return candidate
        End Function

    End Class

End Namespace
