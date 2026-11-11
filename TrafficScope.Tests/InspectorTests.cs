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
