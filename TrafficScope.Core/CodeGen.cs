using System.Text;

namespace TrafficScope.Core;

/// <summary>Turns a captured request into ready-to-run code.</summary>
public static class CodeGen
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Connection", "Proxy-Connection", "Accept-Encoding", "Transfer-Encoding" };

    public static readonly string[] Languages = { "Python", "JavaScript", "C#" };

    public static string Build(string language, Flow f, bool mask) => language switch
    {
        "Python" => Python(f, mask),
        "JavaScript" => JavaScript(f, mask),
        "C#" => CSharp(f, mask),
        _ => CurlBuilder.Build(f, mask),
    };

    static IEnumerable<KeyValuePair<string, string>> Headers(Flow f, bool mask) =>
        Redactor.Apply(f.RequestHeaders, mask).Where(h => !Skip.Contains(h.Key));

    static string? TextBody(Flow f) =>
        f.RequestBody is { Length: > 0 } b && Formatting.IsText(f.RequestContentType, b) ? Encoding.UTF8.GetString(b) : null;

    static bool HasBinaryBody(Flow f) => f.RequestBody is { Length: > 0 } b && !Formatting.IsText(f.RequestContentType, b);

    /// <summary>A double-quoted string literal valid in Python, JavaScript and C#.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    static string Python(Flow f, bool mask)
    {
        var sb = new StringBuilder("import requests\n\n");
        sb.Append("url = ").Append(Quote(f.Url)).Append('\n');
        sb.Append("headers = {\n");
        foreach (var h in Headers(f, mask)) sb.Append("    ").Append(Quote(h.Key)).Append(": ").Append(Quote(h.Value)).Append(",\n");
        sb.Append("}\n");
        var body = TextBody(f);
        if (body != null) sb.Append("data = ").Append(Quote(body)).Append('\n');
        if (HasBinaryBody(f)) sb.Append("data = open(\"request-body.bin\", \"rb\").read()  # binary body: ").Append(f.RequestBody!.Length).Append(" bytes\n");
        sb.Append("\nresponse = requests.request(").Append(Quote(f.Method)).Append(", url, headers=headers");
        if (f.RequestBody is { Length: > 0 }) sb.Append(", data=data").Append(body != null ? ".encode(\"utf-8\")" : "");
        sb.Append(")\nprint(response.status_code)\nprint(response.text)\n");
        return sb.ToString();
    }

    static string JavaScript(Flow f, bool mask)
    {
        var sb = new StringBuilder("const response = await fetch(").Append(Quote(f.Url)).Append(", {\n");
        sb.Append("  method: ").Append(Quote(f.Method)).Append(",\n  headers: {\n");
        foreach (var h in Headers(f, mask)) sb.Append("    ").Append(Quote(h.Key)).Append(": ").Append(Quote(h.Value)).Append(",\n");
        sb.Append("  },\n");
        if (TextBody(f) is { } body) sb.Append("  body: ").Append(Quote(body)).Append(",\n");
        else if (HasBinaryBody(f)) sb.Append("  // binary body: ").Append(f.RequestBody!.Length).Append(" bytes (set body to a Blob or ArrayBuffer)\n");
        sb.Append("});\nconsole.log(response.status);\nconsole.log(await response.text());\n");
        return sb.ToString();
    }

    static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Content-Type", "Content-Encoding", "Content-Language", "Content-Disposition" };

    static string CSharp(Flow f, bool mask)
    {
        var sb = new StringBuilder("using var client = new HttpClient();\n");
        sb.Append("using var request = new HttpRequestMessage(new HttpMethod(").Append(Quote(f.Method)).Append("), ").Append(Quote(f.Url)).Append(");\n");
        var body = TextBody(f);
        if (body != null) sb.Append("request.Content = new StringContent(").Append(Quote(body)).Append(");\n");
        else if (HasBinaryBody(f)) sb.Append("request.Content = new ByteArrayContent(File.ReadAllBytes(\"request-body.bin\"));  // binary body: ").Append(f.RequestBody!.Length).Append(" bytes\n");
        var hasContent = f.RequestBody is { Length: > 0 };
        foreach (var h in Headers(f, mask))
        {
            if (ContentHeaders.Contains(h.Key))
            {
                if (!hasContent) continue;
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) sb.Append("request.Content.Headers.Remove(\"Content-Type\");\n");
                sb.Append("request.Content.Headers.TryAddWithoutValidation(").Append(Quote(h.Key)).Append(", ").Append(Quote(h.Value)).Append(");\n");
            }
            else sb.Append("request.Headers.TryAddWithoutValidation(").Append(Quote(h.Key)).Append(", ").Append(Quote(h.Value)).Append(");\n");
        }
        sb.Append("using var response = await client.SendAsync(request);\n");
        sb.Append("Console.WriteLine((int)response.StatusCode);\nConsole.WriteLine(await response.Content.ReadAsStringAsync());\n");
        return sb.ToString();
    }
}
