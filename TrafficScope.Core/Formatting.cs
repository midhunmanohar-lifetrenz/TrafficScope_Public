using System.Text;
using System.Text.Json;

namespace TrafficScope.Core;

public static class Formatting
{
    public const int MaxDisplayBytes = 512 * 1024;

    public static bool IsText(string? contentType, byte[]? body)
    {
        if (body == null || body.Length == 0) return true;
        if (!string.IsNullOrEmpty(contentType))
        {
            var c = contentType.ToLowerInvariant();
            if (c.StartsWith("text/") || c.Contains("json") || c.Contains("xml") || c.Contains("javascript")
                || c.Contains("x-www-form-urlencoded") || c.Contains("graphql") || c.Contains("html")) return true;
            if (c.StartsWith("image/") || c.StartsWith("video/") || c.StartsWith("audio/") || c.Contains("octet-stream")
                || c.Contains("zip") || c.Contains("protobuf") || c.Contains("font") || c.Contains("pdf")) return false;
        }
        var n = Math.Min(body.Length, 512);
        for (var i = 0; i < n; i++) if (body[i] == 0) return false;
        return true;
    }

    public static string BodyText(string? contentType, byte[]? body, bool pretty = true)
    {
        if (body == null || body.Length == 0) return "";
        if (!IsText(contentType, body)) return $"(binary, {body.Length:N0} bytes)";
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, MaxDisplayBytes));
        if (pretty && contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && body.Length <= MaxDisplayBytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }
        }
        return body.Length > MaxDisplayBytes ? text + $"\n… truncated ({body.Length:N0} bytes total)" : text;
    }

    /// <summary>Full text for the clipboard: decoded text, or Base64 for binary bodies.</summary>
    public static string BodyForCopy(string? contentType, byte[]? body) =>
        body == null || body.Length == 0 ? "" :
        IsText(contentType, body) ? BodyText(contentType, body, pretty: true).Replace($"\n… truncated ({body.Length:N0} bytes total)", "") + (body.Length > MaxDisplayBytes ? Encoding.UTF8.GetString(body, MaxDisplayBytes, body.Length - MaxDisplayBytes) : "")
        : Convert.ToBase64String(body);

    public static string RawRequest(Flow f, bool forCopy = false, bool mask = false)
    {
        var sb = new StringBuilder();
        var pathAndQuery = string.IsNullOrEmpty(f.Path) ? "/" : f.Path;
        sb.Append(f.Method).Append(' ').Append(pathAndQuery).Append(' ').AppendLine(f.HttpVersion);
        foreach (var h in Redactor.Apply(f.RequestHeaders, mask)) sb.Append(h.Key).Append(": ").AppendLine(h.Value);
        sb.AppendLine();
        sb.Append(forCopy ? BodyForCopy(f.RequestContentType, f.RequestBody) : BodyText(f.RequestContentType, f.RequestBody));
        return sb.ToString();
    }

    public static string RawResponse(Flow f, bool forCopy = false, bool mask = false)
    {
        if (f.IsTunnel) return "Tunnel to " + f.Host + " (not decrypted: host is outside your decrypt-only list)";
        if (f.StatusCode == null) return f.Error ?? "Waiting for response…";
        var sb = new StringBuilder();
        sb.Append(f.HttpVersion).Append(' ').Append(f.StatusCode).Append(' ').AppendLine(f.StatusDescription);
        foreach (var h in Redactor.Apply(f.ResponseHeaders, mask)) sb.Append(h.Key).Append(": ").AppendLine(h.Value);
        sb.AppendLine();
        sb.Append(forCopy ? BodyForCopy(f.ResponseContentType, f.ResponseBody) : BodyText(f.ResponseContentType, f.ResponseBody));
        return sb.ToString();
    }

    public static string Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}
