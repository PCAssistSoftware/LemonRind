Imports System.IO
Imports System.Threading
Imports System.Windows
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration

Namespace Modules.FileSystem

    ''' <summary>
    ''' Read/write access to explicitly configured folders
    ''' (AppSettings.FileSystem.AllowedRoots), via PathSandbox. Reads are
    ''' unguarded (sandboxed, but no confirmation needed); writes go through
    ''' FileWriteApprovalDialog first.
    ''' </summary>
    Public Class FileSystemModule
        Implements IAssistantModule

        Private ReadOnly _sandbox As PathSandbox
        Private ReadOnly _approvalStore As FileWriteApprovalStore
        Private ReadOnly _moduleSettings As ModuleSettings

        Public Sub New(settings As AppSettings, sandbox As PathSandbox, approvalStore As FileWriteApprovalStore)
            _sandbox = sandbox
            _approvalStore = approvalStore
            _moduleSettings = settings.Modules
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "File system"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "FileSystem"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Reads and writes files in specific, pre-configured folders only."
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
            ' The real folder path(s) are baked directly into every tool's
            ' description - a static fact worth a few tokens on every
            ' request rather than a separate "where's the workspace" tool
            ' call. Without this, the model would have no way to answer
            ' "where is the workspace folder" even though it can read and
            ' write files there just fine.
            Dim rootsList = String.Join(", ", _sandbox.AllowedRoots)

            Return {
                AIFunctionFactory.Create(
                    method:=Function(path As String) ListDirectoryAsync(path),
                    name:="list_directory",
                    description:=$"Lists files and folders at a path inside the allowed workspace folder(s): {rootsList}. Pass """" for the root."),
                AIFunctionFactory.Create(
                    method:=Function(path As String) ReadFileAsync(path),
                    name:="read_file",
                    description:=$"Reads a text file's contents from inside the allowed workspace folder(s): {rootsList}."),
                AIFunctionFactory.Create(
                    method:=Function(path As String, content As String) WriteFileAsync(path, content),
                    name:="write_file",
                    description:=$"Writes (creating or overwriting) a text file inside the allowed workspace " &
                        $"folder(s): {rootsList}. The user is asked to approve every write before it happens.")
            }
        End Function

        Private Function ListDirectoryAsync(path As String) As Task(Of String)
            Try
                Dim fullPath = _sandbox.ResolveSafePath(path)
                If Not Directory.Exists(fullPath) Then
                    Return Task.FromResult($"'{path}' isn't a folder.")
                End If

                Dim entries As New List(Of String)
                entries.AddRange(Directory.GetDirectories(fullPath).Select(Function(d) $"[dir]  {IO.Path.GetFileName(d)}"))
                entries.AddRange(Directory.GetFiles(fullPath).Select(Function(f) $"[file] {IO.Path.GetFileName(f)}"))

                Return Task.FromResult(If(entries.Count = 0, "(empty)", String.Join(Environment.NewLine, entries)))
            Catch ex As Exception
                Return Task.FromResult($"Couldn't list that folder: {ex.Message}")
            End Try
        End Function

        Private Async Function ReadFileAsync(path As String) As Task(Of String)
            Try
                Dim fullPath = _sandbox.ResolveSafePath(path)
                If Not File.Exists(fullPath) Then
                    Return $"'{path}' doesn't exist."
                End If
                Return Await File.ReadAllTextAsync(fullPath)
            Catch ex As Exception
                Return $"Couldn't read that file: {ex.Message}"
            End Try
        End Function

        Private Async Function WriteFileAsync(path As String, content As String) As Task(Of String)
            Dim fullPath As String
            Try
                fullPath = _sandbox.ResolveSafePath(path)
            Catch ex As Exception
                Return $"Couldn't write that file: {ex.Message}"
            End Try

            Dim folder = IO.Path.GetDirectoryName(fullPath)
            If Not _approvalStore.IsPreApproved(folder) Then
                Dim approval = ShowApprovalDialog(fullPath, content)

                Select Case approval
                    Case FileWriteApprovalDialog.ApprovalResult.Deny
                        Return "The user denied this write - the file was not changed."
                    Case FileWriteApprovalDialog.ApprovalResult.AllowForSession
                        _approvalStore.ApproveFolderForSession(folder)
                End Select
            End If

            Try
                Await File.WriteAllTextAsync(fullPath, content)
                Return $"Wrote {content.Length} characters to '{path}'."
            Catch ex As Exception
                Return $"Couldn't write that file: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Marshals showing the dialog onto the UI thread and blocks until
        ''' the user answers. Dispatcher.Invoke is safe here even if this is
        ''' already running on the UI thread (e.g. because the whole chat
        ''' pipeline resumed there after the last await, via the captured
        ''' SynchronizationContext) - WPF runs the delegate immediately in
        ''' that case rather than deadlocking.
        ''' </summary>
        Private Function ShowApprovalDialog(fullPath As String, content As String) As FileWriteApprovalDialog.ApprovalResult
            Return Application.Current.Dispatcher.Invoke(
                Function()
                    Dim preview = If(content.Length > 2000, content.Substring(0, 2000) & "... [truncated]", content)
                    Dim dialog As New FileWriteApprovalDialog(fullPath, preview) With {
                        .Owner = Application.Current.MainWindow
                    }
                    dialog.ShowDialog()
                    Return dialog.Result
                End Function)
        End Function

    End Class

End Namespace
