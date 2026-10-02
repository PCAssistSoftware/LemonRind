Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Threading

Namespace Modules.WebReader

    ''' <summary>
    ''' Fetches a URL while refusing to touch anything on the local network -
    ''' resolves the hostname and validates the ACTUAL resolved IP, not just
    ''' the hostname string.
    '''
    ''' The naive version of this check - resolve, validate, then let the
    ''' HTTP stack resolve AGAIN when it actually connects - is vulnerable to
    ''' DNS rebinding (the second lookup can return a different, unsafe IP).
    ''' This fixes that by using SocketsHttpHandler.ConnectCallback to connect
    ''' directly to the one IP that was already validated, so there's no
    ''' second lookup to rebind. TLS still validates the original hostname
    ''' normally (SNI/certificate checks use the request URI, not the IP the
    ''' callback happened to connect to) - this is a documented, supported
    ''' use of ConnectCallback, not a workaround.
    ''' </summary>
    Public Class SsrfSafeHttpFetcher

        Private Const MaxRedirects = 5
        Private Const MaxResponseBytes = 5_000_000 ' 5 MB - a reasonable cap for a text-focused reader, not a general-purpose downloader.

        Private ReadOnly _httpClient As HttpClient

        Public Sub New()
            Dim handler As New SocketsHttpHandler With {
                .AllowAutoRedirect = False, ' redirects are followed manually below, so every hop re-validates its own IP.
                .ConnectCallback = AddressOf ConnectToValidatedAddressAsync
            }
            _httpClient = New HttpClient(handler) With {
                .Timeout = TimeSpan.FromSeconds(15)
            }
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LemonRind/1.0 (local desktop assistant)")
        End Sub

        ''' <summary>
        ''' Fetches the page at url, following redirects (each hop
        ''' re-validated) up to MaxRedirects. Throws InvalidOperationException
        ''' with a caller-safe message if the URL, any redirect target, or the
        ''' resolved address is refused - callers should catch this
        ''' specifically to return a clean message to the model rather than a
        ''' raw exception.
        ''' </summary>
        Public Async Function FetchAsync(url As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim currentUrl = url

            For redirectCount = 0 To MaxRedirects
                Dim uri = ValidateUrlScheme(currentUrl)

                Using request As New HttpRequestMessage(HttpMethod.Get, uri)
                    Using response = Await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        If response.StatusCode = HttpStatusCode.MovedPermanently OrElse
                           response.StatusCode = HttpStatusCode.Found OrElse
                           response.StatusCode = HttpStatusCode.SeeOther OrElse
                           response.StatusCode = HttpStatusCode.TemporaryRedirect OrElse
                           response.StatusCode = HttpStatusCode.PermanentRedirect Then
                            Dim location = response.Headers.Location
                            If location Is Nothing Then
                                Throw New InvalidOperationException("Server sent a redirect with no Location header.")
                            End If
                            currentUrl = New Uri(uri, location).ToString()
                            Continue For
                        End If

                        response.EnsureSuccessStatusCode()

                        Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken)
                            Using limitedStream As New LimitedStream(stream, MaxResponseBytes)
                                Using reader As New IO.StreamReader(limitedStream)
                                    Return Await reader.ReadToEndAsync(cancellationToken)
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
            Next

            Throw New InvalidOperationException($"Too many redirects (over {MaxRedirects}).")
        End Function

        Private Shared Function ValidateUrlScheme(url As String) As Uri
            Dim uri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, uri) Then
                Throw New InvalidOperationException($"'{url}' isn't a valid absolute URL.")
            End If
            If uri.Scheme <> Uri.UriSchemeHttp AndAlso uri.Scheme <> Uri.UriSchemeHttps Then
                Throw New InvalidOperationException($"Only http/https URLs are allowed, not '{uri.Scheme}'.")
            End If
            Return uri
        End Function

        ''' <summary>
        ''' The actual SSRF check: resolves the target host, refuses to
        ''' proceed if every resolved address is private/loopback/link-local/
        ''' reserved, and connects directly to the first safe one found -
        ''' see the class-level comment for why connecting directly (rather
        ''' than validating then letting the stack re-resolve) matters.
        '''
        ''' SocketsHttpHandler.ConnectCallback requires a ValueTask(Of Stream)
        ''' return type, but VB's Async modifier only supports Task/Task(Of T),
        ''' not ValueTask - this non-Async wrapper adapts an Async
        ''' Task(Of Stream) helper's result into a ValueTask.
        ''' </summary>
        Private Function ConnectToValidatedAddressAsync(context As SocketsHttpConnectionContext, cancellationToken As CancellationToken) As ValueTask(Of IO.Stream)
            Return New ValueTask(Of IO.Stream)(ConnectToValidatedAddressCoreAsync(context, cancellationToken))
        End Function

        ''' <summary>
        ''' A naive "block the well-known private ranges" check isn't enough
        ''' on its own: a host can resolve to both a private IPv4 (correctly
        ''' blocked) AND a globally-routable IPv6 address that ISP home
        ''' delegation hands out for the same local network - not link-local,
        ''' not unique-local (fc00::/7), just a normal-looking public IPv6
        ''' address that still points at the local LAN. With IPv6, "is this
        ''' address actually local" isn't reliably decidable from the address
        ''' alone the way it is for IPv4's reserved ranges - so for this
        ''' tool's purpose (don't ever reach the user's own network), IPv6 is
        ''' refused outright rather than guessed at.
        ''' </summary>
        Private Async Function ConnectToValidatedAddressCoreAsync(context As SocketsHttpConnectionContext, cancellationToken As CancellationToken) As Task(Of IO.Stream)
            Dim addresses = Await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            Dim safeAddress = addresses.FirstOrDefault(Function(a) a.AddressFamily = AddressFamily.InterNetwork AndAlso Not IsPrivateOrReservedIPv4(a))

            If safeAddress Is Nothing Then
                Throw New InvalidOperationException($"Refusing to fetch '{context.DnsEndPoint.Host}': no public IPv4 address found (private/reserved addresses and all IPv6 addresses are refused).")
            End If

            ' A second, defense-in-depth timeout independent of the caller's
            ' own cancellationToken - a "safe" but unreachable/firewalled
            ' address (nothing listening, no RST sent) would otherwise hang
            ' for the OS's default TCP connect timeout, which can be a
            ' minute or more and make the whole tool call - and the chat
            ' turn using it - appear to lock up.
            Using connectTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                Using linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeoutCts.Token)
                    Dim socket As New Socket(SocketType.Stream, ProtocolType.Tcp) With {.NoDelay = True}
                    Try
                        Await socket.ConnectAsync(New IPEndPoint(safeAddress, context.DnsEndPoint.Port), linkedCts.Token)
                        Return New NetworkStream(socket, ownsSocket:=True)
                    Catch ex As OperationCanceledException When connectTimeoutCts.IsCancellationRequested
                        socket.Dispose()
                        Throw New InvalidOperationException($"Timed out connecting to '{context.DnsEndPoint.Host}'.")
                    Catch
                        socket.Dispose()
                        Throw
                    End Try
                End Using
            End Using
        End Function

        ''' <summary>
        ''' True for loopback, link-local, private (RFC 1918), carrier-grade
        ''' NAT, and multicast/broadcast IPv4 ranges - anything that could
        ''' point back at this machine or its local network rather than the
        ''' public internet. IPv4 only - see ConnectToValidatedAddressCoreAsync's
        ''' comment for why IPv6 is handled separately (refused outright).
        ''' </summary>
        Private Shared Function IsPrivateOrReservedIPv4(address As IPAddress) As Boolean
            If IPAddress.IsLoopback(address) Then Return True

            Dim bytes = address.GetAddressBytes()
            Select Case bytes(0)
                Case 10 : Return True ' 10.0.0.0/8
                Case 127 : Return True ' 127.0.0.0/8 (also covered by IsLoopback, kept for clarity)
                Case 169 : Return bytes(1) = 254 ' 169.254.0.0/16 (link-local)
                Case 172 : Return bytes(1) >= 16 AndAlso bytes(1) <= 31 ' 172.16.0.0/12
                Case 192 : Return bytes(1) = 168 ' 192.168.0.0/16
                Case 100 : Return bytes(1) >= 64 AndAlso bytes(1) <= 127 ' 100.64.0.0/10 (carrier-grade NAT)
                Case 0 : Return True ' 0.0.0.0/8
                Case Else
                    Return bytes(0) >= 224 ' 224.0.0.0/4 multicast, 255.255.255.255 broadcast
            End Select
        End Function

    End Class

End Namespace
