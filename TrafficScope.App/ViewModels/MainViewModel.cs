using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

public enum EditMode { Compose, BreakRequest, BreakResponse }

public sealed class EditModel
{
    public EditMode Mode { get; init; }
    public string Title { get; init; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public int Status { get; set; }
    public string Headers { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Accepted { get; set; }
    public bool Abort { get; set; }
}

/// <summary>Things only the window can do (dialogs, clipboard, file pickers).</summary>
public interface IUiServices
{
    Task SetClipboardAsync(string text);
    Task<string?> PickSaveFileAsync(string suggestedName);
    Task EditAsync(EditModel model);
    Task<bool> OpenRulesAsync(AppSettings settings, int tab);
    void ShowCompare(Flow a, Flow b);
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    readonly ProxyEngine _engine = new(FavoritesStore.DataDir);
    readonly FavoritesStore _store = new();
    readonly SettingsStore _settingsStore = new();
    readonly List<FlowVm> _all = new();
    readonly Dictionary<string, bool> _selected = new();
    readonly Dictionary<string, AppEntry> _favorites = new();
    readonly Dictionary<string, int> _counts = new();
    List<AppEntry> _running = new(), _installed = new();
    readonly DispatcherTimer _appTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    CancellationTokenSource? _toastCts;
    List<FlowVm> _multi = new();

    public IUiServices? Ui { get; set; }
    public AppSettings Settings { get; private set; }

    public ObservableCollection<AppItemVm> Favorites { get; } = new();
    public ObservableCollection<AppItemVm> Others { get; } = new();
    public ObservableCollection<FlowVm> Flows { get; } = new();
    public ObservableCollection<JsonNodeVm> RequestTree { get; } = new();
    public ObservableCollection<JsonNodeVm> ResponseTree { get; } = new();
    public ObservableCollection<NameValue> RequestHeaders { get; } = new();
    public ObservableCollection<NameValue> ResponseHeaders { get; } = new();
    public ObservableCollection<NameValue> QueryParams { get; } = new();
    public ObservableCollection<NameValue> FormFields { get; } = new();
    public ObservableCollection<NameValue> RequestCookies { get; } = new();
    public ObservableCollection<NameValue> ResponseCookies { get; } = new();
    public ObservableCollection<NameValue> TimingRows { get; } = new();

    [ObservableProperty] bool _isCapturing;
    [ObservableProperty] bool _isPaused;
    [ObservableProperty] bool _showInstalled;
    [ObservableProperty] bool _certTrusted = true;
    [ObservableProperty] string _appSearch = "";
    [ObservableProperty] string _filterText = "";
    [ObservableProperty] FlowFilterKind _filterKind = FlowFilterKind.All;
    [ObservableProperty] string _status = "Pick one or more apps, then press Record.";
    [ObservableProperty] string _toast = "";
    [ObservableProperty] FlowVm? _selectedFlow;
    [ObservableProperty] string _requestText = "";
    [ObservableProperty] string _responseText = "";
    [ObservableProperty] string _requestBodyText = "";
    [ObservableProperty] string _responseBodyText = "";
    [ObservableProperty] string _requestHeadersText = "";
    [ObservableProperty] string _responseHeadersText = "";
    [ObservableProperty] bool _showTree = true;
    [ObservableProperty] string _jwtText = "";
    [ObservableProperty] Bitmap? _responseImage;
    [ObservableProperty] string _selectedSummary = "No apps selected";
    [ObservableProperty] string _recordingText = "";
    [ObservableProperty] string _footerText = "";
    [ObservableProperty] string _breakpointBanner = "";

    public string RecordText => IsCapturing ? "Stop" : "Record";
    public string PauseText => IsPaused ? "Resume" : "Pause";
    public bool HasSelection => SelectedFlow != null;
    public bool CanCompare => _multi.Count == 2;
    public bool RequestIsJson => RequestTree.Count > 0 && ShowTree;
    public bool ResponseIsJson => ResponseTree.Count > 0 && ShowTree && ResponseImage == null;
    public bool RequestIsText => !RequestIsJson;
    public bool ResponseIsText => !ResponseIsJson && ResponseImage == null;
    public bool HasResponseImage => ResponseImage != null;
    public bool HasJwt => JwtText.Length > 0;
    public bool HasForm => FormFields.Count > 0;
    public bool HasQuery => QueryParams.Count > 0;
    public bool HasParams => HasForm || HasQuery;
    public bool NoParams => !HasParams;
    public bool ShowSetup => !CertTrusted;
    public bool ShowTunnels { get => Settings.ShowTunnels; set { Settings.ShowTunnels = value; SaveSettings(); ApplyFilter(); OnPropertyChanged(); } }
    public bool MaskSecrets { get => Settings.MaskSecrets; set { Settings.MaskSecrets = value; SaveSettings(); OnPropertyChanged(); } }
    public string TreeToggleText => ShowTree ? "Raw" : "Tree";
    public int TunnelCount => _all.Count(f => f.Flow.IsTunnel);
    public string TunnelText => $"Show tunnels ({TunnelCount})";
    public string AllChip => $"All {_all.Count(f => !f.Flow.IsTunnel)}";
    public string ErrorsChip => $"Errors {_all.Count(f => !f.Flow.IsTunnel && f.IsError)}";
    public string RulesSummary
    {
        get
        {
            var parts = new List<string>();
            var m = Settings.Mocks.Count(x => x.Enabled); var b = Settings.Breakpoints.Count(x => x.Enabled);
            if (m > 0) parts.Add($"{m} mock{(m == 1 ? "" : "s")}");
            if (b > 0) parts.Add($"{b} breakpoint{(b == 1 ? "" : "s")}");
            if (Settings.DelayMs > 0) parts.Add($"+{Settings.DelayMs} ms delay");
            if (Settings.DecryptOnlyHosts.Count > 0) parts.Add("decrypt-only list");
            return parts.Count == 0 ? "" : "Active: " + string.Join(", ", parts);
        }
    }
    public bool HasRules => RulesSummary.Length > 0;

    public MainViewModel()
    {
        Settings = _settingsStore.Load();
        _engine.ApplySettings(Settings);
        foreach (var f in _store.Load()) _favorites[f.Key] = f;
        _engine.FlowStarted += f => Dispatcher.UIThread.Post(() => AddFlow(f));
        _engine.FlowUpdated += f => Dispatcher.UIThread.Post(() => UpdateFlow(f));
        _engine.BreakpointHandler = OnBreakpoint;
        _appTimer.Tick += async (_, _) => await RefreshAppsAsync();
        _appTimer.Start();
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        _ = InitAsync();
        Tick();
    }

    async Task InitAsync()
    {
        _installed = await Task.Run(AppCatalog.GetInstalled);
        await RefreshAppsAsync();
        await CheckCertAsync();
        await CheckLeftoverProxyAsync();
    }

    public static Func<(bool Leftover, string Detail)> DetectLeftoverProxy { get; set; } = SystemProxy.DetectLeftover;
    public static Func<(bool Ok, string Message)> RestoreProxy { get; set; } = SystemProxy.Disable;
    [ObservableProperty] string _leftoverProxy = "";
    public bool HasLeftoverProxy => LeftoverProxy.Length > 0;
    partial void OnLeftoverProxyChanged(string value) => OnPropertyChanged(nameof(HasLeftoverProxy));

    public async Task CheckLeftoverProxyAsync()
    {
        if (IsCapturing) { LeftoverProxy = ""; return; }
        var (left, detail) = await Task.Run(DetectLeftoverProxy);
        LeftoverProxy = left ? detail : "";
    }

    [RelayCommand]
    async Task FixLeftoverProxy()
    {
        var (ok, msg) = await Task.Run(RestoreProxy);
        await CheckLeftoverProxyAsync();
        Status = ok && !HasLeftoverProxy ? "Proxy turned off. Your apps connect directly again." : "Could not turn the proxy off. " + msg;
    }

    public async Task CheckCertAsync()
    {
        try
        {
            _engine.EnsureRootCertificate();
            var cert = _engine.RootCertificate!;
            CertTrusted = await Task.Run(() => CertificateService.IsTrusted(cert));
        }
        catch { CertTrusted = false; }
    }

    partial void OnCertTrustedChanged(bool value) => OnPropertyChanged(nameof(ShowSetup));
    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(RecordText));
    partial void OnIsPausedChanged(bool value) { _engine.Paused = value; OnPropertyChanged(nameof(PauseText)); }
    partial void OnShowInstalledChanged(bool value) { RebuildApps(); OnPropertyChanged(nameof(ShowRunningTab)); }
    public bool ShowRunningTab => !ShowInstalled;
    partial void OnAppSearchChanged(string value) => RebuildApps();
    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnFilterKindChanged(FlowFilterKind value)
    {
        ApplyFilter();
        foreach (var p in new[] { nameof(IsAllFilter), nameof(IsErrorsFilter), nameof(IsJsonFilter), nameof(IsImagesFilter), nameof(IsSlowFilter) }) OnPropertyChanged(p);
    }
    public bool IsAllFilter => FilterKind == FlowFilterKind.All;
    public bool IsErrorsFilter => FilterKind == FlowFilterKind.Errors;
    public bool IsJsonFilter => FilterKind == FlowFilterKind.Json;
    public bool IsImagesFilter => FilterKind == FlowFilterKind.Images;
    public bool IsSlowFilter => FilterKind == FlowFilterKind.Slow;
    partial void OnShowTreeChanged(bool value) { OnPropertyChanged(nameof(TreeToggleText)); NotifyBodyViews(); }
    partial void OnJwtTextChanged(string value) => OnPropertyChanged(nameof(HasJwt));
    partial void OnResponseImageChanged(Bitmap? value) => NotifyBodyViews();
    partial void OnSelectedFlowChanged(FlowVm? value) { OnPropertyChanged(nameof(HasSelection)); LoadInspector(); }

    void NotifyBodyViews()
    {
        foreach (var p in new[] { nameof(RequestIsJson), nameof(ResponseIsJson), nameof(RequestIsText), nameof(ResponseIsText), nameof(HasResponseImage) })
            OnPropertyChanged(p);
    }

    void Tick()
    {
        RecordingText = IsCapturing && _engine.CaptureStartedAt is { } t
            ? (IsPaused ? "Paused " : "Recording ") + (DateTime.Now - t).ToString(@"mm\:ss") : "";
        var visible = _all.Where(f => !f.Flow.IsTunnel).ToList();
        var bytes = visible.Sum(f => (long)(f.Flow.ResponseBody?.Length ?? 0) + (f.Flow.RequestBody?.Length ?? 0));
        var apps = SelectedKeys().Length;
        FooterText = $"{(IsCapturing ? (IsPaused ? "Paused" : "Capturing") : "Not capturing")}  ·  {apps} app{(apps == 1 ? "" : "s")}  ·  {visible.Count} request{(visible.Count == 1 ? "" : "s")}  ·  {Formatting.Size(bytes)}"
            + (IsCapturing ? $"  ·  {_engine.IgnoredConnections} other-app connections ignored" : "");
    }

    // ---------- apps ----------
    async Task RefreshAppsAsync()
    {
        var running = await AppCatalog.GetRunningAsync();
        if (running.Count == _running.Count && running.Select(r => r.Key).SequenceEqual(_running.Select(r => r.Key))) return;
        _running = running;
        RebuildApps();
    }

    bool Selected(string key) => _selected.TryGetValue(key, out var s) && s;
    bool IsRunningKey(string key) => _running.Any(r => r.Key == key || AppMatcher.Matches(r.Key, key));

    void RebuildApps()
    {
        var q = AppSearch.Trim();
        bool Match(AppEntry a) => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        var source = ShowInstalled ? _installed.Select(i => i with { IsRunning = IsRunningKey(i.Key) }) : _running;

        AppItemVm Make(AppEntry a, bool run)
        {
            var vm = new AppItemVm(a.Name, a.Key, run, Selected(a.Key), _favorites.ContainsKey(a.Key), OnSelectionChanged, OnFavoriteToggled)
            { Count = _counts.TryGetValue(a.Key, out var c) ? c : 0 };
            _ = LoadIcon(vm);
            return vm;
        }

        Favorites.Clear();
        foreach (var f in _favorites.Values.Where(Match).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            Favorites.Add(Make(f, IsRunningKey(f.Key)));
        Others.Clear();
        foreach (var a in source.Where(a => !_favorites.ContainsKey(a.Key) && Match(a)))
            Others.Add(Make(a, a.IsRunning));
        UpdateSummary();
    }

    static async Task LoadIcon(AppItemVm vm)
    {
        var bmp = await AppIcons.GetAsync(vm.Key);
        if (bmp != null) Dispatcher.UIThread.Post(() => vm.Icon = bmp);
    }

    void OnSelectionChanged(AppItemVm item)
    {
        _selected[item.Key] = item.IsSelected;
        foreach (var other in Favorites.Concat(Others).Where(o => o.Key == item.Key && o != item))
            if (other.IsSelected != item.IsSelected) other.IsSelected = item.IsSelected;
        if (IsCapturing) _engine.SetSelectedApps(SelectedKeys());
        UpdateSummary();
    }

    void OnFavoriteToggled(AppItemVm item)
    {
        if (!_favorites.Remove(item.Key)) _favorites[item.Key] = new AppEntry(item.Name, item.Key);
        _store.Save(_favorites.Values);
        RebuildApps();
    }

    string[] SelectedKeys() => _selected.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();

    void UpdateSummary()
    {
        var n = SelectedKeys().Length;
        SelectedSummary = n == 0 ? "No apps selected" : n == 1 ? "1 app selected" : $"{n} apps selected";
    }

    [RelayCommand] void ShowRunning() => ShowInstalled = false;
    [RelayCommand] void ShowInstalledApps() => ShowInstalled = true;

    // ---------- capture ----------
    [RelayCommand]
    async Task ToggleCaptureAsync()
    {
        if (IsCapturing) { await StopAsync(); return; }
        if (SelectedKeys().Length == 0) { Status = "Select at least one app on the left first."; return; }

        Status = "Starting proxy…";
        try
        {
            _engine.Start();
            var cert = _engine.RootCertificate!;
            var trusted = await Task.Run(() => CertificateService.IsTrusted(cert));
            if (!trusted)
            {
                Status = "Trusting the TrafficScope certificate (admin approval needed)…";
                await Task.Run(() => CertificateService.Install(cert, FavoritesStore.DataDir));
                trusted = await Task.Run(() => CertificateService.IsTrusted(cert));
            }
            CertTrusted = trusted;
            if (!trusted)
            {
                // Never intercept HTTPS without a trusted certificate: apps would show "certificate invalid" errors.
                _engine.Stop();
                Status = "Not started: the TrafficScope certificate is not trusted yet, so your apps would show certificate errors. Use Install certificate, then press Record again.";
                return;
            }
            Status = "Setting system proxy…";
            var (pok, pmsg) = await Task.Run(() => SystemProxy.Enable(_engine.Port));
            if (!pok) { _engine.Stop(); Status = "Could not set the system proxy. " + pmsg; return; }
            _engine.BeginCapture(SelectedKeys());
            IsPaused = false;
            IsCapturing = true;
            Status = $"Recording since {_engine.CaptureStartedAt:HH:mm:ss}. Only selected apps are recorded."
                + (OperatingSystem.IsMacOS() && !SystemProxy.LastRunWithoutPrompt ? " Tip: to stop the password prompts, open Tools > Settings > Proxy permission." : "");
            Tick();
        }
        catch (Exception ex) { Status = "Failed to start: " + ex.Message; }
    }

    public async Task StopAsync()
    {
        _engine.EndCapture();
        await Task.Run(SystemProxy.Disable);
        _engine.Stop();
        IsCapturing = false;
        IsPaused = false;
        Status = "Stopped. System proxy restored.";
        Tick();
    }

    [RelayCommand] void TogglePause() { if (IsCapturing) IsPaused = !IsPaused; Tick(); }

    [RelayCommand]
    void Clear()
    {
        _all.Clear(); Flows.Clear(); SelectedFlow = null; _counts.Clear();
        foreach (var a in Favorites.Concat(Others)) a.Count = 0;
        NotifyCounts();
    }

    // ---------- flows ----------
    public void AddFlow(Flow f)
    {
        var vm = new FlowVm(f);
        _all.Add(vm);
        if (f.Process != null)
        {
            var key = _selected.Keys.FirstOrDefault(k => AppMatcher.Matches(f.Process.Path, k));
            if (key != null)
            {
                _counts[key] = _counts.TryGetValue(key, out var c) ? c + 1 : 1;
                foreach (var a in Favorites.Concat(Others).Where(a => a.Key == key)) a.Count = _counts[key];
            }
        }
        if (Passes(vm)) Flows.Add(vm);
        NotifyCounts();
        SelectedFlow ??= vm;
    }

    void UpdateFlow(Flow f)
    {
        var vm = _all.FirstOrDefault(x => x.Flow == f);
        if (vm == null) return;
        vm.Refresh();
        if (SelectedFlow == vm) LoadInspector();
        NotifyCounts();
    }

    void NotifyCounts()
    {
        OnPropertyChanged(nameof(AllChip)); OnPropertyChanged(nameof(ErrorsChip));
        OnPropertyChanged(nameof(TunnelText)); OnPropertyChanged(nameof(TunnelCount));
        Tick();
    }

    bool Passes(FlowVm f) =>
        (ShowTunnels || !f.Flow.IsTunnel) && FlowFilter.Matches(f.Flow, FilterKind) && FlowFilter.MatchesText(f.Flow, FilterText);

    void ApplyFilter()
    {
        var keep = SelectedFlow;
        Flows.Clear();
        foreach (var f in _all.Where(Passes)) Flows.Add(f);
        if (keep != null && Flows.Contains(keep)) SelectedFlow = keep;
    }

    [RelayCommand] void SetFilter(string kind) => FilterKind = Enum.Parse<FlowFilterKind>(kind);

    public void SetMultiSelection(IEnumerable<FlowVm> items)
    {
        _multi = items.ToList();
        OnPropertyChanged(nameof(CanCompare));
    }

    // ---------- inspector ----------
    void LoadInspector()
    {
        var f = SelectedFlow?.Flow;
        RequestTree.Clear(); ResponseTree.Clear(); RequestHeaders.Clear(); ResponseHeaders.Clear();
        QueryParams.Clear(); FormFields.Clear(); RequestCookies.Clear(); ResponseCookies.Clear(); TimingRows.Clear();
        ResponseImage = null; JwtText = "";
        if (f == null)
        {
            RequestText = ResponseText = RequestBodyText = ResponseBodyText = RequestHeadersText = ResponseHeadersText = "";
            NotifyInspector(); return;
        }
        RequestText = Formatting.RawRequest(f);
        ResponseText = Formatting.RawResponse(f);
        RequestBodyText = f.RequestBody is not { Length: > 0 } ? "No request body"
            : FormParser.IsForm(f.RequestContentType)
                ? "Form fields (decoded, see Raw for the original)\n\n" + string.Join("\n", FormParser.Parse(System.Text.Encoding.UTF8.GetString(f.RequestBody)).Select(p => $"{p.Name} = {p.Value}"))
                : Formatting.BodyText(f.RequestContentType, f.RequestBody);
        ResponseBodyText = f.IsTunnel ? ResponseText : f.ResponseBody is { Length: > 0 } ? Formatting.BodyText(f.ResponseContentType, f.ResponseBody)
            : f.StatusCode == null ? (f.Error ?? "Waiting for response…") : "No response body";
        RequestHeadersText = string.Join("\n", f.RequestHeaders.Select(h => $"{h.Key}: {h.Value}"));
        ResponseHeadersText = string.Join("\n", f.ResponseHeaders.Select(h => $"{h.Key}: {h.Value}"));

        if (JsonTree.LooksLikeJson(f.RequestContentType, f.RequestBody) && JsonTree.Build(f.RequestBody) is { } rq) RequestTree.Add(new JsonNodeVm(rq, 0));
        if (JsonTree.LooksLikeJson(f.ResponseContentType, f.ResponseBody) && JsonTree.Build(f.ResponseBody) is { } rs) ResponseTree.Add(new JsonNodeVm(rs, 0));
        if ((f.ResponseContentType ?? "").StartsWith("image/") && f.ResponseBody is { Length: > 0 } img && !(f.ResponseContentType ?? "").Contains("svg"))
        {
            try { using var ms = new MemoryStream(img); ResponseImage = new Bitmap(ms); } catch { ResponseImage = null; }
        }
        foreach (var h in f.RequestHeaders) RequestHeaders.Add(new NameValue(h.Key, h.Value));
        foreach (var h in f.ResponseHeaders) ResponseHeaders.Add(new NameValue(h.Key, h.Value));
        if (JwtDecoder.FindInHeaders(f.RequestHeaders) is { } jwt)
        {
            var exp = jwt.ExpiresUtc is { } e ? $"Expires {e.ToLocalTime():dd MMM yyyy HH:mm}" + (e < DateTime.UtcNow ? " (expired)" : "") + "\n" : "";
            JwtText = exp + "Header\n" + jwt.HeaderJson + "\n\nPayload\n" + jwt.PayloadJson;
        }
        foreach (var q in FormParser.Query(f.Url)) QueryParams.Add(q);
        if (FormParser.IsForm(f.RequestContentType) && f.RequestBody is { Length: > 0 } fb)
            foreach (var p in FormParser.Parse(System.Text.Encoding.UTF8.GetString(fb))) FormFields.Add(p);
        foreach (var c in CookieParser.Request(f.RequestHeaders)) RequestCookies.Add(c);
        foreach (var c in CookieParser.Response(f.ResponseHeaders)) ResponseCookies.Add(c);

        TimingRows.Add(new("Started", f.StartedAt.ToString("dd MMM yyyy HH:mm:ss.fff")));
        TimingRows.Add(new("Total time", f.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "pending"));
        TimingRows.Add(new("Request size", Formatting.Size(f.RequestBody?.Length ?? 0)));
        TimingRows.Add(new("Response size", Formatting.Size(f.ResponseBody?.Length ?? 0)));
        TimingRows.Add(new("Protocol", f.HttpVersion));
        TimingRows.Add(new("App", f.Process != null ? $"{f.AppName} (pid {f.Process.Pid})" : f.AppName));
        if (f.Process != null) TimingRows.Add(new("Executable", f.Process.Path));
        if (f.Mocked) TimingRows.Add(new("Note", "Answered by a mock rule; the server was not called."));
        if (f.Edited) TimingRows.Add(new("Note", "Changed at a breakpoint."));
        if (Settings.DelayMs > 0) TimingRows.Add(new("Slow network", $"+{Settings.DelayMs} ms added per request"));
        NotifyInspector();
    }

    void NotifyInspector()
    {
        NotifyBodyViews();
        foreach (var p in new[] { nameof(HasForm), nameof(HasQuery), nameof(HasParams), nameof(NoParams), nameof(HasJwt) }) OnPropertyChanged(p);
    }

    [RelayCommand] void ToggleTree() => ShowTree = !ShowTree;
    [RelayCommand] void ExpandAll() { foreach (var n in RequestTree.Concat(ResponseTree)) n.SetExpanded(true); }
    [RelayCommand] void CollapseAll() { foreach (var n in RequestTree.Concat(ResponseTree)) { n.SetExpanded(false); n.IsExpanded = true; } }

    // ---------- copy ----------
    async Task Copy(string text, string message)
    {
        if (Ui != null) await Ui.SetClipboardAsync(text);
        await ShowToast(message);
    }

    public async Task ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;
        Toast = message;
        try { await Task.Delay(2500, token); Toast = ""; } catch (TaskCanceledException) { }
    }

    Flow? Sel => SelectedFlow?.Flow;
    bool M => Settings.MaskSecrets;
    string Hdr(IEnumerable<KeyValuePair<string, string>> h) => string.Join("\n", Redactor.Apply(h, M).Select(x => $"{x.Key}: {x.Value}"));

    [RelayCommand] Task CopyRequest() => Sel == null ? Task.CompletedTask : Copy(Formatting.RawRequest(Sel, true, M), "Request copied");
    [RelayCommand] Task CopyResponse() => Sel == null ? Task.CompletedTask : Copy(Formatting.RawResponse(Sel, true, M), "Response copied");
    [RelayCommand] Task CopyRequestBody() => Sel == null ? Task.CompletedTask : Copy(Formatting.BodyForCopy(Sel.RequestContentType, Sel.RequestBody), "Request body copied");
    [RelayCommand] Task CopyResponseBody() => Sel == null ? Task.CompletedTask : Copy(Formatting.BodyForCopy(Sel.ResponseContentType, Sel.ResponseBody), "Response body copied");
    [RelayCommand] Task CopyRequestHeaders() => Sel == null ? Task.CompletedTask : Copy(Hdr(Sel.RequestHeaders), "Request headers copied");
    [RelayCommand] Task CopyResponseHeaders() => Sel == null ? Task.CompletedTask : Copy(Hdr(Sel.ResponseHeaders), "Response headers copied");
    [RelayCommand] Task CopyUrl() => Sel == null ? Task.CompletedTask : Copy(Sel.Url, "URL copied");
    [RelayCommand] Task CopyCurl() => Sel == null ? Task.CompletedTask : Copy(CurlBuilder.Build(Sel, M), "cURL copied");
    [RelayCommand] Task CopyJwt() => JwtText.Length == 0 ? Task.CompletedTask : Copy(JwtText, "Token details copied");

    public bool PostmanConnected => !string.IsNullOrEmpty(Settings.PostmanWorkspaceId) && !string.IsNullOrEmpty(PostmanSetup.GetKey());

    [RelayCommand]
    async Task SendToPostman()
    {
        if (Sel == null) { await ShowToast("Select a request first."); return; }
        if (Sel.IsTunnel) { await ShowToast("Tunnels are not decrypted, so there is no request to send."); return; }
        var key = PostmanSetup.GetKey();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(Settings.PostmanWorkspaceId))
        {
            await ShowToast("Connect Postman once in Settings, then Send to Postman saves requests there.");
            await OpenRules(2);
            if (string.IsNullOrEmpty(Settings.PostmanWorkspaceId)) return;
            key = PostmanSetup.GetKey();
            if (string.IsNullOrEmpty(key)) return;
        }
        var flow = Sel;
        Toast = "Saving to Postman…";
        var r = await PostmanSetup.ClientFactory(key).SaveRequestAsync(Settings.PostmanWorkspaceId, Settings.PostmanCollection, flow, M);
        if (r.Ok)
        {
            Status = $"{r.Message} (workspace {Settings.PostmanWorkspaceName}). Open it in Postman and press Send.";
            await ShowToast(r.Message + (M ? " (secrets masked)" : "") + ". Opening Postman…");
            OpenPostmanApp();
        }
        else { Status = r.Message; await ShowToast(r.Message); }
    }

    public Action OpenPostmanApp { get; set; } = PostmanLauncher.Open;

    // ---------- replay, edit, compare, save ----------
    [RelayCommand]
    async Task Replay()
    {
        if (Sel == null || Sel.IsTunnel) return;
        var r = await Replayer.ReplayAsync(Sel, _engine.NextId());
        AddFlow(r);
        SelectedFlow = _all.Last();
        await ShowToast(r.Error == null ? $"Replayed: {r.StatusCode}" : "Replay failed: " + r.Error);
    }

    [RelayCommand]
    async Task EditAndResend()
    {
        if (Ui == null) return;
        var src = Sel;
        var m = new EditModel
        {
            Mode = EditMode.Compose, Title = "Edit and resend",
            Method = src?.Method ?? "GET", Url = src?.Url ?? "https://",
            Headers = src == null ? "" : string.Join("\n", src.RequestHeaders.Select(h => $"{h.Key}: {h.Value}")),
            Body = src?.RequestBody is { Length: > 0 } b ? System.Text.Encoding.UTF8.GetString(b) : "",
        };
        await Ui.EditAsync(m);
        if (!m.Accepted) return;
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out _)) { await ShowToast("That URL is not valid."); return; }
        var headers = m.Headers.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(':'))
            .Select(l => { var i = l.IndexOf(':'); return new KeyValuePair<string, string>(l[..i].Trim(), l[(i + 1)..].Trim()); }).ToList();
        var r = await Replayer.SendAsync(_engine.NextId(), m.Method.Trim().ToUpperInvariant(), m.Url.Trim(), headers,
            m.Body.Length > 0 ? System.Text.Encoding.UTF8.GetBytes(m.Body) : null, "Resent");
        AddFlow(r);
        SelectedFlow = _all.Last();
        await ShowToast(r.Error == null ? $"Sent: {r.StatusCode}" : "Send failed: " + r.Error);
    }

    [RelayCommand]
    void Compare()
    {
        if (_multi.Count != 2 || Ui == null) { _ = ShowToast("Select two requests (Ctrl/Cmd+click) to compare."); return; }
        Ui.ShowCompare(_multi[0].Flow, _multi[1].Flow);
    }

    [RelayCommand]
    async Task SaveHar()
    {
        if (Ui == null) return;
        var flows = _all.Where(f => !f.Flow.IsTunnel).Select(f => f.Flow).ToList();
        if (flows.Count == 0) { await ShowToast("Nothing to save yet."); return; }
        var path = await Ui.PickSaveFileAsync($"TrafficScope-{DateTime.Now:yyyyMMdd-HHmm}.har");
        if (path == null) return;
        await File.WriteAllTextAsync(path, HarExporter.Build(flows, M));
        await ShowToast($"Saved {flows.Count} requests" + (M ? " (secrets masked)" : ""));
    }

    // ---------- rules ----------
    [RelayCommand] Task OpenMocks() => OpenRules(0);
    [RelayCommand] Task OpenBreakpoints() => OpenRules(1);
    [RelayCommand] Task OpenSettings() => OpenRules(2);

    async Task OpenRules(int tab)
    {
        if (Ui == null) return;
        await Ui.OpenRulesAsync(Settings, tab);
        SaveSettings();
        ApplyTheme();
    }

    [RelayCommand]
    async Task MockFromSelected()
    {
        if (Sel == null || Ui == null) return;
        var path = Sel.Path.Split('?')[0];
        Settings.Mocks.Add(new MockRule
        {
            Name = "Mock " + path, Method = Sel.Method, UrlContains = path, Status = Sel.StatusCode ?? 200,
            ContentType = Sel.ResponseContentType ?? "application/json",
            Body = Sel.ResponseBody is { Length: > 0 } b && Formatting.IsText(Sel.ResponseContentType, b) ? System.Text.Encoding.UTF8.GetString(b) : "{}",
        });
        await OpenRules(0);
    }

    [RelayCommand]
    async Task BreakpointFromSelected()
    {
        if (Sel == null || Ui == null) return;
        Settings.Breakpoints.Add(new BreakpointRule { UrlContains = Sel.Path.Split('?')[0], Method = Sel.Method, OnRequest = true });
        await OpenRules(1);
    }

    public void SaveSettings()
    {
        _settingsStore.Save(Settings);
        _engine.ApplySettings(Settings);
        OnPropertyChanged(nameof(RulesSummary)); OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(MaskSecrets)); OnPropertyChanged(nameof(ShowTunnels));
    }

    public void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = Settings.Theme switch
            {
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                _ => Avalonia.Styling.ThemeVariant.Default
            };
    }

    async Task OnBreakpoint(BreakpointContext ctx)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Ui == null) return;
            BreakpointBanner = $"Paused at {(ctx.Stage == BreakStage.Request ? "request" : "response")}: {ctx.Method} {ctx.Url}";
            var m = new EditModel
            {
                Mode = ctx.Stage == BreakStage.Request ? EditMode.BreakRequest : EditMode.BreakResponse,
                Title = ctx.Stage == BreakStage.Request ? "Breakpoint: edit request before it is sent" : "Breakpoint: edit response before the app gets it",
                Method = ctx.Method, Url = ctx.Url, Status = ctx.StatusCode, Headers = ctx.HeadersText, Body = ctx.BodyText,
            };
            await Ui.EditAsync(m);
            if (m.Abort) ctx.Abort = true;
            else if (m.Accepted)
            {
                ctx.Method = m.Method.Trim().ToUpperInvariant(); ctx.Url = m.Url.Trim(); ctx.StatusCode = m.Status;
                ctx.HeadersText = m.Headers; ctx.BodyText = m.Body;
            }
            BreakpointBanner = "";
        });
    }

    [RelayCommand]
    async Task InstallCertificate()
    {
        _engine.EnsureRootCertificate();
        Status = "Installing certificate (approve the prompts)…";
        var (ok, msg) = await Task.Run(() => CertificateService.Install(_engine.RootCertificate!, FavoritesStore.DataDir));
        await CheckCertAsync();
        Status = CertTrusted ? "Certificate installed and trusted. Pick apps and press Record."
            : "Certificate is not trusted yet" + (ok ? ". If it still is not trusted: open Keychain Access, login keychain, double-click TrafficScope Root CA, Trust, When using this certificate: Always Trust." : ". " + msg);
    }

    public void Dispose()
    {
        _appTimer.Stop(); _clock.Stop();
        if (IsCapturing) SystemProxy.Disable();
        _engine.Dispose();
    }
}
