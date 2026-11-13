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
    Task<string?> PickOpenFileAsync();
    Task EditAsync(EditModel model);
    Task<bool> OpenRulesAsync(AppSettings settings, int tab);
    void ShowCompare(Flow a, Flow b);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

public enum NoticeKind { Info, Success, Warning, Error }

public partial class MainViewModel : ObservableObject, IDisposable
{
    readonly ProxyEngine _engine = new(FavoritesStore.DataDir);
    readonly FavoritesStore _store = new();
    readonly SettingsStore _settingsStore = new();
    /// <summary>Oldest requests are dropped beyond this, so a long session cannot use unlimited memory.</summary>
    public const int MaxFlows = 5000;
    readonly List<FlowVm> _all = new();
    readonly Dictionary<Flow, FlowVm> _byFlow = new();
    bool _countsQueued;
    public int DroppedFlows { get; private set; }
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
    [ObservableProperty] string _requestHexText = "";
    [ObservableProperty] string _responseHexText = "";
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
    [ObservableProperty] string _recordPillText = "Record";
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
    public string CertButtonText => CertTrusted ? "Verified" : "Install certificate";
    public string CertButtonTip => CertTrusted
        ? "The TrafficScope certificate is installed and trusted. Click to check again."
        : "Trust the TrafficScope certificate so HTTPS can be read (needed once)";

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
            var maps = Settings.Maps.Count(x => x.Enabled);
            if (maps > 0) parts.Add($"{maps} map{(maps == 1 ? "" : "s")}");
            if (Settings.DelayMs > 0) parts.Add($"+{Settings.DelayMs} ms delay");
            if (Settings.DecryptOnlyHosts.Count > 0) parts.Add("decrypt-only list");
            return parts.Count == 0 ? "" : "Active: " + string.Join(", ", parts);
        }
    }
    public bool HasRules => RulesSummary.Length > 0;

    public string StartHintText => CertTrusted
        ? "No requests yet. Pick apps on the left, press Record, then use your app."
        : "First press Install certificate (top right) once. Then pick apps on the left, press Record and use your app.";

    [RelayCommand]
    async Task ResetAll()
    {
        if (Ui == null) return;
        var ok = await Ui.ConfirmAsync("Reset TrafficScope?",
            "This clears the recorded requests, mocks, breakpoints, map rules, the decrypt-only list, slow network, theme and the other settings, " +
            "and disconnects Postman.\n\nYour favourite apps and the apps you switched on are kept. The certificate stays installed.",
            "Reset all");
        if (!ok) return;
        Clear();
        FilterText = ""; FilterKind = FlowFilterKind.All;
        var d = new AppSettings();
        Settings.Theme = d.Theme; Settings.MaskSecrets = d.MaskSecrets; Settings.ShowTunnels = d.ShowTunnels; Settings.DelayMs = d.DelayMs;
        Settings.DecryptOnlyHosts = d.DecryptOnlyHosts; Settings.Mocks = d.Mocks; Settings.Breakpoints = d.Breakpoints; Settings.Maps = d.Maps;
        Settings.PostmanWorkspaceId = d.PostmanWorkspaceId; Settings.PostmanWorkspaceName = d.PostmanWorkspaceName;
        Settings.PostmanCollection = d.PostmanCollection; Settings.PostmanUser = d.PostmanUser;
        try { PostmanSetup.DeleteKey(); } catch { }
        SaveSettings();
        ApplyTheme();
        ApplyFilter();
        Status = "Everything was reset. Your favourites and selected apps were kept.";
    }

    public MainViewModel()
    {
        Settings = _settingsStore.Load();
        _engine.ApplySettings(Settings);
        foreach (var f in _store.Load()) _favorites[f.Key] = f;
        _engine.FlowStarted += f => Dispatcher.UIThread.Post(() => AddFlow(f));
        _engine.FlowUpdated += f => Dispatcher.UIThread.Post(() => UpdateFlow(f));
        _engine.BreakpointHandler = OnBreakpoint;
        RulesVm.RemoveCertificate = () => IsCapturing
            ? (false, "Press Stop first. Removing the certificate while recording would break the apps being recorded.")
            : CertificateService.Remove(_engine.RootCertificate!, FavoritesStore.DataDir);
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
    public bool HasLeftoverProxy => LeftoverProxy.Length > 0 || ProxyStillOn;
    /// <summary>Stop ran but the system proxy could not be switched off (for example the password prompt was cancelled).</summary>
    [ObservableProperty] bool _proxyStillOn;
    public string LeftoverTitle => ProxyStillOn ? "Your system proxy is still switched on"
        : "Your proxy is still switched on, but nothing is answering on it";
    public string LeftoverDetail => ProxyStillOn
        ? "Recording stopped, but the proxy could not be turned off. Your apps keep working while TrafficScope is open; turn it off before you quit."
        : "A previous session did not close properly, so some apps may have no internet. Proxy: " + LeftoverProxy;
    public string LeftoverButtonText => ProxyStillOn ? "Turn proxy off" : "Fix now";
    partial void OnLeftoverProxyChanged(string value) => NotifyLeftover();
    partial void OnProxyStillOnChanged(bool value) => NotifyLeftover();
    void NotifyLeftover()
    {
        foreach (var p in new[] { nameof(HasLeftoverProxy), nameof(LeftoverTitle), nameof(LeftoverDetail), nameof(LeftoverButtonText) })
            OnPropertyChanged(p);
    }

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
        if (ok) ProxyStillOn = false;
        await CheckLeftoverProxyAsync();
        if (ok && !HasLeftoverProxy) Say("Proxy turned off. Your apps connect directly again.", NoticeKind.Success);
        else Fail("Could not turn the proxy off. " + msg);
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

    partial void OnCertTrustedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSetup)); OnPropertyChanged(nameof(CertButtonText)); OnPropertyChanged(nameof(CertButtonTip));
        OnPropertyChanged(nameof(StartHintText));
    }
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
        RecordPillText = IsCapturing && _engine.CaptureStartedAt is { } t2
            ? "Stop  ·  " + (IsPaused ? "paused " : "") + (DateTime.Now - t2).ToString(@"mm\:ss") : "Record";
        var visible = _all.Where(f => !f.Flow.IsTunnel).ToList();
        var bytes = visible.Sum(f => (long)(f.Flow.ResponseBody?.Length ?? 0) + (f.Flow.RequestBody?.Length ?? 0));
        var apps = SelectedKeys().Length;
        FooterText = $"{(IsCapturing ? (IsPaused ? "Paused" : "Capturing") : "Not capturing")}  ·  {apps} app{(apps == 1 ? "" : "s")}  ·  {visible.Count} request{(visible.Count == 1 ? "" : "s")}  ·  {Formatting.Size(bytes)}"
            + (IsCapturing ? $"  ·  {_engine.IgnoredConnections} other-app connections ignored" : "")
            + (DroppedFlows > 0 ? $"  ·  oldest {DroppedFlows} removed (keeps the latest {MaxFlows})" : "");
    }

    // ---------- apps ----------
    public async Task RefreshAppsAsync()
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

    // ---------- flows ----------
    public void AddFlow(Flow f)
    {
        var vm = new FlowVm(f);
        _all.Add(vm);
        _byFlow[f] = vm;
        if (_all.Count > MaxFlows) DropOldest(_all.Count - MaxFlows);
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
        if (!_byFlow.TryGetValue(f, out var vm)) return;
        vm.Refresh();
        if (SelectedFlow == vm) LoadInspector();
        NotifyCounts();
    }

    void DropOldest(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var old = _all[i];
            _byFlow.Remove(old.Flow);
            Flows.Remove(old);
        }
        _all.RemoveRange(0, n);
        DroppedFlows += n;
    }

    /// <summary>Counts are recomputed once per burst of traffic, not once per request.</summary>
    void NotifyCounts()
    {
        if (_countsQueued) return;
        _countsQueued = true;
        Dispatcher.UIThread.Post(FlushCounts, DispatcherPriority.Background);
    }

    void FlushCounts()
    {
        _countsQueued = false;
        OnPropertyChanged(nameof(AllChip)); OnPropertyChanged(nameof(ErrorsChip));
        OnPropertyChanged(nameof(TunnelText)); OnPropertyChanged(nameof(TunnelCount));
        NotifyEmptyState();
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
        NotifyEmptyState();
    }

    [RelayCommand] void SetFilter(string kind) => FilterKind = Enum.Parse<FlowFilterKind>(kind);

    public void SetMultiSelection(IEnumerable<FlowVm> items)
    {
        _multi = items.ToList();
        OnPropertyChanged(nameof(CanCompare));
    }

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
        Notify(message, NoticeKind.Success);
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
        else Fail(r.Message);
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

    /// <summary>Opens a saved session or any HAR file and adds its requests to the list.</summary>
    [RelayCommand]
    async Task OpenSession()
    {
        if (Ui == null) return;
        var path = await Ui.PickOpenFileAsync();
        if (path == null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 300L * 1024 * 1024) { Fail("That file is larger than 300 MB, which is too big to open."); return; }
            var flows = await Task.Run(() => HarImporter.Parse(File.ReadAllText(path), _engine.NextId));
            if (flows.Count == 0) { Say("That file has no requests in it.", NoticeKind.Warning); return; }
            var first = _all.Count;
            foreach (var f in flows) AddFlow(f);
            if (FilterText.Length > 0 || FilterKind != FlowFilterKind.All) ClearFilters();
            SelectedFlow = _all.Count > 0 ? _all[Math.Min(Math.Max(0, first - DroppedFlows), _all.Count - 1)] : null;
            Say($"Opened {flows.Count} request{(flows.Count == 1 ? "" : "s")} from {Path.GetFileName(path)}."
                + (flows.Count > MaxFlows ? $" Only the latest {MaxFlows} are kept." : ""), NoticeKind.Success);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { Fail("Could not open that file: it is not a valid HAR file. " + (ex is FormatException ? ex.Message : "")); }
        catch (Exception ex) { Fail("Could not open that file. " + ex.Message); }
    }

    public string[] CodeLanguages => CodeGen.Languages;
    [RelayCommand]
    Task CopyCode(string language) => Sel == null || Sel.IsTunnel ? ShowToast("Select a request first.")
        : Copy(CodeGen.Build(language, Sel, M), $"{language} code copied" + (M ? " (secrets masked)" : ""));

    [RelayCommand] Task OpenMaps() => OpenRules(3);

    [RelayCommand]
    async Task MapLocalFromSelected()
    {
        if (Sel == null || Ui == null || Sel.IsTunnel) return;
        var path = Sel.Path.Split('?')[0];
        Settings.Maps.Add(new MapRule { Name = "Map Local " + path, Method = Sel.Method, UrlContains = path, Status = Sel.StatusCode is >= 100 and <= 599 ? Sel.StatusCode.Value : 200 });
        await OpenRules(3);
    }

    [RelayCommand]
    async Task MapRemoteFromSelected()
    {
        if (Sel == null || Ui == null || Sel.IsTunnel) return;
        Settings.Maps.Add(new MapRule { Name = "Map Remote " + Sel.Host, IsRemote = true, UrlContains = Sel.Host });
        await OpenRules(3);
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
        await CheckCertAsync();   // the certificate can be removed from Settings
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
        NotifyEmptyState();
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
        if (CertTrusted)
        {
            await CheckCertAsync();
            if (CertTrusted) { Status = "Certificate verified: installed and trusted on this computer."; return; }
        }
        _engine.EnsureRootCertificate();
        Status = "Installing certificate (approve the prompts)…";
        var (ok, msg) = await Task.Run(() => CertificateService.Install(_engine.RootCertificate!, FavoritesStore.DataDir));
        await CheckCertAsync();
        if (CertTrusted) Say("Certificate installed and trusted. Pick apps and press Record.", NoticeKind.Success);
        else Fail("Certificate is not trusted yet" + (ok ? ". If it still is not trusted: open Keychain Access, login keychain, double-click TrafficScope Root CA, Trust, When using this certificate: Always Trust." : ". " + msg));
    }

    public void Dispose()
    {
        _appTimer.Stop(); _clock.Stop();
        if (IsCapturing || ProxyStillOn) SystemProxy.Disable();
        _engine.Dispose();
    }
}
