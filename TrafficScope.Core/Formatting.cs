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

    /// <summary>Text boxes get slow with very long text, so the screen shows this much and the rest is saved to a file.</summary>
    public const int PreviewChars = 200 * 1024;
    /// <summary>Above this size a body is saved to a file instead of being copied to the clipboard.</summary>
    public const int LargeBodyBytes = 1024 * 1024;

    public static string Preview(string text, long totalBytes)
    {
        if (text.Length <= PreviewChars) return text;
        var cut = text.LastIndexOf('\n', PreviewChars - 1, Math.Min(PreviewChars, 2000));
        return text[..(cut > 0 ? cut : PreviewChars)]
            + $"\n\n… Preview only: this body is {Size(totalBytes)}. Press the save icon above to get all of it in a file.";
    }

    /// <summary>A sensible file name for saving a body: the last part of the address plus an extension for its type.</summary>
    public static string BodyFileName(string url, string? contentType, bool request)
    {
        var name = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) name = System.IO.Path.GetFileName(u.AbsolutePath.TrimEnd('/'));
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (name.Length == 0) name = "body";
        if (name.Length > 60) name = name[..60];
        var ct = (contentType ?? "").ToLowerInvariant();
        var ext = ct.Contains("json") ? ".json" : ct.Contains("html") ? ".html" : ct.Contains("xml") ? ".xml" : ct.Contains("javascript") ? ".js"
            : ct.Contains("css") ? ".css" : ct.Contains("csv") ? ".csv" : ct.Contains("png") ? ".png" : ct.Contains("jpeg") ? ".jpg" : ct.Contains("gif") ? ".gif"
            : ct.Contains("pdf") ? ".pdf" : ct.Contains("zip") ? ".zip" : ct.StartsWith("text/") || ct.Contains("form-urlencoded") ? ".txt" : ".bin";
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0) stem = "body";
        return stem + (request ? "-request" : "-response") + ext;
    }

    /// <summary>Classic hex view: offset, 16 bytes in hex, then the printable characters.</summary>
    public static string HexDump(byte[]? data, int maxBytes = 64 * 1024)
    {
        if (data is not { Length: > 0 }) return "";
        var n = Math.Min(data.Length, maxBytes);
        var sb = new System.Text.StringBuilder(n * 4 + 64);
        for (var i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("x8")).Append("  ");
            for (var j = 0; j < 16; j++)
            {
                sb.Append(i + j < n ? data[i + j].ToString("x2") : "  ").Append(' ');
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (var j = 0; j < 16 && i + j < n; j++) { var c = (char)data[i + j]; sb.Append(c is >= ' ' and <= '~' ? c : '.'); }
            sb.Append('\n');
        }
        if (data.Length > n) sb.Append($"… {Size(data.Length - n)} more not shown (first {Size(n)} of {Size(data.Length)})\n");
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
