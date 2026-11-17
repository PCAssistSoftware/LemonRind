Namespace Mcp

    ''' <summary>Where Lemonade's own MCP tools are and how to reach them - see LemonadeGateway.Describe.</summary>
    Public Class LemonadeGatewayAddress
        Public Property Url As String
        Public Property Headers As Dictionary(Of String, String)
    End Class

    ''' <summary>
    ''' Lemonade serves a few tools of its own over MCP (speech to text, picture drawing, listing and asking its models).
    ''' They live at /mcp on the same host and port as the chat address (which ends in /v1/), so the address is worked out
    ''' from the one already set in Settings, and the chat key is sent along if there is one.
    ''' </summary>
    Public Module LemonadeGateway

        Public Const ServerName As String = "lemonade"

        ''' <summary>The gateway's address and headers, or Nothing when the chat address is not a web address.</summary>
        Public Function Describe(baseUrl As String, apiKey As String) As LemonadeGatewayAddress
            Dim address As Uri = Nothing
            If Not Uri.TryCreate(baseUrl, UriKind.Absolute, address) OrElse (address.Scheme <> "http" AndAlso address.Scheme <> "https") Then Return Nothing
            Dim headers As New Dictionary(Of String, String)
            If Not String.IsNullOrWhiteSpace(apiKey) Then headers("Authorization") = $"Bearer {apiKey.Trim()}"
            Return New LemonadeGatewayAddress With {.Url = $"{address.GetLeftPart(UriPartial.Authority)}/mcp", .Headers = headers}
        End Function

    End Module

End Namespace
