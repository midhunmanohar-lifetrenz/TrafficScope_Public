using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

public static class Palette
{
    static readonly string[] Avatars = { "#6D4AFF", "#E8710A", "#2E8B57", "#D9531E", "#1A73E8", "#B0399B", "#0F8B8D", "#8D6E63" };
    public static IBrush Avatar(string name) => Brush.Parse(Avatars[(uint)name.GetHashCode() % Avatars.Length]);
    public static IBrush MethodBg(string m) => Brush.Parse(m switch { "GET" => "#332E9E5B", "POST" => "#336D4AFF", "PUT" or "PATCH" => "#33E0A030", "DELETE" => "#33E24B4A", _ => "#33888780" });
    public static IBrush MethodFg(string m) => Brush.Parse(m switch { "GET" => "#2E9E5B", "POST" => "#7C5CFF", "PUT" or "PATCH" => "#C98A12", "DELETE" => "#E24B4A", _ => "#888780" });
    public static readonly IBrush Ok = Brush.Parse("#2E9E5B");
    public static readonly IBrush Bad = Brush.Parse("#E24B4A");
    public static readonly IBrush Pending = Brush.Parse("#888780");
    public static readonly IBrush Info = Brush.Parse("#1A73E8");
}

public partial class FlowVm : ObservableObject
{
    public FlowVm(Flow flow) { Flow = flow; }
    public Flow Flow { get; }
    public long Id => Flow.Id;
    public string App => Flow.AppName;
    public string Method => Flow.Method;
    public IBrush MethodBg => Palette.MethodBg(Flow.Method);
    public IBrush MethodFg => Palette.MethodFg(Flow.Method);
    public bool IsGet => Flow.Method == "GET";
    public bool IsPost => Flow.Method == "POST";
    public bool IsPut => Flow.Method is "PUT" or "PATCH";
    public bool IsDelete => Flow.Method == "DELETE";
    public string Host => Flow.Host;
    public string PathText => Flow.IsTunnel ? "(tunnel, not decrypted)" : Flow.Path;
    public string Url => Flow.Url;
    public bool IsError => Flow.StatusCode >= 400 || Flow.Error != null;
    public string Status => Flow.Error != null && Flow.StatusCode == null ? "ERR" : Flow.StatusCode?.ToString() ?? "…";
    public IBrush StatusBrush => Flow.StatusCode == null && Flow.Error == null ? Palette.Pending : IsError ? Palette.Bad : Flow.StatusCode >= 300 ? Palette.Info : Palette.Ok;
    bool Done => Flow.StatusCode != null || Flow.Error != null;
    public bool StatusOk => Done && !IsError && Flow.StatusCode < 300;
    public bool StatusBad => Done && IsError;
    public bool StatusInfo => Done && !IsError && Flow.StatusCode >= 300;
    public string Time => Flow.Duration is { } d && !Flow.IsTunnel ? (d.TotalMilliseconds >= 1000 ? $"{d.TotalSeconds:0.0} s" : $"{d.TotalMilliseconds:0} ms") : "";
    public string Size => Formatting.Size(Flow.ResponseBody?.Length);
    public string Type => (Flow.ResponseContentType ?? "").Split(';')[0].Replace("application/", "").Replace("text/", "");
    public string Tag => Flow.IsTunnel ? "Tunnel" : Flow.Mapped.Length > 0 ? Flow.Mapped : Flow.Mocked ? "Mock" : Flow.Edited ? "Edited" : Flow.Source.Length > 0 ? Flow.Source : "";
    public bool HasTag => Tag.Length > 0;

    public void Refresh()
    {
        foreach (var p in new[] { nameof(Status), nameof(StatusBrush), nameof(StatusOk), nameof(StatusBad), nameof(StatusInfo), nameof(IsGet), nameof(IsPost), nameof(IsPut), nameof(IsDelete), nameof(Time), nameof(Size), nameof(Type), nameof(Tag), nameof(HasTag), nameof(IsError), nameof(Method), nameof(MethodBg), nameof(MethodFg), nameof(Host), nameof(PathText), nameof(Url) })
            OnPropertyChanged(p);
    }
}

/// <summary>
/// One row of the JSON tree. Children are created only when the row is opened, and a long list shows
/// <see cref="PageSize"/> items at a time, so a response with thousands of rows stays fast.
/// </summary>
public partial class JsonNodeVm : ObservableObject
{
    public const int PageSize = 100;
    readonly JsonNodeInfo? _info;
    readonly int _depth;
    readonly int _openDepth;
    readonly JsonNodeVm? _owner;      // set on a "show more" row: the list it belongs to
    bool _built;
    int _shown;

    /// <param name="openDepth">Levels opened at the start: 3 for normal responses, 1 for large ones.</param>
    public JsonNodeVm(JsonNodeInfo n, int depth, int openDepth = 3)
    {
        _info = n; _depth = depth; _openDepth = openDepth;
        Name = n.Name; Value = n.Value;
        IsContainer = n.Kind is "object" or "array";
        IsString = n.Kind == "string"; IsNumber = n.Kind == "number"; IsBool = n.Kind == "bool";
        if (IsContainer && n.Children.Count > 0)
        {
            if (depth < openDepth) { _isExpanded = true; Build(); }
            else Children.Add(new JsonNodeVm());               // placeholder so the row can be opened
        }
    }

    JsonNodeVm() { Name = "…"; Value = ""; IsPlaceholder = true; }

    JsonNodeVm(JsonNodeVm owner, int shown, int total)
    {
        _owner = owner; IsMore = true; Value = "";
        var next = Math.Min(PageSize, total - shown);
        Name = $"Show next {next}  ({shown:N0} of {total:N0} shown)";
    }

    public string Name { get; }
    public string Value { get; }
    public bool IsContainer { get; }
    public bool IsString { get; }
    public bool IsNumber { get; }
    public bool IsBool { get; }
    public bool IsMore { get; }
    public bool IsPlaceholder { get; }
    public bool IsValue => !IsMore && !IsPlaceholder;
    public int TotalChildren => _info?.Children.Count ?? 0;
    public ObservableCollection<JsonNodeVm> Children { get; } = new();
    [ObservableProperty] bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) { if (value) Build(); }

    void Build()
    {
        if (_built || _info == null) return;
        _built = true;
        Children.Clear();
        AddPage();
    }

    void AddPage()
    {
        if (_info == null) return;
        if (Children.Count > 0 && Children[^1].IsMore) Children.RemoveAt(Children.Count - 1);
        var end = Math.Min(_shown + PageSize, _info.Children.Count);
        for (; _shown < end; _shown++) Children.Add(new JsonNodeVm(_info.Children[_shown], _depth + 1, _openDepth));
        if (_shown < _info.Children.Count) Children.Add(new JsonNodeVm(this, _shown, _info.Children.Count));
    }

    /// <summary>On a "show more" row: adds the next page to its list.</summary>
    [RelayCommand]
    void ShowMore() { if (_owner != null) { _owner.Build(); _owner.AddPage(); } }

    /// <summary>Opens or closes this row and the rows below it. Opening stops once the budget is used, so "Expand all" cannot freeze on a huge response.</summary>
    public void SetExpanded(bool value, ref int budget)
    {
        if (IsMore || IsPlaceholder || !IsContainer) return;
        if (value && budget <= 0) return;
        IsExpanded = value;
        if (!_built) return;
        if (value) budget -= Children.Count;
        foreach (var c in Children) c.SetExpanded(value, ref budget);
    }

    public void SetExpanded(bool value) { var budget = int.MaxValue; SetExpanded(value, ref budget); }
}

public partial class MockRuleVm : ObservableObject
{
    public MockRuleVm(MockRule r) { Rule = r; }
    public MockRule Rule { get; }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; OnPropertyChanged(); } }
    public string Name { get => Rule.Name; set { Rule.Name = value; OnPropertyChanged(); } }
    public string Method { get => Rule.Method; set { Rule.Method = value?.Trim().ToUpperInvariant() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string UrlContains { get => Rule.UrlContains; set { Rule.UrlContains = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string Status { get => Rule.Status.ToString(); set { if (int.TryParse(value, out var s) && s is >= 100 and <= 599) Rule.Status = s; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string ContentType { get => Rule.ContentType; set { Rule.ContentType = value ?? ""; OnPropertyChanged(); } }
    public string Body { get => Rule.Body; set { Rule.Body = value ?? ""; OnPropertyChanged(); } }
    public string ExtraHeaders { get => Rule.ExtraHeaders; set { Rule.ExtraHeaders = value ?? ""; OnPropertyChanged(); } }
    public string Summary => $"{(string.IsNullOrEmpty(Rule.Method) ? "Any" : Rule.Method)} · {Rule.UrlContains} → {Rule.Status}";
}

public partial class MapRuleVm : ObservableObject
{
    public MapRuleVm(MapRule r) { Rule = r; }
    public MapRule Rule { get; }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; OnPropertyChanged(); } }
    public string Name { get => Rule.Name; set { Rule.Name = value ?? ""; OnPropertyChanged(); } }
    public string Method { get => Rule.Method; set { Rule.Method = value?.Trim().ToUpperInvariant() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string UrlContains { get => Rule.UrlContains; set { Rule.UrlContains = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string LocalPath { get => Rule.LocalPath; set { Rule.LocalPath = value ?? ""; OnPropertyChanged(); Changed(); } }
    public string RemoteUrl { get => Rule.RemoteUrl; set { Rule.RemoteUrl = value ?? ""; OnPropertyChanged(); Changed(); } }
    public string Status { get => Rule.Status.ToString(); set { if (int.TryParse(value, out var s) && s is >= 100 and <= 599) Rule.Status = s; OnPropertyChanged(); } }
    public bool IsRemote => Rule.IsRemote;
    public bool IsLocal => !Rule.IsRemote;
    public string Kind => Rule.IsRemote ? "Map Remote" : "Map Local";
    public string Summary => $"{Kind} · {(string.IsNullOrEmpty(Rule.Method) ? "Any" : Rule.Method)} · {Rule.UrlContains}";
    /// <summary>Plain-language check of the target, shown under the field.</summary>
    public string Check
    {
        get
        {
            if (Rule.IsRemote)
            {
                if (string.IsNullOrWhiteSpace(Rule.RemoteUrl)) return "Enter the server to send matching requests to, for example https://staging.example.com";
                if (!Uri.TryCreate(Rule.RemoteUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "This must start with http:// or https://";
                return "Example: https://api.example.com/v2/orders?id=1 → " + MapRule.Remap("https://api.example.com/v2/orders?id=1", Rule.RemoteUrl);
            }
            if (string.IsNullOrWhiteSpace(Rule.LocalPath)) return "Choose the file to answer with.";
            return File.Exists(Rule.LocalPath.Trim()) ? "File found. Content type: " + MapRule.ContentTypeFor(Rule.LocalPath) : "This file does not exist. Matching requests will get an error until it does.";
        }
    }
    void Changed() { OnPropertyChanged(nameof(Check)); OnPropertyChanged(nameof(Summary)); }
}

public partial class BreakpointRuleVm : ObservableObject
{
    public BreakpointRuleVm(BreakpointRule r) { Rule = r; }
    public BreakpointRule Rule { get; }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; OnPropertyChanged(); } }
    public string Method { get => Rule.Method; set { Rule.Method = value?.Trim().ToUpperInvariant() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string UrlContains { get => Rule.UrlContains; set { Rule.UrlContains = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public bool OnRequest { get => Rule.OnRequest; set { Rule.OnRequest = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public bool OnResponse { get => Rule.OnResponse; set { Rule.OnResponse = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string Summary => $"{(string.IsNullOrEmpty(Rule.Method) ? "Any" : Rule.Method)} · {Rule.UrlContains} · " +
        (Rule.OnRequest && Rule.OnResponse ? "request and response" : Rule.OnResponse ? "response" : "request");
}

public sealed record DiffLineVm(string Prefix, string Text, IBrush Background);
