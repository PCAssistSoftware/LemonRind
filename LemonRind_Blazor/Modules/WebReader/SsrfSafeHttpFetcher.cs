using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LemonRindBlazor.Modules.WebReader;

/// <summary>
/// Fetches a URL while refusing to touch anything on the local network -
/// resolves the hostname and validates the ACTUAL resolved IP, not just the
/// hostname string.
///
/// The naive version of this check - resolve, validate, then let the HTTP
/// stack resolve AGAIN when it actually connects - is vulnerable to DNS
/// rebinding (the second lookup can return a different, unsafe IP). This
/// fixes that by using SocketsHttpHandler.ConnectCallback to connect
/// directly to the one IP that was already validated, so there's no second
/// lookup to rebind. TLS still validates the original hostname normally
/// (SNI/certificate checks use the request URI, not the IP the callback
/// happened to connect to) - this is a documented, supported use of
/// ConnectCallback, not a workaround.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\WebReader\SsrfSafeHttpFetcher.vb,
/// including its IPv6-refused-outright fix and connect timeout (both found
/// through live testing, not code review).
/// </summary>
public class SsrfSafeHttpFetcher
{
    private const int MaxRedirects = 5;
    private const long MaxResponseBytes = 5_000_000; // 5 MB - a reasonable cap for a text-focused reader, not a general-purpose downloader.

    private readonly HttpClient _httpClient;

    public SsrfSafeHttpFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects are followed manually below, so every hop re-validates its own IP.
            ConnectCallback = ConnectToValidatedAddressAsync,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LemonRind/1.0 (local desktop assistant)");
    }

    /// <summary>
    /// Fetches the page at url, following redirects (each hop re-validated)
    /// up to MaxRedirects. Throws InvalidOperationException with a
    /// caller-safe message if the URL, any redirect target, or the resolved
    /// address is refused.
    /// </summary>
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var currentUrl = url;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            var uri = ValidateUrlScheme(currentUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Server sent a redirect with no Location header.");
                currentUrl = new Uri(uri, location).ToString();
                continue;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var limitedStream = new LimitedStream(stream, MaxResponseBytes);
            using var reader = new StreamReader(limitedStream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        throw new InvalidOperationException($"Too many redirects (over {MaxRedirects}).");
    }

    private static Uri ValidateUrlScheme(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"'{url}' isn't a valid absolute URL.");
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Only http/https URLs are allowed, not '{uri.Scheme}'.");
        }
        return uri;
    }

    /// <summary>
    /// The actual SSRF check: resolves the target host, refuses to proceed
    /// if every resolved address is private/loopback/link-local/reserved,
    /// and connects directly to the first safe one found.
    /// </summary>
    private async ValueTask<Stream> ConnectToValidatedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var safeAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsPrivateOrReservedIPv4(a));

        if (safeAddress is null)
        {
            throw new InvalidOperationException($"Refusing to fetch '{context.DnsEndPoint.Host}': no public IPv4 address found (private/reserved addresses and all IPv6 addresses are refused).");
        }

        // A second, defense-in-depth timeout independent of the caller's own
        // cancellationToken - a "safe" but unreachable/firewalled address
        // would otherwise hang for the OS's default TCP connect timeout,
        // which can be a minute or more.
        using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeoutCts.Token);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(safeAddress, context.DnsEndPoint.Port), linkedCts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (connectTimeoutCts.IsCancellationRequested)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Timed out connecting to '{context.DnsEndPoint.Host}'.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True for loopback, link-local, private (RFC 1918), carrier-grade NAT,
    /// and multicast/broadcast IPv4 ranges. IPv4 only - IPv6 is refused
    /// outright above, since "is this address actually local" isn't
    /// reliably decidable from an IPv6 address alone the way it is for
    /// IPv4's reserved ranges (a host can resolve to both a blocked private
    /// IPv4 AND a globally-routable IPv6 address that still points at the
    /// local LAN).
    /// </summary>
    private static bool IsPrivateOrReservedIPv4(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true, // 10.0.0.0/8
            127 => true, // 127.0.0.0/8 (also covered by IsLoopback, kept for clarity)
            169 => bytes[1] == 254, // 169.254.0.0/16 (link-local)
            172 => bytes[1] is >= 16 and <= 31, // 172.16.0.0/12
            192 => bytes[1] == 168, // 192.168.0.0/16
            100 => bytes[1] is >= 64 and <= 127, // 100.64.0.0/10 (carrier-grade NAT)
            0 => true, // 0.0.0.0/8
            _ => bytes[0] >= 224, // 224.0.0.0/4 multicast, 255.255.255.255 broadcast
        };
    }
}
