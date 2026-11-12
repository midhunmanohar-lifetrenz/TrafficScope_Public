using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public string Tag => Flow.IsTunnel ? "Tunnel" : Flow.Mocked ? "Mock" : Flow.Edited ? "Edited" : Flow.Source.Length > 0 ? Flow.Source : "";
    public bool HasTag => Tag.Length > 0;

    public void Refresh()
    {
        foreach (var p in new[] { nameof(Status), nameof(StatusBrush), nameof(StatusOk), nameof(StatusBad), nameof(StatusInfo), nameof(IsGet), nameof(IsPost), nameof(IsPut), nameof(IsDelete), nameof(Time), nameof(Size), nameof(Type), nameof(Tag), nameof(HasTag), nameof(IsError), nameof(Method), nameof(MethodBg), nameof(MethodFg), nameof(Host), nameof(PathText), nameof(Url) })
            OnPropertyChanged(p);
    }
}

public partial class JsonNodeVm : ObservableObject
{
    public JsonNodeVm(JsonNodeInfo n, int depth)
    {
        Name = n.Name; Value = n.Kind is "object" or "array" ? n.Value : n.Value;
        IsContainer = n.Kind is "object" or "array";
        ValueBrush = Brush.Parse(n.Kind switch { "string" => "#2E9E5B", "number" => "#C98A12", "bool" => "#1A73E8", "null" => "#888780", _ => "#888780" });
        IsString = n.Kind == "string"; IsNumber = n.Kind == "number"; IsBool = n.Kind == "bool";
        _isExpanded = depth < 3;
        foreach (var c in n.Children) Children.Add(new JsonNodeVm(c, depth + 1));
    }
    public string Name { get; }
    public string Value { get; }
    public bool IsContainer { get; }
    public IBrush ValueBrush { get; }
    public bool IsString { get; }
    public bool IsNumber { get; }
    public bool IsBool { get; }
    public ObservableCollection<JsonNodeVm> Children { get; } = new();
    [ObservableProperty] bool _isExpanded;

    public void SetExpanded(bool value) { IsExpanded = value; foreach (var c in Children) c.SetExpanded(value); }
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
