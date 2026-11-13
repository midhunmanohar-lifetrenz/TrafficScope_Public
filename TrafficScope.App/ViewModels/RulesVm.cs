using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

public partial class RulesVm : ObservableObject
{
    readonly AppSettings _s;
    public RulesVm(AppSettings s, int tab)
    {
        _s = s;
        SelectedTab = tab;
        foreach (var m in s.Mocks) Mocks.Add(new MockRuleVm(m));
        foreach (var b in s.Breakpoints) Breakpoints.Add(new BreakpointRuleVm(b));
        SelectedMock = Mocks.LastOrDefault();
        SelectedBreakpoint = Breakpoints.LastOrDefault();
        _decryptOnlyText = string.Join("\n", s.DecryptOnlyHosts);
        _delayText = s.DelayMs.ToString();
        _maskSecrets = s.MaskSecrets;
        _theme = s.Theme;
        _postmanCollection = string.IsNullOrWhiteSpace(s.PostmanCollection) ? "TrafficScope" : s.PostmanCollection;
        var hasKey = !string.IsNullOrEmpty(PostmanSetup.GetKey());
        _isPostmanConnected = hasKey && s.PostmanWorkspaceId.Length > 0;
        if (_isPostmanConnected)
        {
            var ws = new PostmanWorkspace(s.PostmanWorkspaceId, s.PostmanWorkspaceName, "");
            Workspaces.Add(ws); _selectedWorkspace = ws;
            _postmanStatus = $"Connected as {s.PostmanUser}.";
        }
        else _postmanStatus = "Not connected.";
        LoadPermission();
    }

    // ---------- certificate ----------
    /// <summary>Set by the main window: removes trust in the TrafficScope root certificate.</summary>
    public static Func<(bool Ok, string Message)> RemoveCertificate { get; set; } = () => (false, "Not available.");
    [ObservableProperty] string _certificateStatus = "";
    [ObservableProperty] bool _certificateBusy;

    [RelayCommand]
    async Task RemoveCertificateAsync()
    {
        CertificateBusy = true;
        CertificateStatus = "Removing (approve the system prompt)…";
        var (_, msg) = await Task.Run(RemoveCertificate);
        CertificateStatus = msg;
        CertificateBusy = false;
    }

    // ---------- proxy permission (macOS) ----------
    public static Func<bool> CheckPermission { get; set; } = ProxyPermission.IsInstalled;
    public static Func<(bool Ok, string Message)> InstallPermission { get; set; } = ProxyPermission.Install;
    public static Func<(bool Ok, string Message)> RemovePermission { get; set; } = ProxyPermission.Remove;
    public bool IsMac => OperatingSystem.IsMacOS();
    [ObservableProperty] bool _permissionInstalled;
    [ObservableProperty] bool _permissionBusy;
    [ObservableProperty] string _permissionStatus = "";
    public bool PermissionMissing => !PermissionInstalled;
    partial void OnPermissionInstalledChanged(bool value) => OnPropertyChanged(nameof(PermissionMissing));

    void LoadPermission()
    {
        PermissionInstalled = IsMac && CheckPermission();
        PermissionStatus = PermissionInstalled
            ? "On: Record and Stop switch the proxy without asking for your password."
            : "Off: macOS asks for your password each time you press Record or Stop.";
    }

    [RelayCommand]
    async Task InstallProxyPermission()
    {
        PermissionBusy = true; PermissionStatus = "Waiting for your password (asked once)…";
        var (ok, msg) = await Task.Run(InstallPermission);
        PermissionBusy = false;
        LoadPermission();
        if (!ok) PermissionStatus = "Not changed. " + msg;
    }

    [RelayCommand]
    async Task RemoveProxyPermission()
    {
        PermissionBusy = true; PermissionStatus = "Removing (asks for your password)…";
        var (ok, msg) = await Task.Run(RemovePermission);
        PermissionBusy = false;
        LoadPermission();
        if (!ok) PermissionStatus = "Not changed. " + msg;
    }

    public ObservableCollection<PostmanWorkspace> Workspaces { get; } = new();
    [ObservableProperty] string _postmanKeyInput = "";
    [ObservableProperty] string _postmanStatus;
    [ObservableProperty] string _postmanCollection;
    [ObservableProperty] bool _isPostmanConnected;
    [ObservableProperty] bool _isPostmanBusy;
    [ObservableProperty] PostmanWorkspace? _selectedWorkspace;
    string _postmanUser = "";
    public bool IsPostmanDisconnected => !IsPostmanConnected;
    partial void OnIsPostmanConnectedChanged(bool value) => OnPropertyChanged(nameof(IsPostmanDisconnected));

    [RelayCommand]
    async Task ConnectPostman()
    {
        var key = string.IsNullOrWhiteSpace(PostmanKeyInput) ? PostmanSetup.GetKey() : PostmanKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(key)) { PostmanStatus = "Paste your Postman API key first (it starts with PMAK-)."; return; }
        IsPostmanBusy = true;
        PostmanStatus = "Checking the key with Postman…";
        try
        {
            var client = PostmanSetup.ClientFactory(key);
            _postmanUser = await client.GetUserAsync();
            var list = await client.GetWorkspacesAsync();
            if (!PostmanSetup.SetKey(key)) { PostmanStatus = "Could not store the key securely on this computer."; return; }
            Workspaces.Clear();
            foreach (var w in list) Workspaces.Add(w);
            SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == _s.PostmanWorkspaceId)
                ?? Workspaces.FirstOrDefault(w => w.Type == "personal") ?? Workspaces.FirstOrDefault();
            PostmanKeyInput = "";
            IsPostmanConnected = SelectedWorkspace != null;
            PostmanStatus = IsPostmanConnected ? $"Connected as {_postmanUser}. Choose the workspace to save into." : "Connected, but your account has no workspaces.";
        }
        catch (PostmanException ex) { PostmanStatus = ex.Message; }
        catch (Exception ex) { PostmanStatus = "Could not reach Postman: " + ex.Message; }
        finally { IsPostmanBusy = false; }
    }

    [RelayCommand]
    void DisconnectPostman()
    {
        PostmanSetup.DeleteKey();
        Workspaces.Clear(); SelectedWorkspace = null; IsPostmanConnected = false; _postmanUser = "";
        _s.PostmanWorkspaceId = _s.PostmanWorkspaceName = _s.PostmanUser = "";
        PostmanStatus = "Disconnected. The key was removed from this computer.";
    }

    public ObservableCollection<MockRuleVm> Mocks { get; } = new();
    public ObservableCollection<BreakpointRuleVm> Breakpoints { get; } = new();
    public string[] Themes { get; } = { "System", "Light", "Dark" };
    public string[] DelayPresets { get; } = { "0", "300", "1000", "3000" };

    [ObservableProperty] int _selectedTab;
    [ObservableProperty] MockRuleVm? _selectedMock;
    [ObservableProperty] BreakpointRuleVm? _selectedBreakpoint;
    [ObservableProperty] string _decryptOnlyText;
    [ObservableProperty] string _delayText;
    [ObservableProperty] bool _maskSecrets;
    [ObservableProperty] string _theme;

    public bool HasMock => SelectedMock != null;
    public bool HasBreakpoint => SelectedBreakpoint != null;
    partial void OnSelectedMockChanged(MockRuleVm? value) => OnPropertyChanged(nameof(HasMock));
    partial void OnSelectedBreakpointChanged(BreakpointRuleVm? value) => OnPropertyChanged(nameof(HasBreakpoint));

    [RelayCommand] void AddMock() { var vm = new MockRuleVm(new MockRule { UrlContains = "/api/" }); Mocks.Add(vm); SelectedMock = vm; }
    [RelayCommand] void DeleteMock() { if (SelectedMock != null) { Mocks.Remove(SelectedMock); SelectedMock = Mocks.LastOrDefault(); } }
    [RelayCommand] void AddBreakpoint() { var vm = new BreakpointRuleVm(new BreakpointRule { UrlContains = "/api/" }); Breakpoints.Add(vm); SelectedBreakpoint = vm; }
    [RelayCommand] void DeleteBreakpoint() { if (SelectedBreakpoint != null) { Breakpoints.Remove(SelectedBreakpoint); SelectedBreakpoint = Breakpoints.LastOrDefault(); } }
    [RelayCommand] void SetDelay(string ms) => DelayText = ms;

    /// <summary>Writes the edits back into the settings object.</summary>
    public void Commit()
    {
        _s.Mocks = Mocks.Select(m => m.Rule).ToList();
        _s.Breakpoints = Breakpoints.Select(b => b.Rule).ToList();
        _s.DecryptOnlyHosts = DecryptOnlyText.Split('\n', ',').Select(h => h.Trim()).Where(h => h.Length > 0).Distinct().ToList();
        _s.DelayMs = int.TryParse(DelayText, out var d) ? Math.Clamp(d, 0, 60000) : 0;
        _s.MaskSecrets = MaskSecrets;
        _s.Theme = Theme;
        _s.PostmanCollection = string.IsNullOrWhiteSpace(PostmanCollection) ? "TrafficScope" : PostmanCollection.Trim();
        if (IsPostmanConnected && SelectedWorkspace != null)
        {
            _s.PostmanWorkspaceId = SelectedWorkspace.Id; _s.PostmanWorkspaceName = SelectedWorkspace.Name;
            if (_postmanUser.Length > 0) _s.PostmanUser = _postmanUser;
        }
    }
}
