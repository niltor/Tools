using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class PortScanner
{
    private static readonly Regex TitleRegex = new("<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _host;
    private readonly int _startPort;
    private readonly int _endPort;
    private readonly TimeSpan _timeout;
    private readonly int _maxParallelism;

    public PortScanner(string host, int startPort, int endPort, TimeSpan timeout, int maxParallelism)
    {
        _host = host;
        _startPort = startPort;
        _endPort = endPort;
        _timeout = timeout;
        _maxParallelism = Math.Max(1, maxParallelism);
    }

    public async Task<IReadOnlyList<PortScanResult>> ScanAsync(
        Action<int>? onPortScanned = null,
        Action<PortScanResult>? onOpenPort = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PortScanResult>();
        var syncRoot = new object();

        await Parallel.ForEachAsync(
            Enumerable.Range(_startPort, _endPort - _startPort + 1),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken
            },
            async (port, ct) =>
            {
                try
                {
                    if (!await IsPortOpenAsync(_host, port, _timeout, ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    var webInfo = await ProbeWebAsync(_host, port, _timeout, ct).ConfigureAwait(false);
                    var result = new PortScanResult(port, webInfo.IsWeb, webInfo.Scheme, webInfo.StatusCode, webInfo.Server, webInfo.Title);

                    lock (syncRoot)
                    {
                        results.Add(result);
                    }

                    onOpenPort?.Invoke(result);
                }

                finally
                {
                    onPortScanned?.Invoke(port);
                }
            }).ConfigureAwait(false);

        return results;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<WebProbeResult> ProbeWebAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var scheme in GetPreferredSchemes(port))
        {
            var result = await TryProbeSchemeAsync(host, port, scheme, timeout, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return WebProbeResult.NotWeb;
    }

    private static IEnumerable<string> GetPreferredSchemes(int port)
    {
        if (port is 443 or 8443 or 9443 or 10443)
        {
            yield return "https";
            yield return "http";
            yield break;
        }

        yield return "http";
        yield return "https";
    }

    private static async Task<WebProbeResult?> TryProbeSchemeAsync(string host, int port, string scheme, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("PortScan/1.0");

            var uri = new UriBuilder(scheme, host, port, "/").Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            string? server = response.Headers.Server.ToString();
            string? title = null;

            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            {
                title = await TryReadTitleAsync(response.Content, timeout, cancellationToken).ConfigureAwait(false);
            }

            return new WebProbeResult(true, scheme, response.StatusCode, server, title);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadTitleAsync(HttpContent content, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

        var buffer = new char[4096];
        var read = await reader.ReadAsync(buffer, 0, buffer.Length).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (read <= 0)
        {
            return null;
        }

        var snippet = new string(buffer, 0, read);
        var match = TitleRegex.Match(snippet);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()) : null;
    }

    private sealed record WebProbeResult(bool IsWeb, string? Scheme, HttpStatusCode? StatusCode, string? Server, string? Title)
    {
        public static WebProbeResult NotWeb { get; } = new(false, null, null, null, null);
    }
}