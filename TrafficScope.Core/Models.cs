namespace TrafficScope.Core;

public sealed record ProcessInfo(int Pid, string Path, string Name);

public sealed record AppEntry(string Name, string Key)
{
    public bool IsRunning { get; init; }
}

public sealed class Flow
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public ProcessInfo? Process { get; set; }
    public string AppName { get; set; } = "Unknown";
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public int? StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public TimeSpan? Duration { get; set; }
    public List<KeyValuePair<string, string>> RequestHeaders { get; set; } = new();
    public List<KeyValuePair<string, string>> ResponseHeaders { get; set; } = new();
    public byte[]? RequestBody { get; set; }
    public byte[]? ResponseBody { get; set; }
    public string? Error { get; set; }
    public bool IsTunnel { get; set; }
    public bool Mocked { get; set; }
    public bool Edited { get; set; }
    public string Source { get; set; } = "";
    public event Action<Flow>? Changed;
    public void RaiseChanged() => Changed?.Invoke(this);

    public string? RequestContentType => Header(RequestHeaders, "Content-Type");
    public string? ResponseContentType => Header(ResponseHeaders, "Content-Type");

    public static string? Header(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
