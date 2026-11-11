using System.Text.Json;

namespace TrafficScope.Core;

public sealed class MockRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "New mock";
    public string Method { get; set; } = "";          // empty = any
    public string UrlContains { get; set; } = "";
    public int Status { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";
    public string Body { get; set; } = "{}";
    public string ExtraHeaders { get; set; } = "";     // "Name: value" per line

    public bool Matches(string method, string url) =>
        Enabled && !string.IsNullOrWhiteSpace(UrlContains)
        && url.Contains(UrlContains, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(Method) || Method.Equals(method, StringComparison.OrdinalIgnoreCase));
}

public sealed class BreakpointRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string UrlContains { get; set; } = "";
    public string Method { get; set; } = "";
    public bool OnRequest { get; set; } = true;
    public bool OnResponse { get; set; }

    public bool Matches(string method, string url) =>
        Enabled && !string.IsNullOrWhiteSpace(UrlContains)
        && url.Contains(UrlContains, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(Method) || Method.Equals(method, StringComparison.OrdinalIgnoreCase));
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";       // System, Light, Dark
    public bool MaskSecrets { get; set; }
    public bool ShowTunnels { get; set; }
    public int DelayMs { get; set; }
    public List<string> DecryptOnlyHosts { get; set; } = new();
    public string PostmanWorkspaceId { get; set; } = "";
    public string PostmanWorkspaceName { get; set; } = "";
    public string PostmanCollection { get; set; } = "TrafficScope";
    public string PostmanUser { get; set; } = "";
    public List<MockRule> Mocks { get; set; } = new();
    public List<BreakpointRule> Breakpoints { get; set; } = new();
}

public sealed class SettingsStore
{
    readonly string _file;
    public SettingsStore(string? dir = null) => _file = Path.Combine(dir ?? FavoritesStore.DataDir, "settings.json");

    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file)) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppSettings s)
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

public static class HostPattern
{
    /// <summary>"*.example.com" and "example.com" (also matches subdomains) or "*" wildcards anywhere.</summary>
    public static bool Matches(string host, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        host = host.ToLowerInvariant(); pattern = pattern.ToLowerInvariant();
        if (pattern.Contains('*'))
        {
            var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(host, rx);
        }
        return host == pattern || host.EndsWith("." + pattern);
    }

    public static bool MatchesAny(string host, IEnumerable<string> patterns) => patterns.Any(p => Matches(host, p));
}
