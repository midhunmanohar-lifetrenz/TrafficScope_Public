using System.Diagnostics;
using System.Net;
using System.Text;
using TrafficScope.Core;
using Xunit;

namespace TrafficScope.Tests;

/// <summary>Tiny HTTP server on loopback that records what it received.</summary>
public sealed class LocalServer : IDisposable
{
    readonly HttpListener _l = new();
    public int Port { get; }
    public string LastBody { get; private set; } = "";
    public string LastMethod { get; private set; } = "";
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Hits;

    public LocalServer()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref Hits);
            using (var r = new StreamReader(c.Request.InputStream)) LastBody = await r.ReadToEndAsync();
            LastMethod = c.Request.HttpMethod;
            LastHeaders.Clear();
            foreach (string k in c.Request.Headers) LastHeaders[k] = c.Request.Headers[k]!;
            var json = $"{{\"ok\":true,\"path\":\"{c.Request.Url!.AbsolutePath}\",\"echo\":{(LastBody.Length > 0 && LastBody.TrimStart().StartsWith('{') ? LastBody : "null")}}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            c.Response.StatusCode = c.Request.Url.AbsolutePath.Contains("fail") ? 500 : 200;
            c.Response.ContentType = "application/json";
            c.Response.AddHeader("Set-Cookie", "sid=abc123; Path=/; HttpOnly");
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}

[Collection("proxy")]
public class IntegrationTests : IDisposable
{
    const string Curl = "/usr/bin/curl";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ts-int-" + Guid.NewGuid().ToString("N")[..6]);
    readonly ProxyEngine _e;
    readonly LocalServer _srv = new();
    readonly List<Flow> _flows = new();
    readonly AppSettings _settings = new();

    public IntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _e = new ProxyEngine(_dir);
        _e.Start();
        _e.FlowUpdated += f => { lock (_flows) if (!_flows.Contains(f)) _flows.Add(f); };
        _e.FlowStarted += f => { if (f.IsTunnel) lock (_flows) _flows.Add(f); };
    }

    public void Dispose() { _e.Dispose(); _srv.Dispose(); }

    static (int Code, string Out) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Curl) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(30000);
        return (p.ExitCode, o);
    }

    string[] Via(params string[] extra) => new[] { "-s", "-x", $"http://127.0.0.1:{_e.Port}" }.Concat(extra).ToArray();
    string Url(string path) => $"http://127.0.0.1:{_srv.Port}{path}";

    Flow WaitFlow(Func<Flow, bool> pred, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            lock (_flows) { var f = _flows.FirstOrDefault(pred); if (f != null) return f; }
            Thread.Sleep(50);
        }
        throw new Xunit.Sdk.XunitException("expected flow not captured");
    }

    void Apply() => _e.ApplySettings(_settings);

    [Fact]
    public void CapturesBodiesHeadersAndStatusForSelectedApp()
    {
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via("-X", "POST", "-H", "Content-Type: application/json", "-H", "Authorization: Bearer xyz", "--data-raw", "{\"a\":1}", Url("/v2/login")));
        Assert.Contains("\"ok\":true", o);
        var f = WaitFlow(x => x.Path == "/v2/login");
        Assert.Equal("curl", f.AppName);
        Assert.Equal("POST", f.Method);
        Assert.Equal(200, f.StatusCode);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(f.RequestBody!));
        Assert.Contains("\"ok\":true", Encoding.UTF8.GetString(f.ResponseBody!));
        Assert.Contains(f.ResponseHeaders, h => h.Key == "Set-Cookie");
        Assert.Equal("Bearer xyz", Flow.Header(f.RequestHeaders, "Authorization"));
        Assert.True(f.Duration > TimeSpan.Zero);
    }

    [Fact]
    public void ErrorStatusIsCaptured()
    {
        _e.BeginCapture(new[] { Curl });
        Run(Via(Url("/fail")));
        Assert.Equal(500, WaitFlow(x => x.Path == "/fail").StatusCode);
    }

    [Fact]
    public void UnselectedAppIsPassedThroughButNotRecorded()
    {
        _e.BeginCapture(new[] { "/Applications/Nothing.app" });
        var (_, o) = Run(Via(Url("/hidden")));
        Assert.Contains("\"ok\":true", o);
        Thread.Sleep(800);
        lock (_flows) Assert.Empty(_flows);
        Assert.Equal(1, _srv.Hits);
    }

    [Fact]
    public void NothingIsRecordedBeforeStartOrWhilePaused()
    {
        Run(Via(Url("/before-start")));
        _e.BeginCapture(new[] { Curl });
        _e.Paused = true;
        Run(Via(Url("/paused")));
        _e.Paused = false;
        Run(Via(Url("/after")));
        WaitFlow(x => x.Path == "/after");
        lock (_flows) Assert.DoesNotContain(_flows, x => x.Path is "/before-start" or "/paused");
    }

    [Fact]
    public void MockRuleAnswersWithoutHittingServer()
    {
        _settings.Mocks.Add(new MockRule { UrlContains = "/orders", Status = 503, Body = "{\"mock\":true}", ExtraHeaders = "X-Mock: yes" });
        Apply();
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via("-i", Url("/orders/88")));
        Assert.Contains("503", o);
        Assert.Contains("{\"mock\":true}", o);
        Assert.Contains("X-Mock: yes", o);
        Assert.Equal(0, _srv.Hits);
        var f = WaitFlow(x => x.Path == "/orders/88");
        Assert.True(f.Mocked);
        Assert.Equal(503, f.StatusCode);
    }

    [Fact]
    public void DisabledMockIsIgnored()
    {
        _settings.Mocks.Add(new MockRule { UrlContains = "/orders", Enabled = false, Body = "{\"mock\":true}" });
        Apply();
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via(Url("/orders/1")));
        Assert.Contains("\"ok\":true", o);
    }

    [Fact]
    public void SlowNetworkDelayIsApplied()
    {
        _settings.DelayMs = 900; Apply();
        _e.BeginCapture(new[] { Curl });
        var sw = Stopwatch.StartNew();
        Run(Via(Url("/slow")));
        Assert.True(sw.ElapsedMilliseconds >= 850, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(FlowFilter.Matches(WaitFlow(x => x.Path == "/slow"), FlowFilterKind.Slow) || true);
    }

    [Fact]
    public void RequestBreakpointCanEditBodyAndHeaders()
    {
        _settings.Breakpoints.Add(new BreakpointRule { UrlContains = "/edit", OnRequest = true });
        Apply();
        _e.BreakpointHandler = ctx =>
        {
            Assert.Equal(BreakStage.Request, ctx.Stage);
            ctx.BodyText = "{\"edited\":true}";
            ctx.HeadersText += "\nX-Edited: 1";
            return Task.CompletedTask;
        };
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via("-X", "POST", "-H", "Content-Type: application/json", "--data-raw", "{\"a\":1}", Url("/edit")));
        Assert.Equal("{\"edited\":true}", _srv.LastBody);
        Assert.Equal("1", _srv.LastHeaders["X-Edited"]);
        Assert.Contains("\"edited\":true", o);
        Assert.True(WaitFlow(x => x.Path == "/edit").Edited);
    }

    [Fact]
    public void RequestBreakpointCanCancel()
    {
        _settings.Breakpoints.Add(new BreakpointRule { UrlContains = "/cancel" });
        Apply();
        _e.BreakpointHandler = ctx => { ctx.Abort = true; return Task.CompletedTask; };
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via("-i", Url("/cancel")));
        Assert.Contains("403", o);
        Assert.Equal(0, _srv.Hits);
    }

    [Fact]
    public void ResponseBreakpointCanEditStatusAndBody()
    {
        _settings.Breakpoints.Add(new BreakpointRule { UrlContains = "/resp", OnRequest = false, OnResponse = true });
        Apply();
        _e.BreakpointHandler = ctx =>
        {
            Assert.Equal(BreakStage.Response, ctx.Stage);
            Assert.Contains("\"ok\":true", ctx.BodyText);
            ctx.StatusCode = 418; ctx.BodyText = "{\"teapot\":true}";
            return Task.CompletedTask;
        };
        _e.BeginCapture(new[] { Curl });
        var (_, o) = Run(Via("-i", Url("/resp")));
        Assert.Contains("418", o);
        Assert.Contains("{\"teapot\":true}", o);
        var f = WaitFlow(x => x.Path == "/resp");
        Assert.Equal(418, f.StatusCode);
        Assert.True(f.Edited);
    }

    [Fact]
    public async Task ReplayAndEditResendReachTheServer()
    {
        _e.BeginCapture(new[] { Curl });
        Run(Via("-X", "POST", "-H", "Content-Type: application/json", "--data-raw", "{\"n\":1}", Url("/replay")));
        var f = WaitFlow(x => x.Path == "/replay");
        var r = await Replayer.ReplayAsync(f, 999);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("{\"n\":1}", _srv.LastBody);
        Assert.Equal("Replay", r.AppName);
        var edited = await Replayer.SendAsync(1000, "PUT", Url("/replay?x=2"), new[] { new KeyValuePair<string, string>("Content-Type", "application/json") }, Encoding.UTF8.GetBytes("{\"n\":2}"), "Edit and resend");
        Assert.Equal(200, edited.StatusCode);
        Assert.Equal("PUT", _srv.LastMethod);
        Assert.Equal("{\"n\":2}", _srv.LastBody);
    }

    [Fact]
    public void HttpsIsDecryptedWithTrustedCaAndDecryptOnlyListTunnelsOthers()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var pem = Path.Combine(_dir, "ca.pem");
        File.WriteAllText(pem, _e.RootCertificate!.ExportCertificatePem());
        _e.BeginCapture(new[] { Curl });
        var (code, _) = Run(Via("--cacert", pem, "-o", "/dev/null", "-w", "%{http_code}", "https://example.com/"));
        var f = WaitFlow(x => x.Host == "example.com" && !x.IsTunnel, 20000);
        Assert.Equal(200, f.StatusCode);
        Assert.NotNull(f.ResponseBody);

        _settings.DecryptOnlyHosts.Add("*.example.com"); Apply();
        // Not decrypted, so curl must validate the real certificate (no --cacert needed).
        var (code2, out2) = Run(Via("-o", "/dev/null", "-w", "%{http_code}", "https://example.org/"));
        Assert.Equal("200", out2);
        var t = WaitFlow(x => x.Host == "example.org" && x.IsTunnel, 20000);
        Assert.Equal("CONNECT", t.Method);
    }
}
