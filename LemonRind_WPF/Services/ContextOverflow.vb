Namespace Services

    ''' <summary>
    ''' Recognises the error Lemonade gives when a request is longer than the model's context window
    ''' ("request (N tokens) exceeds the available context size (M tokens)"). The chat then folds the older messages into a
    ''' summary and asks again once, instead of just reporting the failure.
    ''' </summary>
    Public Module ContextOverflow

        Private Const Marker As String = "exceeds the available context size"

        ''' <summary>True when an error text already contains the server's wording.</summary>
        Public Function IsOverflowText(text As String) As Boolean
            Return text IsNot Nothing AndAlso text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        Public Function IsOverflow(failure As Exception) As Boolean
            Dim current As Exception = failure
            While current IsNot Nothing
                If current.Message.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True

                ' The SDK's own message is only "Service request failed. Status: 400"; the server's reason lives in the body.
                Dim clientError = TryCast(current, System.ClientModel.ClientResultException)
                If clientError IsNot Nothing Then
                    Try
                        Dim body = clientError.GetRawResponse()?.Content?.ToString()
                        If body IsNot Nothing AndAlso body.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
                    Catch
                        ' No readable body: treat it as some other error.
                    End Try
                End If
                current = current.InnerException
            End While
            Return False
        End Function

    End Module

End Namespace
