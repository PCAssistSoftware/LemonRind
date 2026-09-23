Imports System.Threading

Namespace Modules.FileSystem

    ''' <summary>
    ''' Remembers which folders the user picked "Allow for this session" for,
    ''' so a write to the same folder later in the same app run skips the
    ''' confirmation dialog. In-memory only, per app run - not persisted; a
    ''' persisted "always allow this folder" setting is a deliberate future
    ''' addition (a Settings screen), not something this store tries to do
    ''' yet.
    ''' </summary>
    Public Class FileWriteApprovalStore

        Private ReadOnly _approvedFolders As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' A scheduled job runs with nobody there to click the
        ''' approval dialog, so any write_file/Coder write it makes would
        ''' just hang until Scheduler's own 10-minute job timeout kills it.
        ''' AsyncLocal, not a plain field, deliberately - ScheduledJobRunner
        ''' sets this True only around ITS OWN job's tool-calling call chain,
        ''' and .NET's AsyncLocal correctly scopes that to just this one
        ''' async call tree (including every tool invocation nested inside
        ''' it) without leaking into a live chat's concurrent tool calls
        ''' running on the same thread pool at the same time - scheduled
        ''' jobs are auto-approved, as distinct from a persistent setting
        ''' that would also change live-chat behaviour, and this is what
        ''' actually delivers that distinction.
        ''' </summary>
        Private ReadOnly _isRunningScheduledJob As New AsyncLocal(Of Boolean)

        Public Property IsRunningScheduledJob As Boolean
            Get
                Return _isRunningScheduledJob.Value
            End Get
            Set(value As Boolean)
                _isRunningScheduledJob.Value = value
            End Set
        End Property

        Public Function IsPreApproved(folderPath As String) As Boolean
            Return IsRunningScheduledJob OrElse _approvedFolders.Contains(folderPath)
        End Function

        Public Sub ApproveFolderForSession(folderPath As String)
            _approvedFolders.Add(folderPath)
        End Sub

    End Class

End Namespace
