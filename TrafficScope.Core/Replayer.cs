using System.Diagnostics;
using System.Net;
using System.Text;

namespace TrafficScope.Core;

/// <summary>Sends a request directly (not through the proxy) for Replay and Edit and resend.</summary>
public static class Replayer
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Connection", "Proxy-Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Expect" };
    static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Content-Type", "Content-Encoding", "Content-Language", "Content-Disposition", "Content-MD5", "Content-Range", "Expires", "Last-Modified", "Allow" };

    static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<Flow> SendAsync(long id, string method, string url, IEnumerable<KeyValuePair<string, string>> headers, byte[]? body, string source)
    {
        var uri = new Uri(url);
        var flow = new Flow
        {
            Id = id, AppName = source, Source = source, Method = method, Url = url, Host = uri.Host, Path = uri.PathAndQuery,
            RequestHeaders = headers.ToList(), RequestBody = body,
        };
        var msg = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body is { Length: > 0 }) msg.Content = new ByteArrayContent(body);
        foreach (var h in headers)
        {
            if (Skip.Contains(h.Key) || h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (ContentHeaders.Contains(h.Key)) { msg.Content ??= new ByteArrayContent(Array.Empty<byte>()); msg.Content.Headers.TryAddWithoutValidation(h.Key, h.Value); }
            else msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await Client.SendAsync(msg);
            flow.ResponseBody = await res.Content.ReadAsByteArrayAsync();
            flow.StatusCode = (int)res.StatusCode;
            flow.StatusDescription = res.ReasonPhrase;
            flow.HttpVersion = "HTTP/" + res.Version;
            flow.ResponseHeaders = res.Headers.Concat(res.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToList();
        }
        catch (Exception ex) { flow.Error = ex.Message; }
        flow.Duration = sw.Elapsed;
        return flow;
    }

    public static Task<Flow> ReplayAsync(Flow f, long id) => SendAsync(id, f.Method, f.Url, f.RequestHeaders, f.RequestBody, "Replay");
}
