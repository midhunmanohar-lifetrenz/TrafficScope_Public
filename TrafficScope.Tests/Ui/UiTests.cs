using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrafficScope.Core;
using TrafficScope.ViewModels;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TrafficScope.Tests.Ui.TestAppBuilder))]

namespace TrafficScope.Tests.Ui;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        PostmanStore.Reset();
        RulesVm.CheckPermission = () => false;
        MainViewModel.DetectLeftoverProxy = () => (false, "");
        Environment.SetEnvironmentVariable("TS_DATA_DIR", Path.Combine(Path.GetTempPath(), "ts-ui-" + Guid.NewGuid().ToString("N")[..6]));
        return AppBuilder.Configure<TrafficScope.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>In-memory key so UI tests never touch the real Keychain.</summary>
public static class PostmanStore
{
    public static string? Key;
    public static void Reset()
    {
        Key = null;
        PostmanSetup.GetKey = () => Key;
        PostmanSetup.SetKey = k => { Key = k; return true; };
        PostmanSetup.DeleteKey = () => Key = null;
    }
}

public sealed class FakeUi : IUiServices
{
    public string Clipboard = "";
    public string? SavePath;
    public Action<EditModel>? OnEdit;
    public int RulesOpened;
    public (Flow, Flow)? Compared;
    public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult(SavePath);
    public Task EditAsync(EditModel model) { OnEdit?.Invoke(model); return Task.CompletedTask; }
    public Task<bool> OpenRulesAsync(AppSettings settings, int tab) { RulesOpened++; return Task.FromResult(true); }
    public void ShowCompare(Flow a, Flow b) => Compared = (a, b);
}

public static class Samples
{
    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static readonly string Jwt = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + B64("{\"sub\":\"demo-user\",\"role\":\"admin\",\"exp\":1893456000}") + ".sig";

    public static Flow Make(long id, string app, string m, string url, int? code, string reqCt, string req, string resCt, string res, int ms)
    {
        var u = new Uri(url);
        var f = new Flow { Id = id, AppName = app, Method = m, Url = url, Host = u.Host, Path = u.PathAndQuery, StatusCode = code,
            StatusDescription = code switch { 200 => "OK", 500 => "Internal Server Error", _ => "" }, Duration = TimeSpan.FromMilliseconds(ms) };
        f.RequestHeaders.Add(new("Host", u.Host));
        f.RequestHeaders.Add(new("Content-Type", reqCt));
        f.RequestHeaders.Add(new("Authorization", "Bearer " + Jwt));
        f.RequestHeaders.Add(new("Cookie", "sid=a81f3c; theme=dark"));
        f.RequestHeaders.Add(new("User-Agent", "SampleClinic/1.25"));
        f.ResponseHeaders.Add(new("Content-Type", resCt));
        f.ResponseHeaders.Add(new("Set-Cookie", "sid=a81f3c; Path=/; HttpOnly; Secure"));
        f.RequestBody = req.Length > 0 ? Encoding.UTF8.GetBytes(req) : null;
        f.ResponseBody = Encoding.UTF8.GetBytes(res);
        return f;
    }

    public static void Fill(MainViewModel vm)
    {
        vm.AddFlow(Make(1, "SampleClinic", "POST", "https://prod.example-clinic.test/services/get_invoice_numbers.php?site=12", 200,
            "application/x-www-form-urlencoded", "start%5Fdate=04%2F01%2F2026&appVersion=3%2E5%2E90&period%5Fid=42&site=12",
            "application/json", "{\"data\":[{\"invoice_prefix\":\"INV/A/2026/\",\"current_number\":\"INV/A/2026/0001\",\"period_id\":42,\"has_alias\":1},{\"invoice_prefix\":\"INV/B/2026/\",\"period_id\":42}]}", 182));
        vm.AddFlow(Make(2, "SampleClinic", "GET", "https://api.example.com/v2/patients?page=1&size=20", 200, "application/json", "", "application/json", "{\"items\":[{\"id\":101,\"name\":\"Demo\"}],\"total\":1}", 96));
        vm.AddFlow(Make(3, "SampleClinic", "PUT", "https://api.example.com/v2/orders/88", 500, "application/json", "{\"status\":\"done\"}", "application/json", "{\"error\":\"internal\"}", 1310));
        vm.AddFlow(Make(4, "Google Chrome", "GET", "https://fonts.gstatic.com/s/inter.woff2", 200, "application/json", "", "font/woff2", "binary", 41));
        vm.AddFlow(new Flow { Id = 5, AppName = "SampleClinic", Method = "CONNECT", Url = "https://telemetry.example.com:443", Host = "telemetry.example.com", IsTunnel = true, StatusCode = 200, StatusDescription = "Tunnel (not decrypted)" });
    }
}

public class UiTests
{
    static (MainWindow W, MainViewModel Vm, FakeUi Ui) Open(bool show = true)
    {
        var w = new MainWindow();
        var vm = w.ViewModel;
        var ui = new FakeUi();
        vm.Ui = ui;
        Samples.Fill(vm);
        if (show) { w.Show(); Dispatcher.UIThread.RunJobs(); }
        return (w, vm, ui);
    }

    static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("TS_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir)) frame!.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public void MainWindowRendersLightAndDark()
    {
        var (w, vm, _) = Open();
        vm.SelectedFlow = vm.Flows[0];
        Shot(w, "1-main-light-body");
        vm.Settings.Theme = "Dark"; vm.ApplyTheme();
        Shot(w, "2-main-dark-body");
        vm.Settings.Theme = "Light"; vm.ApplyTheme();
        w.FindControl<TabControl>("Inspector")!.SelectedIndex = 2;
        Shot(w, "3-params-form-decoded");
        w.FindControl<TabControl>("Inspector")!.SelectedIndex = 1;
        Shot(w, "4-headers-jwt");
        w.Close();
    }

    [AvaloniaFact]
    public void InspectorShowsTreeFormJwtCookiesAndTiming()
    {
        var (_, vm, _) = Open(false);
        vm.SelectedFlow = vm.Flows[0];
        Assert.True(vm.ResponseIsJson);
        Assert.Equal("data", vm.ResponseTree[0].Children[0].Name);
        Assert.False(vm.RequestIsJson);   // form body shows as text
        Assert.True(vm.HasForm);
        Assert.Contains(vm.FormFields, f => f.Name == "start_date" && f.Value == "04/01/2026");
        Assert.True(vm.HasQuery);
        Assert.True(vm.HasJwt);
        Assert.Contains("demo-user", vm.JwtText);
        Assert.Contains(vm.RequestCookies, c => c.Name == "sid");
        Assert.Contains(vm.ResponseCookies, c => c.Note.Contains("HttpOnly"));
        Assert.Contains(vm.TimingRows, r => r.Name == "Total time" && r.Value == "182 ms");
        vm.ToggleTreeCommand.Execute(null);
        Assert.False(vm.ResponseIsJson);
        Assert.Contains("invoice_prefix", vm.ResponseBodyText);
        vm.ToggleTreeCommand.Execute(null);
        vm.CollapseAllCommand.Execute(null);
        Assert.False(vm.ResponseTree[0].Children[0].IsExpanded);
        vm.ExpandAllCommand.Execute(null);
        Assert.True(vm.ResponseTree[0].Children[0].Children[0].IsExpanded);
    }

    [AvaloniaFact]
    public void FilterChipsSearchAndTunnels()
    {
        var (_, vm, _) = Open(false);
        Assert.Equal(4, vm.Flows.Count);               // tunnel hidden by default
        Assert.Equal("All 4", vm.AllChip);
        Assert.Equal("Errors 1", vm.ErrorsChip);
        vm.SetFilterCommand.Execute("Errors");
        Assert.Single(vm.Flows); Assert.Equal(3, vm.Flows[0].Id);
        vm.SetFilterCommand.Execute("Slow");
        Assert.Single(vm.Flows);
        vm.SetFilterCommand.Execute("Json");
        Assert.Equal(3, vm.Flows.Count);
        vm.SetFilterCommand.Execute("All");
        vm.FilterText = "invoice_prefix";                // body search
        Assert.Single(vm.Flows);
        vm.FilterText = "";
        vm.ShowTunnels = true;
        Assert.Equal(5, vm.Flows.Count);
        Assert.Equal("Tunnel", vm.Flows[4].Tag);
        vm.ShowTunnels = false;
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Flows);
    }

    [AvaloniaFact]
    public async Task CopyActionsRespectMasking()
    {
        var (_, vm, ui) = Open(false);
        vm.SelectedFlow = vm.Flows[1];
        await vm.CopyRequestCommand.ExecuteAsync(null);
        Assert.StartsWith("GET /v2/patients", ui.Clipboard);
        Assert.Contains(Samples.Jwt, ui.Clipboard);
        await vm.CopyResponseBodyCommand.ExecuteAsync(null);
        Assert.Contains("\"total\": 1", ui.Clipboard);
        await vm.CopyResponseCommand.ExecuteAsync(null);
        Assert.StartsWith("HTTP/1.1 200", ui.Clipboard);
        await vm.CopyUrlCommand.ExecuteAsync(null);
        Assert.Equal("https://api.example.com/v2/patients?page=1&size=20", ui.Clipboard);
        await vm.CopyCurlCommand.ExecuteAsync(null);
        Assert.StartsWith("curl 'https://api.example.com", ui.Clipboard);
        vm.MaskSecrets = true;
        await vm.CopyRequestHeadersCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Samples.Jwt, ui.Clipboard);
        Assert.Contains("Bearer " + Redactor.Mask, ui.Clipboard);
        await vm.CopyCurlCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Samples.Jwt, ui.Clipboard);
        vm.MaskSecrets = false;
    }

    [AvaloniaFact]
    public async Task SaveHarCompareAndRuleShortcuts()
    {
        var (_, vm, ui) = Open(false);
        ui.SavePath = Path.Combine(Path.GetTempPath(), "ts-" + Guid.NewGuid().ToString("N")[..6] + ".har");
        await vm.SaveHarCommand.ExecuteAsync(null);
        using (var d = JsonDocument.Parse(File.ReadAllText(ui.SavePath)))
            Assert.Equal(4, d.RootElement.GetProperty("log").GetProperty("entries").GetArrayLength());

        Assert.False(vm.CanCompare);
        vm.SetMultiSelection(new[] { vm.Flows[1], vm.Flows[2] });
        Assert.True(vm.CanCompare);
        vm.CompareCommand.Execute(null);
        Assert.Equal(2, ui.Compared!.Value.Item1.Id);

        vm.SelectedFlow = vm.Flows[2];
        var before = vm.Settings.Mocks.Count;
        await vm.MockFromSelectedCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, vm.Settings.Mocks.Count);
        var mock = vm.Settings.Mocks.Last();
        Assert.Equal("/v2/orders/88", mock.UrlContains); Assert.Equal(500, mock.Status); Assert.Contains("internal", mock.Body);
        await vm.BreakpointFromSelectedCommand.ExecuteAsync(null);
        Assert.Equal("/v2/orders/88", vm.Settings.Breakpoints.Last().UrlContains);
        Assert.Contains("mock", vm.RulesSummary);
        Assert.Equal(2, ui.RulesOpened);
    }

    [AvaloniaFact]
    public async Task EditAndResendAndReplayHitTheServer()
    {
        using var srv = new LocalServer();
        var (_, vm, ui) = Open(false);
        var src = Samples.Make(50, "SampleClinic", "POST", $"http://127.0.0.1:{srv.Port}/v2/login", 200, "application/json", "{\"user\":\"demo\"}", "application/json", "{}", 10);
        vm.AddFlow(src);
        vm.SelectedFlow = vm.Flows.Last();
        await vm.ReplayCommand.ExecuteAsync(null);
        Assert.Equal("{\"user\":\"demo\"}", srv.LastBody);
        Assert.Equal("Replay", vm.SelectedFlow!.Tag);
        Assert.Equal("200", vm.SelectedFlow.Status);

        vm.SelectedFlow = vm.Flows.First(f => f.Id == 50);
        ui.OnEdit = m => { Assert.Equal(EditMode.Compose, m.Mode); m.Body = "{\"user\":\"edited\"}"; m.Method = "PUT"; m.Accepted = true; };
        await vm.EditAndResendCommand.ExecuteAsync(null);
        Assert.Equal("PUT", srv.LastMethod);
        Assert.Equal("{\"user\":\"edited\"}", srv.LastBody);
        Assert.Equal("Resent", vm.SelectedFlow!.Tag);

        ui.OnEdit = m => { m.Accepted = false; };
        var count = vm.Flows.Count;
        await vm.EditAndResendCommand.ExecuteAsync(null);
        Assert.Equal(count, vm.Flows.Count);   // cancel sends nothing
    }

    [AvaloniaFact]
    public void ToolWindowsRender()
    {
        var s = new AppSettings();
        s.Mocks.Add(new MockRule { Name = "Order fails", Method = "PUT", UrlContains = "/v2/orders", Status = 500, Body = "{\"error\":\"internal\"}" });
        s.Breakpoints.Add(new BreakpointRule { UrlContains = "/v2/login", OnRequest = true, OnResponse = true });
        s.DecryptOnlyHosts.Add("*.example.com");
        var rvm = new RulesVm(s, 0);
        var rw = new RulesWindow(rvm); rw.Show(); Shot(rw, "5-tools-mocks");
        rvm.SelectedTab = 1; Shot(rw, "6-tools-breakpoints");
        rvm.SelectedTab = 2; Shot(rw, "7-tools-settings");
        rvm.DelayText = "300"; rvm.DecryptOnlyText = "*.example.com\nexample-clinic.test"; rvm.MaskSecrets = true; rvm.Theme = "Dark";
        rw.Close();
        Assert.Equal(300, s.DelayMs); Assert.Equal(2, s.DecryptOnlyHosts.Count); Assert.True(s.MaskSecrets); Assert.Equal("Dark", s.Theme);

        var em = new EditModel { Mode = EditMode.BreakResponse, Title = "Breakpoint: edit response before the app gets it", Method = "POST", Url = "https://api.example.com/v2/login", Status = 200, Headers = "Content-Type: application/json", Body = "{\"token\":\"abc\"}" };
        var ew = new EditWindow(em); ew.Show(); Shot(ew, "8-breakpoint-editor"); ew.Close();

        var a = Samples.Make(1, "x", "GET", "https://a.test/v1?x=1", 200, "application/json", "", "application/json", "{\"a\":1,\"b\":2}", 5);
        var b = Samples.Make(2, "x", "GET", "https://a.test/v1?x=2", 500, "application/json", "", "application/json", "{\"a\":1,\"b\":3}", 5);
        var cw = new CompareWindow(a, b); cw.Show(); Shot(cw, "9-compare"); cw.Close();
    }

    [AvaloniaFact]
    public async Task PostmanConnectAndSendSavesIntoWorkspace()
    {
        using var fake = new FakePostman();
        PostmanStore.Reset();
        PostmanSetup.ClientFactory = k => new PostmanClient(k, fake.BaseUrl);
        try
        {
            var s = new AppSettings();
            var rvm = new RulesVm(s, 2);
            Assert.False(rvm.IsPostmanConnected);
            var rw = new RulesWindow(rvm); rw.Show();
            rvm.PostmanKeyInput = "PMAK-wrong";
            await rvm.ConnectPostmanCommand.ExecuteAsync(null);
            Assert.False(rvm.IsPostmanConnected);
            Assert.Contains("API key", rvm.PostmanStatus);
            Assert.Null(PostmanStore.Key);              // bad key is not stored
            Shot(rw, "10-postman-not-connected");
            rvm.PostmanKeyInput = "PMAK-test";
            await rvm.ConnectPostmanCommand.ExecuteAsync(null);
            Assert.True(rvm.IsPostmanConnected);
            Assert.Equal("PMAK-test", PostmanStore.Key);
            Assert.Equal("", rvm.PostmanKeyInput);       // key not kept on screen
            Assert.Equal("My Workspace", rvm.SelectedWorkspace!.Name);   // personal by default
            rvm.SelectedWorkspace = rvm.Workspaces.First(w => w.Name == "QA Team");
            rvm.PostmanCollection = "LTZ debugging";
            Shot(rw, "11-postman-connected");
            rw.Close();
            Assert.Equal("ws2", s.PostmanWorkspaceId);
            Assert.Equal("LTZ debugging", s.PostmanCollection);
            Assert.Equal("Demo User", s.PostmanUser);

            var (_, vm, ui) = Open(false);
            vm.Settings.PostmanWorkspaceId = s.PostmanWorkspaceId; vm.Settings.PostmanWorkspaceName = s.PostmanWorkspaceName;
            vm.Settings.PostmanCollection = s.PostmanCollection;
            var opened = 0; vm.OpenPostmanApp = () => opened++;
            vm.SelectedFlow = vm.Flows[0];
            await vm.SendToPostmanCommand.ExecuteAsync(null);
            Assert.Equal(1, opened);
            var col = Assert.Single(fake.Collections).Value;
            Assert.Equal("LTZ debugging", col["info"]!["name"]!.GetValue<string>());
            var item = col["item"]![0]!;
            Assert.Equal("urlencoded", item["request"]!["body"]!["mode"]!.GetValue<string>());
            Assert.Contains("Saved to Postman", vm.Status);

            rvm = new RulesVm(s, 2);
            rvm.DisconnectPostmanCommand.Execute(null);
            Assert.Null(PostmanStore.Key);
            Assert.Equal("", s.PostmanWorkspaceId);
        }
        finally { PostmanStore.Reset(); PostmanSetup.ClientFactory = k => new PostmanClient(k); }
    }

    [AvaloniaFact]
    public async Task SendToPostmanWithoutConnectionOpensSettings()
    {
        PostmanStore.Reset();
        var (_, vm, ui) = Open(false);
        var opened = 0; vm.OpenPostmanApp = () => opened++;
        vm.SelectedFlow = vm.Flows[0];
        await vm.SendToPostmanCommand.ExecuteAsync(null);
        Assert.Equal(1, ui.RulesOpened);
        Assert.Equal(0, opened);
    }

    [AvaloniaFact]
    public async Task ProxyPermissionCanBeTurnedOnAndOff()
    {
        var installed = false;
        RulesVm.CheckPermission = () => installed;
        RulesVm.InstallPermission = () => { installed = true; return (true, "Done."); };
        RulesVm.RemovePermission = () => { installed = false; return (true, "Removed."); };
        try
        {
            var rvm = new RulesVm(new AppSettings(), 2);
            Assert.Equal(OperatingSystem.IsMacOS(), rvm.IsMac);
            Assert.False(rvm.PermissionInstalled);
            Assert.StartsWith("Off", rvm.PermissionStatus);
            var rw = new RulesWindow(rvm); rw.Show(); Shot(rw, "12-proxy-permission-off");
            await rvm.InstallProxyPermissionCommand.ExecuteAsync(null);
            Assert.Equal(OperatingSystem.IsMacOS(), rvm.PermissionInstalled);
            Shot(rw, "13-proxy-permission-on");
            await rvm.RemoveProxyPermissionCommand.ExecuteAsync(null);
            Assert.False(rvm.PermissionInstalled);
            RulesVm.InstallPermission = () => (false, "Cancelled.");
            await rvm.InstallProxyPermissionCommand.ExecuteAsync(null);
            Assert.False(rvm.PermissionInstalled);
            Assert.Contains("Cancelled", rvm.PermissionStatus);
            rw.Close();
        }
        finally
        {
            RulesVm.CheckPermission = () => false;
            RulesVm.InstallPermission = ProxyPermission.Install;
            RulesVm.RemovePermission = ProxyPermission.Remove;
        }
    }

    [AvaloniaFact]
    public async Task LeftoverProxyBannerFixesProxy()
    {
        var left = true; var restored = 0;
        MainViewModel.DetectLeftoverProxy = () => left ? (true, "Wi-Fi: 127.0.0.1:59309") : (false, "");
        MainViewModel.RestoreProxy = () => { restored++; left = false; return (true, "Done."); };
        try
        {
            var (w, vm, _) = Open();
            await vm.CheckLeftoverProxyAsync();
            Assert.True(vm.HasLeftoverProxy);
            Shot(w, "14-leftover-proxy-banner");
            await vm.FixLeftoverProxyCommand.ExecuteAsync(null);
            Assert.Equal(1, restored);
            Assert.False(vm.HasLeftoverProxy);
            Assert.Contains("connect directly", vm.Status);
            w.Close();
        }
        finally { MainViewModel.DetectLeftoverProxy = () => (false, ""); MainViewModel.RestoreProxy = SystemProxy.Disable; }
    }
}
