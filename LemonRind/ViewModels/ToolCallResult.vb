Imports CommunityToolkit.Mvvm.ComponentModel

Namespace ViewModels

    ''' <summary>
    ''' One tool call's real outcome, not just its name. Succeeded starts
    ''' Nothing (call made, no result yet) rather than defaulting to True -
    ''' an always-true checkmark before the real result exists would be
    ''' misleading, so showing nothing until the real result is known
    ''' matters, not just cosmetically.
    ''' </summary>
    Public Class ToolCallResult
        Inherits ObservableObject

        Public Property Name As String

        ''' <summary>Correlates a later FunctionResultContent back to this call - Microsoft.Extensions.AI gives both the same CallId.</summary>
        Public Property CallId As String

        Private _succeeded As Boolean?
        Public Property Succeeded As Boolean?
            Get
                Return _succeeded
            End Get
            Set(value As Boolean?)
                SetProperty(_succeeded, value)
            End Set
        End Property

        ''' <summary>The real exception message when Succeeded = False - shown as a ToolTip on the failure indicator, same "compact indicator + hover for detail" pattern as the Lemonade-unreachable fix.</summary>
        Private _errorMessage As String
        Public Property ErrorMessage As String
            Get
                Return _errorMessage
            End Get
            Set(value As String)
                SetProperty(_errorMessage, value)
            End Set
        End Property

        ''' <summary>"name:1" (succeeded), "name:0" (failed), "name:?" (call made, no result ever arrived - e.g. the turn was stopped mid-call), comma-separated - what actually gets persisted to Messages.ToolCalls.</summary>
        Public Shared Function SerializeForStorage(toolCalls As IEnumerable(Of ToolCallResult)) As String
            Return String.Join(",", toolCalls.Select(
                Function(t)
                    Dim flag = If(t.Succeeded.HasValue, If(t.Succeeded.Value, "1", "0"), "?")
                    Return $"{t.Name}:{flag}"
                End Function))
        End Function

        ''' <summary>Parses SerializeForStorage's format back into real objects - a bare name with no colon restores fine as Succeeded=Nothing, not an error.</summary>
        Public Shared Function ParseFromStorage(stored As String) As List(Of ToolCallResult)
            Dim results As New List(Of ToolCallResult)
            If String.IsNullOrEmpty(stored) Then Return results

            For Each entry In stored.Split(","c)
                Dim colonIndex = entry.LastIndexOf(":"c)
                If colonIndex < 0 Then
                    results.Add(New ToolCallResult With {.Name = entry})
                Else
                    Dim name = entry.Substring(0, colonIndex)
                    Dim flag = entry.Substring(colonIndex + 1)
                    Dim succeeded As Boolean? = If(flag = "1", True, If(flag = "0", False, CType(Nothing, Boolean?)))
                    results.Add(New ToolCallResult With {.Name = name, .Succeeded = succeeded})
                End If
            Next
            Return results
        End Function

    End Class

End Namespace
