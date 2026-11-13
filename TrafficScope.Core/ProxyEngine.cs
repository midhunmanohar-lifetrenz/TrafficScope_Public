using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace TrafficScope.Core;

public enum BreakStage { Request, Response }

/// <summary>Editable snapshot handed to the UI when a breakpoint pauses a flow.</summary>
public sealed class BreakpointContext
{
    public required Flow Flow { get; init; }
    public required BreakStage Stage { get; init; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int StatusCode { get; set; }
    public string HeadersText { get; set; } = "";
    public string BodyText { get; set; } = "";
    public bool Abort { get; set; }
}

public sealed class ProxyEngine : IDisposable
{
    const int MaxBody = 8 * 1024 * 1024;
    readonly ProxyServer _proxy;
    readonly IProcessResolver _resolver = ProcessResolver.Create();
    readonly string _dataDir;
    ExplicitProxyEndPoint? _endpoint;
    long _nextId;
    long _ignored;
    volatile string[] _selected = Array.Empty<string>();
    volatile bool _capturing;
    volatile MockRule[] _mocks = Array.Empty<MockRule>();
    volatile BreakpointRule[] _breakpoints = Array.Empty<BreakpointRule>();
    volatile MapRule[] _maps = Array.Empty<MapRule>();
    volatile string[] _decryptOnly = Array.Empty<string>();
    volatile int _delayMs;

    public int Port { get; private set; }
    public bool IsRunning => _proxy.ProxyRunning;
    public bool Paused { get; set; }
    public long IgnoredConnections => Interlocked.Read(ref _ignored);
    public DateTime? CaptureStartedAt { get; private set; }
    public event Action<Flow>? FlowStarted;
    public event Action<Flow>? FlowUpdated;
    public event Action<string>? Log;
    /// <summary>Set by the UI; awaited while a breakpoint holds the flow. Null = breakpoints are skipped.</summary>
    public Func<BreakpointContext, Task>? BreakpointHandler { get; set; }

    public ProxyEngine(string dataDir)
    {
        _proxy = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false, trustRootCertificateAsAdmin: false);
        _proxy.CertificateManager.RootCertificateName = CertificateService.RootName;
        _proxy.CertificateManager.RootCertificateIssuerName = "TrafficScope";
        _proxy.CertificateManager.PfxFilePath = Path.Combine(dataDir, "rootCert.pfx");
        // The key file is made owner-only and (in the real data folder) re-encrypted with a random password
        // kept in the Keychain / DPAPI, so other users and programs cannot copy the trusted root key.
        _dataDir = dataDir;
        _proxy.CertificateManager.PfxPassword = CertificateService.ProtectKey(dataDir);
        _proxy.CertificateManager.SaveFakeCertificates = false;
        _proxy.BeforeRequest += OnRequest;
        _proxy.BeforeResponse += OnResponse;
        _proxy.AfterResponse += OnAfterResponse;
        _proxy.ServerCertificateValidationCallback += (_, e) => { e.IsValid = e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None; return Task.CompletedTask; };
    }

    public System.Security.Cryptography.X509Certificates.X509Certificate2? RootCertificate => _proxy.CertificateManager.RootCertificate;
    public void EnsureRootCertificate()
    {
        _proxy.CertificateManager.EnsureRootCertificate();
        CertificateService.RestrictKeyFile(_dataDir);
    }

    public void Start(int port = 0)
    {
        if (_proxy.ProxyRunning) return;
        EnsureRootCertificate();
        _endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port, decryptSsl: true);
        _endpoint.BeforeTunnelConnectRequest += OnTunnelConnect;
        _proxy.AddEndPoint(_endpoint);
        _proxy.Start();
        Port = _endpoint.Port;
    }

    public void Stop()
    {
        if (!_proxy.ProxyRunning) return;
        _proxy.Stop();
        if (_endpoint != null) { _endpoint.BeforeTunnelConnectRequest -= OnTunnelConnect; _proxy.RemoveEndPoint(_endpoint); _endpoint = null; }
    }

    /// <summary>Begin recording; only flows that start after this call and belong to the given apps are kept.</summary>
    public void BeginCapture(IEnumerable<string> appKeys)
    {
        SetSelectedApps(appKeys);
        CaptureStartedAt = DateTime.Now;
        Interlocked.Exchange(ref _ignored, 0);
        _capturing = true;
    }

    public void EndCapture() => _capturing = false;
    public void SetSelectedApps(IEnumerable<string> appKeys) => _selected = appKeys.ToArray();

    public void ApplySettings(AppSettings s)
    {
        _mocks = s.Mocks.Where(m => m.Enabled).ToArray();
        _breakpoints = s.Breakpoints.Where(b => b.Enabled).ToArray();
        _maps = (s.Maps ?? new()).Where(m => m.Enabled).ToArray();
        _decryptOnly = s.DecryptOnlyHosts.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        _delayMs = Math.Max(0, s.DelayMs);
    }

    bool Wanted(ProcessInfo? p) => _capturing && !Paused && p != null && AppMatcher.MatchesAny(p.Path, _selected);

    async Task OnTunnelConnect(object sender, TunnelConnectSessionEventArgs e)
    {
        // Not recording (idle, paused, or passing traffic through after Stop): tunnel untouched and
        // skip the per-connection process lookup, which is the slow part.
        if (!_capturing || Paused) { e.DecryptSsl = false; return; }
        var proc = await _resolver.ResolveAsync(e.ClientRemoteEndPoint.Port, Port);
        if (!Wanted(proc))
        {
            // Apps you did not pick are tunnelled untouched: not decrypted, not recorded.
            e.DecryptSsl = false;
            Interlocked.Increment(ref _ignored);
            return;
        }
        var host = e.HttpClient.Request.RequestUri.Host;
        var only = _decryptOnly;
        if (only.Length > 0 && !HostPattern.MatchesAny(host, only))
        {
            e.DecryptSsl = false;
            var flow = new Flow
            {
                Id = Interlocked.Increment(ref _nextId), Process = proc, AppName = AppNameOf(proc!), Method = "CONNECT",
                Url = $"https://{host}:{e.HttpClient.Request.RequestUri.Port}", Host = host, Path = "", IsTunnel = true,
                StatusCode = 200, StatusDescription = "Tunnel (not decrypted)", Duration = TimeSpan.Zero
            };
            FlowStarted?.Invoke(flow);
        }
    }

    static string HeadersToText(IEnumerable<KeyValuePair<string, string>> h) => string.Join("\n", h.Select(x => $"{x.Key}: {x.Value}"));

    internal static List<KeyValuePair<string, string>> ParseHeaders(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(':'))
            .Select(l => { var i = l.IndexOf(':'); return new KeyValuePair<string, string>(l[..i].Trim(), l[(i + 1)..].Trim()); })
            .Where(kv => kv.Key.Length > 0).ToList();

    /// <summary>True for content worth holding in memory when its size is not known up front.</summary>
    public static bool IsTextType(string? contentType)
    {
        var ct = (contentType ?? "").ToLowerInvariant();
        return ct.Length == 0 || ct.StartsWith("text/") || ct.Contains("json") || ct.Contains("xml") || ct.Contains("javascript")
            || ct.Contains("x-www-form-urlencoded") || ct.Contains("html") || ct.Contains("graphql") || ct.StartsWith("image/");
    }

    /// <summary>
    /// Decides whether a body is read into memory. Bodies larger than the limit, and unknown-length
    /// bodies of binary types (downloads, video), stream straight through instead.
    /// </summary>
    public static string? SkipReason(long contentLength, string? contentType)
    {
        if (contentLength > MaxBody) return $"Body not captured: {Formatting.Size(contentLength)} is over the {MaxBody / (1024 * 1024)} MB limit. It was passed through unchanged.";
        if (contentLength < 0 && !IsTextType(contentType)) return "Body not captured: a binary stream of unknown size. It was passed through unchanged.";
        return null;
    }

    async Task OnRequest(object sender, SessionEventArgs e)
    {
        if (!_capturing || Paused) return;
        var proc = await _resolver.ResolveAsync(e.ClientRemoteEndPoint.Port, Port);
        if (!Wanted(proc)) { return; }
        var req = e.HttpClient.Request;
        var flow = new Flow
        {
            Id = Interlocked.Increment(ref _nextId),
            Process = proc,
            AppName = AppNameOf(proc!),
            Method = req.Method,
            Url = req.Url,
            Host = req.RequestUri.Host,
            Path = req.RequestUri.PathAndQuery,
            HttpVersion = "HTTP/" + req.HttpVersion,
            RequestHeaders = req.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(),
        };
        if (req.HasBody)
        {
            flow.RequestBodyNote = SkipReason(req.ContentLength, req.ContentType);
            if (flow.RequestBodyNote == null)
                try { var b = await e.GetRequestBody(); flow.RequestBody = b.Length > MaxBody ? b[..MaxBody] : b; } catch { }
        }
        e.UserData = flow;
        FlowStarted?.Invoke(flow);

        if (_delayMs > 0) await Task.Delay(_delayMs);

        var bp = _breakpoints.FirstOrDefault(b => b.OnRequest && b.Matches(flow.Method, flow.Url));
        if (bp != null && BreakpointHandler != null)
        {
            var ctx = new BreakpointContext
            {
                Flow = flow, Stage = BreakStage.Request, Method = flow.Method, Url = flow.Url,
                HeadersText = HeadersToText(flow.RequestHeaders),
                BodyText = flow.RequestBody == null ? "" : System.Text.Encoding.UTF8.GetString(flow.RequestBody),
            };
            await BreakpointHandler(ctx);
            if (ctx.Abort)
            {
                flow.Error = "Cancelled at breakpoint";
                e.GenericResponse("Request cancelled by TrafficScope breakpoint", HttpStatusCode.Forbidden, new Dictionary<string, Titanium.Web.Proxy.Models.HttpHeader>());
                CompleteSynthetic(flow, 403, "Forbidden", "text/plain", "Request cancelled by TrafficScope breakpoint");
                return;
            }
            ApplyRequestEdits(e, flow, ctx);
        }

        var mock = _mocks.FirstOrDefault(m => m.Matches(flow.Method, flow.Url));
        var map = mock != null ? null : _maps.FirstOrDefault(m => m.Matches(flow.Method, flow.Url));
        if (map is { IsRemote: false })
        {
            // Map Local: answer from a file; the server is not called.
            var file = map.LocalPath.Trim();
            flow.Mapped = "Local"; flow.MappedTo = file; flow.Mocked = true;
            byte[] bytes; int status; string type;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) throw new FileNotFoundException("File not found");
                if (info.Length > MaxBody) throw new IOException($"File is larger than {MaxBody / (1024 * 1024)} MB");
                bytes = await File.ReadAllBytesAsync(file); status = map.Status is >= 100 and <= 599 ? map.Status : 200; type = MapRule.ContentTypeFor(file);
            }
            catch (Exception ex)
            {
                bytes = System.Text.Encoding.UTF8.GetBytes($"TrafficScope Map Local could not read {file}: {ex.Message}"); status = 502; type = "text/plain; charset=utf-8";
            }
            e.GenericResponse(bytes, (HttpStatusCode)status, new Dictionary<string, HttpHeader> { ["Content-Type"] = new HttpHeader("Content-Type", type) });
            flow.StatusCode = status; flow.StatusDescription = ((HttpStatusCode)status).ToString();
            flow.Duration = DateTime.Now - flow.StartedAt;
            flow.ResponseHeaders = new() { new("Content-Type", type) };
            flow.ResponseBody = bytes;
            FlowUpdated?.Invoke(flow);
            flow.RaiseChanged();
            return;
        }
        if (map is { IsRemote: true })
        {
            // Map Remote: same request, different server. The list keeps showing the address the app asked for.
            var target = MapRule.Remap(flow.Url, map.RemoteUrl);
            req.Url = target;
            req.Host = req.RequestUri.Authority;
            flow.Mapped = "Remote"; flow.MappedTo = target;
            FlowUpdated?.Invoke(flow);
        }
        if (mock != null)
        {
            var headers = new Dictionary<string, HttpHeader> { ["Content-Type"] = new HttpHeader("Content-Type", mock.ContentType) };
            foreach (var kv in ParseHeaders(mock.ExtraHeaders)) headers[kv.Key] = new HttpHeader(kv.Key, kv.Value);
            flow.Mocked = true;
            e.GenericResponse(mock.Body ?? "", (HttpStatusCode)mock.Status, headers);
            CompleteSynthetic(flow, mock.Status, ((HttpStatusCode)mock.Status).ToString(), mock.ContentType, mock.Body ?? "",
                ParseHeaders(mock.ExtraHeaders));
        }
    }

    void ApplyRequestEdits(SessionEventArgs e, Flow flow, BreakpointContext ctx)
    {
        var req = e.HttpClient.Request;
        var newHeaders = ParseHeaders(ctx.HeadersText);
        var newBody = ctx.BodyText ?? "";
        var oldBody = flow.RequestBody == null ? "" : System.Text.Encoding.UTF8.GetString(flow.RequestBody);
        var changed = ctx.Method != flow.Method || ctx.Url != flow.Url || newBody != oldBody
                      || HeadersToText(newHeaders) != HeadersToText(flow.RequestHeaders);
        if (!changed) return;
        req.Method = ctx.Method;
        if (ctx.Url != flow.Url) req.Url = ctx.Url;
        foreach (var h in req.Headers.ToList()) req.Headers.RemoveHeader(h.Name);
        foreach (var kv in newHeaders) if (!kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) req.Headers.AddHeader(kv.Key, kv.Value);
        if (newBody.Length > 0 || req.HasBody) e.SetRequestBodyString(newBody);
        flow.Method = ctx.Method; flow.Url = req.Url; flow.Host = req.RequestUri.Host; flow.Path = req.RequestUri.PathAndQuery;
        flow.RequestHeaders = newHeaders;
        flow.RequestBody = System.Text.Encoding.UTF8.GetBytes(newBody);
        flow.Edited = true;
        FlowUpdated?.Invoke(flow);
    }

    void CompleteSynthetic(Flow flow, int status, string desc, string contentType, string body, List<KeyValuePair<string, string>>? extra = null)
    {
        flow.StatusCode = status;
        flow.StatusDescription = desc;
        flow.Duration = DateTime.Now - flow.StartedAt;
        flow.ResponseHeaders = new() { new("Content-Type", contentType) };
        if (extra != null) flow.ResponseHeaders.AddRange(extra);
        flow.ResponseBody = System.Text.Encoding.UTF8.GetBytes(body);
        FlowUpdated?.Invoke(flow);
        flow.RaiseChanged();
    }

    async Task OnResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is not Flow flow || flow.Mocked || flow.Error != null) return;
        var res = e.HttpClient.Response;
        flow.StatusCode = res.StatusCode;
        flow.StatusDescription = res.StatusDescription;
        flow.Duration = DateTime.Now - flow.StartedAt;
        flow.ResponseHeaders = res.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
        var ct = res.ContentType ?? "";
        if (res.HasBody && !ct.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            flow.ResponseBodyNote = SkipReason(res.ContentLength, ct);
            if (flow.ResponseBodyNote == null)
                try { var b = await e.GetResponseBody(); flow.ResponseBody = b.Length > MaxBody ? b[..MaxBody] : b; } catch { }
        }

        var bp = flow.ResponseBodyNote != null ? null : _breakpoints.FirstOrDefault(b => b.OnResponse && b.Matches(flow.Method, flow.Url));
        if (bp != null && BreakpointHandler != null)
        {
            var ctx = new BreakpointContext
            {
                Flow = flow, Stage = BreakStage.Response, Method = flow.Method, Url = flow.Url, StatusCode = flow.StatusCode ?? 0,
                HeadersText = HeadersToText(flow.ResponseHeaders),
                BodyText = flow.ResponseBody == null ? "" : System.Text.Encoding.UTF8.GetString(flow.ResponseBody),
            };
            FlowUpdated?.Invoke(flow);
            await BreakpointHandler(ctx);
            var oldBody = flow.ResponseBody == null ? "" : System.Text.Encoding.UTF8.GetString(flow.ResponseBody);
            var newHeaders = ParseHeaders(ctx.HeadersText);
            if (ctx.StatusCode != flow.StatusCode || ctx.BodyText != oldBody || HeadersToText(newHeaders) != HeadersToText(flow.ResponseHeaders))
            {
                res.StatusCode = ctx.StatusCode;
                foreach (var h in res.Headers.ToList()) res.Headers.RemoveHeader(h.Name);
                foreach (var kv in newHeaders)
                    if (!kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                        && !kv.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        res.Headers.AddHeader(kv.Key, kv.Value);
                e.SetResponseBodyString(ctx.BodyText ?? "");
                flow.StatusCode = ctx.StatusCode;
                flow.ResponseHeaders = newHeaders;
                flow.ResponseBody = System.Text.Encoding.UTF8.GetBytes(ctx.BodyText ?? "");
                flow.Edited = true;
            }
        }
        FlowUpdated?.Invoke(flow);
        flow.RaiseChanged();
    }

    /// <summary>Runs when a session ends, including when it failed: a flow with no response gets an error instead of staying pending.</summary>
    Task OnAfterResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is Flow flow && flow.StatusCode == null && flow.Error == null)
        {
            var ex = e.Exception;
            while (ex?.InnerException != null) ex = ex.InnerException;
            flow.Error = ex?.Message ?? "No response: the connection failed or was closed before the server answered.";
            flow.Duration = DateTime.Now - flow.StartedAt;
            FlowUpdated?.Invoke(flow);
            flow.RaiseChanged();
        }
        return Task.CompletedTask;
    }

    public long NextId() => Interlocked.Increment(ref _nextId);

    static string AppNameOf(ProcessInfo p)
    {
        var i = p.Path.IndexOf(".app/", StringComparison.Ordinal);
        return i > 0 ? Path.GetFileNameWithoutExtension(p.Path[..(i + 4)]) : Path.GetFileNameWithoutExtension(p.Path);
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _proxy.Dispose();
    }
}
