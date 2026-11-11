using TrafficScope.Core;
using Xunit;

namespace TrafficScope.Tests;

public class CoreTests
{
    [Theory]
    [InlineData("/Applications/Chrome.app/Contents/Frameworks/Helper.app/Contents/MacOS/Helper", "/Applications/Chrome.app", true)]
    [InlineData("/Applications/Chrome.app/Contents/MacOS/Chrome", "/Applications/Chrome.app", true)]
    [InlineData("/Applications/Chrome.appx/Contents/MacOS/x", "/Applications/Chrome.app", false)]
    [InlineData("/usr/bin/curl", "/usr/bin/curl", true)]
    [InlineData("/usr/bin/curl", "/usr/bin/wget", false)]
    [InlineData(null, "/x", false)]
    public void Matcher(string? path, string key, bool expected) => Assert.Equal(expected, AppMatcher.Matches(path, key));

    [Fact]
    public void CurlIncludesMethodHeadersAndBody()
    {
        var f = new Flow { Method = "POST", Url = "https://api.test/v1/x?a=1" };
        f.RequestHeaders.Add(new("Content-Type", "application/json"));
        f.RequestHeaders.Add(new("Host", "api.test"));
        f.RequestHeaders.Add(new("Content-Length", "7"));
        f.RequestBody = System.Text.Encoding.UTF8.GetBytes("{\"a\":'1'}");
        var curl = CurlBuilder.Build(f);
        Assert.Contains("-X POST", curl);
        Assert.Contains("'https://api.test/v1/x?a=1'", curl);
        Assert.Contains("Content-Type: application/json", curl);
        Assert.DoesNotContain("Host:", curl);
        Assert.DoesNotContain("Content-Length", curl);
        Assert.Contains("{\"a\":'\\''1'\\''}", curl);
    }

    [Fact]
    public void JsonBodyIsPrettyPrintedAndBinaryIsBase64ForCopy()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");
        Assert.Contains("\n", Formatting.BodyText("application/json", json));
        var bin = new byte[] { 0, 1, 2, 255 };
        Assert.Equal(Convert.ToBase64String(bin), Formatting.BodyForCopy("application/octet-stream", bin));
    }

    [Fact]
    public void ProxyStartsAndStops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-test-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        using var e = new ProxyEngine(dir);
        e.Start();
        Assert.True(e.IsRunning);
        Assert.True(e.Port > 0);
        Assert.NotNull(e.RootCertificate);
        e.Stop();
        Assert.False(e.IsRunning);
    }

    [Fact]
    public void LiveCaptureAttributesTrafficToSelectedApp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Path.Combine(Path.GetTempPath(), "ts-live-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        using var e = new ProxyEngine(dir);
        e.Start();
        var done = new TaskCompletionSource<Flow>();
        e.FlowUpdated += f => done.TrySetResult(f);
        e.BeginCapture(new[] { "/usr/bin/curl" });
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/curl", $"-s -o /dev/null -x http://127.0.0.1:{e.Port} http://example.com/") { UseShellExecute = false })!;
        Assert.True(done.Task.Wait(TimeSpan.FromSeconds(20)), "no flow captured");
        var flow = done.Task.Result;
        Assert.Equal("curl", flow.AppName);
        Assert.Equal(200, flow.StatusCode);
        Assert.NotNull(flow.ResponseBody);
        p.WaitForExit();
    }
}
