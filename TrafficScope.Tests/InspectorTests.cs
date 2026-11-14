using System.Text;
using System.Text.Json;
using TrafficScope.Core;
using Xunit;

namespace TrafficScope.Tests;

public class InspectorTests
{
    [Fact]
    public void FormBodyFromFiddlerScreenshotIsDecoded()
    {
        var f = FormParser.Parse("start%5Fdate=04%2F01%2F2026&appVersion=3%2E5%2E90&period%5Fid=42&site=12");
        Assert.Equal(new NameValue("start_date", "04/01/2026"), f[0]);
        Assert.Equal(new NameValue("appVersion", "3.5.90"), f[1]);
        Assert.Equal(4, f.Count);
        Assert.Equal("a b", FormParser.Parse("q=a+b")[0].Value);
    }

    [Fact]
    public void QueryStringIsParsed()
    {
        var q = FormParser.Query("https://x/api?page=1&name=J%C3%B6rg#frag");
        Assert.Equal(2, q.Count);
        Assert.Equal("Jörg", q[1].Value);
        Assert.Empty(FormParser.Query("https://x/api"));
    }

    [Fact]
    public void CookiesAreParsed()
    {
        var req = CookieParser.Request(new[] { new KeyValuePair<string, string>("Cookie", "a=1; b=two") });
        Assert.Equal(2, req.Count);
        var res = CookieParser.Response(new[] { new KeyValuePair<string, string>("Set-Cookie", "sid=abc; Path=/; HttpOnly") });
        Assert.Equal("sid", res[0].Name);
        Assert.Equal("Path=/; HttpOnly", res[0].Note);
    }

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void JwtIsDecoded()
    {
        var token = B64("{\"alg\":\"HS256\"}") + "." + B64("{\"sub\":\"7\",\"exp\":1893456000}") + ".sig";
        var j = JwtDecoder.FindInHeaders(new[] { new KeyValuePair<string, string>("Authorization", "Bearer " + token) });
        Assert.NotNull(j);
        Assert.Contains("HS256", j!.HeaderJson);
        Assert.Contains("\"sub\"", j.PayloadJson);
        Assert.Equal(2030, j.ExpiresUtc!.Value.Year);
        Assert.Null(JwtDecoder.TryDecode("Basic abc"));
    }

    [Fact]
    public void JsonTreeBuildsNestedNodes()
    {
        var n = JsonTree.Build(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":1,\"ok\":true}],\"n\":null}"))!;
        Assert.Equal("object", n.Kind);
        Assert.Equal("data", n.Children[0].Name);
        Assert.Equal("[0]", n.Children[0].Children[0].Name);
        Assert.Equal("1", n.Children[0].Children[0].Children[0].Value);
        Assert.Null(JsonTree.Build(Encoding.UTF8.GetBytes("not json")));
        Assert.True(JsonTree.LooksLikeJson(null, Encoding.UTF8.GetBytes(" [1]")));
    }

    [Fact]
    public void SecretsAreMaskedOnlyWhenAsked()
    {
        var f = new Flow { Method = "GET", Url = "https://a/b", Path = "/b" };
        f.RequestHeaders.Add(new("Authorization", "Bearer secret-token"));
        f.RequestHeaders.Add(new("Cookie", "sid=1"));
        f.RequestHeaders.Add(new("Accept", "*/*"));
        Assert.Contains("secret-token", Formatting.RawRequest(f));
        var masked = Formatting.RawRequest(f, true, mask: true);
        Assert.DoesNotContain("secret-token", masked);
        Assert.Contains("Bearer " + Redactor.Mask, masked);
        Assert.Contains("Accept: */*", masked);
        Assert.DoesNotContain("secret-token", CurlBuilder.Build(f, mask: true));
        Assert.Contains("secret-token", CurlBuilder.Build(f));
    }

    [Fact]
    public void HarIsValidAndMasksWhenAsked()
    {
        var f = new Flow { Method = "POST", Url = "https://a.test/x?y=1", Path = "/x?y=1", StatusCode = 200, StatusDescription = "OK", Duration = TimeSpan.FromMilliseconds(12) };
        f.RequestHeaders.Add(new("Authorization", "Bearer s3cret"));
        f.RequestHeaders.Add(new("Content-Type", "application/json"));
        f.RequestBody = Encoding.UTF8.GetBytes("{\"a\":1}");
        f.ResponseHeaders.Add(new("Content-Type", "image/png"));
        f.ResponseBody = new byte[] { 137, 80, 78, 71, 0 };
        var tunnel = new Flow { Method = "CONNECT", Url = "https://t", IsTunnel = true };
        var har = HarExporter.Build(new[] { f, tunnel }, mask: true);
        using var d = JsonDocument.Parse(har);
        var entries = d.RootElement.GetProperty("log").GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var e = entries[0];
        Assert.Equal("1", e.GetProperty("request").GetProperty("queryString")[0].GetProperty("value").GetString());
        Assert.Equal("base64", e.GetProperty("response").GetProperty("content").GetProperty("encoding").GetString());
        Assert.DoesNotContain("s3cret", har);
        Assert.Contains("s3cret", HarExporter.Build(new[] { f }, mask: false));
    }

    [Fact]
    public void LineDiffFindsChanges()
    {
        var d = LineDiff.Compare("a\nb\nc", "a\nB\nc\nd");
        Assert.Equal(new[] { DiffKind.Same, DiffKind.Removed, DiffKind.Added, DiffKind.Same, DiffKind.Added }, d.Select(x => x.Kind));
    }

    [Theory]
    [InlineData("api.example.com", "*.example.com", true)]
    [InlineData("api.example.com", "example.com", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("notexample.com", "example.com", false)]
    [InlineData("prod-eu.example-health.test", "*example-health*", true)]
    [InlineData("example.org", "*.example.com", false)]
    public void HostPatterns(string host, string pattern, bool expected) => Assert.Equal(expected, HostPattern.Matches(host, pattern));

    [Theory]
    [InlineData("prod-eu.example-clinic.test", "example-clinic.test")]
    [InlineData("example.com", "example.com")]
    [InlineData("a.b.api.example.com", "example.com")]
    [InlineData("api.shop.co.uk", "shop.co.uk")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("localhost", "localhost")]
    public void SuggestsSiteForHost(string host, string expected)
    {
        Assert.Equal(expected, HostPattern.Suggest(host));
        Assert.True(HostPattern.Matches(host, HostPattern.Suggest(host)));
    }

    [Fact]
    public void SuggestAllIsDistinctAndMostFrequentFirst() =>
        Assert.Equal(new[] { "b.test", "a.test" }, HostPattern.SuggestAll(new[] { "x.a.test", "x.b.test", "y.b.test", "" }));

    [Fact]
    public void FiltersWork()
    {
        var err = new Flow { StatusCode = 500 };
        var json = new Flow { StatusCode = 200 }; json.ResponseHeaders.Add(new("Content-Type", "application/json; charset=utf-8"));
        var img = new Flow { StatusCode = 200 }; img.ResponseHeaders.Add(new("Content-Type", "image/png"));
        var slow = new Flow { StatusCode = 200, Duration = TimeSpan.FromSeconds(2) };
        Assert.True(FlowFilter.Matches(err, FlowFilterKind.Errors));
        Assert.False(FlowFilter.Matches(json, FlowFilterKind.Errors));
        Assert.True(FlowFilter.Matches(json, FlowFilterKind.Json));
        Assert.True(FlowFilter.Matches(img, FlowFilterKind.Images));
        Assert.True(FlowFilter.Matches(slow, FlowFilterKind.Slow));
        Assert.False(FlowFilter.Matches(json, FlowFilterKind.Slow));
        var body = new Flow { Url = "https://a/b", Method = "GET", AppName = "x" };
        body.ResponseBody = Encoding.UTF8.GetBytes("hello patient 101");
        Assert.True(FlowFilter.MatchesText(body, "patient"));
        Assert.False(FlowFilter.MatchesText(body, "zzz"));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-set-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        var s = new AppSettings { Theme = "Dark", MaskSecrets = true, DelayMs = 300 };
        s.Mocks.Add(new MockRule { UrlContains = "/x", Status = 404 });
        s.Breakpoints.Add(new BreakpointRule { UrlContains = "/y", OnResponse = true });
        s.DecryptOnlyHosts.Add("*.a.com");
        store.Save(s);
        var r = store.Load();
        Assert.Equal("Dark", r.Theme); Assert.True(r.MaskSecrets); Assert.Equal(300, r.DelayMs);
        Assert.Equal(404, r.Mocks[0].Status); Assert.True(r.Breakpoints[0].OnResponse); Assert.Equal("*.a.com", r.DecryptOnlyHosts[0]);
    }

    [Fact]
    public void RuleMatching()
    {
        var m = new MockRule { UrlContains = "/orders", Method = "PUT" };
        Assert.True(m.Matches("put", "https://a/ORDERS/1"));
        Assert.False(m.Matches("GET", "https://a/orders/1"));
        Assert.False(new MockRule { UrlContains = "" }.Matches("GET", "https://a"));
    }
}

public class CertTrustTests
{
    [Fact]
    public void MacTrustIsUserDomainWithoutAdmin()
    {
        var args = CertificateService.MacTrustArgs("/Users/x/Library/Keychains/login.keychain-db", "/tmp/ca.cer");
        Assert.DoesNotContain("-d", args);
        Assert.Contains("trustRoot", args);
        Assert.Equal("/Users/x/Library/Keychains/login.keychain-db", args[Array.IndexOf(args, "-k") + 1]);
        Assert.Equal("/tmp/ca.cer", args[^1]);
    }
}

public class RoadmapFeatureTests
{
    static Flow Sample()
    {
        var f = new Flow { Id = 7, AppName = "SampleClinic", Method = "POST", Url = "https://api.example.com/v2/orders?id=1", Host = "api.example.com", Path = "/v2/orders?id=1",
            StatusCode = 201, StatusDescription = "Created", Duration = TimeSpan.FromMilliseconds(120) };
        f.RequestHeaders.Add(new("Host", "api.example.com")); f.RequestHeaders.Add(new("Content-Type", "application/json"));
        f.RequestHeaders.Add(new("Authorization", "Bearer secret-token")); f.RequestHeaders.Add(new("X-Note", "say \"hi\""));
        f.RequestBody = System.Text.Encoding.UTF8.GetBytes("{\"a\":1,\n\"b\":\"x\"}");
        f.ResponseHeaders.Add(new("Content-Type", "application/json"));
        f.ResponseBody = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
        return f;
    }

    [Fact]
    public void SavedSessionOpensBackWithEverything()
    {
        var a = Sample(); a.Edited = true;
        var bin = new Flow { Id = 8, AppName = "SampleClinic", Method = "GET", Url = "https://cdn.example.com/logo.png", Host = "cdn.example.com", Path = "/logo.png", StatusCode = 200 };
        bin.ResponseHeaders.Add(new("Content-Type", "image/png")); bin.ResponseBody = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 255, 1 };
        var failed = new Flow { Id = 9, AppName = "SampleClinic", Method = "GET", Url = "https://down.example.com/x", Host = "down.example.com", Path = "/x", Error = "Connection refused" };

        long id = 100;
        var back = HarImporter.Parse(HarExporter.Build(new[] { a, bin, failed }, false), () => ++id);

        Assert.Equal(3, back.Count);
        Assert.Equal(new long[] { 101, 102, 103 }, back.Select(f => f.Id));
        var r = back[0];
        Assert.Equal("POST", r.Method); Assert.Equal(a.Url, r.Url); Assert.Equal("api.example.com", r.Host); Assert.Equal("/v2/orders?id=1", r.Path);
        Assert.Equal(201, r.StatusCode); Assert.Equal("SampleClinic", r.AppName); Assert.Equal("Imported", r.Source); Assert.True(r.Edited);
        Assert.Equal(a.RequestBody, r.RequestBody); Assert.Equal(a.ResponseBody, r.ResponseBody);
        Assert.Equal("Bearer secret-token", Flow.Header(r.RequestHeaders, "Authorization"));
        Assert.Equal(120, r.Duration!.Value.TotalMilliseconds);
        Assert.Equal(bin.ResponseBody, back[1].ResponseBody);                 // binary survives (base64)
        Assert.Null(back[2].StatusCode); Assert.Equal("Connection refused", back[2].Error);
    }

    [Fact]
    public void OpensAHarWrittenByABrowser()
    {
        const string har = """
        {"log":{"version":"1.2","creator":{"name":"WebInspector","version":"537.36"},"entries":[
          {"startedDateTime":"2026-11-13T05:00:00.000Z","time":42.5,
           "request":{"method":"get","url":"https://example.com/a?x=1","httpVersion":"h2","headers":[{"name":":authority","value":"example.com"},{"name":"accept","value":"*/*"}]},
           "response":{"status":200,"statusText":"","headers":[{"name":"content-type","value":"text/plain"}],"content":{"size":2,"mimeType":"text/plain","text":"aGk=","encoding":"base64"}}},
          {"request":{"method":"GET","url":"not a url","headers":[]},"response":{"status":200}},
          "junk", {"request":"junk"}, {"request":{"method":"GET","url":"https://example.com/b","headers":"junk","postData":"junk"},"response":"junk"}
        ]}}
        """;
        long id = 0;
        var flows = HarImporter.Parse(har, () => ++id);
        Assert.Equal(2, flows.Count);                                           // broken entries are skipped, odd ones tolerated
        Assert.Equal("https://example.com/b", flows[1].Url); Assert.NotNull(flows[1].Error);
        var f = flows[0];
        Assert.Equal("GET", f.Method); Assert.Equal("Imported", f.AppName); Assert.Equal("h2", f.HttpVersion);
        Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(f.ResponseBody!));
        Assert.DoesNotContain(f.RequestHeaders, h => h.Key.StartsWith(':'));
    }

    [Theory]
    [InlineData("{\"hello\":1}")]
    [InlineData("[]")]
    [InlineData("{\"log\":[]}")]
    [InlineData("{\"log\":{\"entries\":{}}}")]
    public void FilesThatAreNotHarAreRefused(string json) => Assert.Throws<FormatException>(() => HarImporter.Parse(json, () => 1));

    [Theory]
    [InlineData("https://api.example.com/v2/orders?id=1", "https://staging.example.com", "https://staging.example.com/v2/orders?id=1")]
    [InlineData("https://api.example.com/v2/orders?id=1", "http://localhost:3000/", "http://localhost:3000/v2/orders?id=1")]
    [InlineData("https://api.example.com:8443/a", "https://staging.example.com/base/", "https://staging.example.com/base/a")]
    public void MapRemoteKeepsPathAndQuery(string url, string target, string expected) => Assert.Equal(expected, MapRule.Remap(url, target));

    [Fact]
    public void MapRulesOnlyMatchWhenTheyHaveAUsableTarget()
    {
        Assert.True(new MapRule { UrlContains = "/orders", LocalPath = "/tmp/a.json" }.Matches("GET", "https://x.test/orders"));
        Assert.False(new MapRule { UrlContains = "/orders" }.Matches("GET", "https://x.test/orders"));
        Assert.False(new MapRule { UrlContains = "/orders", IsRemote = true, RemoteUrl = "staging" }.Matches("GET", "https://x.test/orders"));
        Assert.False(new MapRule { UrlContains = "/orders", IsRemote = true, RemoteUrl = "ftp://x.test" }.Matches("GET", "https://x.test/orders"));
        Assert.True(new MapRule { UrlContains = "/orders", IsRemote = true, RemoteUrl = "http://localhost:3000", Method = "POST" }.Matches("post", "https://x.test/orders"));
        Assert.False(new MapRule { UrlContains = "/orders", LocalPath = "/tmp/a.json", Enabled = false }.Matches("GET", "https://x.test/orders"));
        Assert.Equal("application/json", MapRule.ContentTypeFor("/tmp/A.JSON"));
        Assert.Equal("application/octet-stream", MapRule.ContentTypeFor("/tmp/data"));
    }

    [Fact]
    public void OldSettingsFilesWithoutMapsStillLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-set-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\",\"Mocks\":[],\"Breakpoints\":[]}");
        var s = new SettingsStore(dir).Load();
        Assert.Equal("Dark", s.Theme); Assert.NotNull(s.Maps); Assert.Empty(s.Maps);
        s.Maps.Add(new MapRule { UrlContains = "/a", IsRemote = true, RemoteUrl = "http://localhost:1" });
        new SettingsStore(dir).Save(s);
        Assert.True(Assert.Single(new SettingsStore(dir).Load().Maps).IsRemote);
    }

    [Fact]
    public void HexDumpShowsOffsetsBytesAndText()
    {
        var dump = Formatting.HexDump(System.Text.Encoding.ASCII.GetBytes("Hello, TrafficScope!\n"));
        var lines = dump.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("00000000  48 65 6c 6c 6f 2c 20 54  72 61 66 66 69 63 53 63  Hello, TrafficSc", lines[0]);
        Assert.StartsWith("00000010  6f 70 65 21 0a", lines[1]);
        Assert.EndsWith("ope!.", lines[1]);
        Assert.Equal("", Formatting.HexDump(null));
        Assert.Contains("more not shown", Formatting.HexDump(new byte[100], 32));
    }

    [Theory]
    [InlineData("Python", "requests.request(\"POST\", url, headers=headers, data=data.encode(\"utf-8\"))")]
    [InlineData("JavaScript", "await fetch(\"https://api.example.com/v2/orders?id=1\", {")]
    [InlineData("C#", "new HttpRequestMessage(new HttpMethod(\"POST\"), \"https://api.example.com/v2/orders?id=1\")")]
    public void CodeIsGeneratedAndEscaped(string language, string expected)
    {
        var code = CodeGen.Build(language, Sample(), false);
        Assert.Contains(expected, code);
        Assert.Contains("\"say \\\"hi\\\"\"", code);                    // quotes inside a header value are escaped
        Assert.Contains("{\\\"a\\\":1,\\n", code);                       // body newline is escaped, not a raw line break
        Assert.Contains("Bearer secret-token", code);
        Assert.DoesNotContain("\"Host\"", code);
        Assert.DoesNotContain("secret-token", CodeGen.Build(language, Sample(), true));   // masking applies
    }

    [Fact]
    public void GeneratedPythonRunsAsValidSyntax()
    {
        if (!File.Exists("/usr/bin/python3")) return;
        var file = Path.Combine(Path.GetTempPath(), "ts-code-" + Guid.NewGuid().ToString("N")[..6] + ".py");
        File.WriteAllText(file, CodeGen.Build("Python", Sample(), false));
        var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/python3") { RedirectStandardError = true };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("import ast,sys; ast.parse(open(sys.argv[1]).read())"); psi.ArgumentList.Add(file);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        File.Delete(file);
        Assert.True(p.ExitCode == 0, err);
    }

    [Theory]
    [InlineData("contract_name", true)]
    [InlineData("CONTRACT_NAME", true)]
    [InlineData("Variable\"", true)]
    [InlineData("31367", true)]
    [InlineData("not-in-there", false)]
    [InlineData("Ünïcode", true)]
    public void BodySearchScansBytesAndIgnoresCase(string q, bool found)
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"Contract_Name\":\"Variable\",\"item_id\":\"31367\",\"note\":\"ünïcode\"}");
        Assert.Equal(found, FlowFilter.BodyContains("application/json", body, q));
        Assert.False(FlowFilter.BodyContains("image/png", new byte[] { 1, 2, 3 }, "a"));
        Assert.False(FlowFilter.BodyContains("application/json", body, new string('x', 500)));
    }

    [Fact]
    public void LongTextIsPreviewedAndFileNamesAreSensible()
    {
        var text = string.Join("\n", Enumerable.Repeat(new string('a', 99), 5000));        // 500,000 characters
        var p = Formatting.Preview(text, 500_000);
        Assert.True(p.Length < Formatting.PreviewChars + 300);
        Assert.Contains("Preview only", p);
        Assert.Equal("short", Formatting.Preview("short", 5));
        Assert.Equal("item_master_report-response.json", Formatting.BodyFileName("https://x.test/app/services/item_master_report.php", "application/json", false));
        Assert.Equal("orders-request.txt", Formatting.BodyFileName("https://x.test/v2/orders/?a=1", "application/x-www-form-urlencoded", true));
        Assert.Equal("body-response.bin", Formatting.BodyFileName("https://x.test/", null, false));
    }
}
