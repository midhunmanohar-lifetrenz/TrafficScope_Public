using System.Net;
using System.Text;
using System.Text.Json;

namespace TrafficScope.Core;

public sealed record NameValue(string Name, string Value, string Note = "");

public static class FormParser
{
    public static bool IsForm(string? contentType) =>
        contentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;

    public static List<NameValue> Parse(string? text)
    {
        var list = new List<NameValue>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            string D(string s) { try { return WebUtility.UrlDecode(s); } catch { return s; } }
            list.Add(i < 0 ? new NameValue(D(part), "") : new NameValue(D(part[..i]), D(part[(i + 1)..])));
        }
        return list;
    }

    public static List<NameValue> Query(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return new();
        var end = url.IndexOf('#', q);
        return Parse(end < 0 ? url[(q + 1)..] : url[(q + 1)..end]);
    }
}

public static class CookieParser
{
    public static List<NameValue> Request(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Where(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(c => { var i = c.IndexOf('='); return i < 0 ? new NameValue(c.Trim(), "") : new NameValue(c[..i].Trim(), c[(i + 1)..].Trim()); })
            .ToList();

    public static List<NameValue> Response(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h =>
            {
                var parts = h.Value.Split(';');
                var first = parts[0]; var i = first.IndexOf('=');
                var attrs = string.Join("; ", parts.Skip(1).Select(p => p.Trim()));
                return i < 0 ? new NameValue(first.Trim(), "", attrs) : new NameValue(first[..i].Trim(), first[(i + 1)..].Trim(), attrs);
            }).ToList();
}

public sealed record JwtInfo(string HeaderJson, string PayloadJson, DateTime? ExpiresUtc, DateTime? IssuedUtc);

public static class JwtDecoder
{
    public static JwtInfo? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var header = Pretty(Decode(parts[0]));
            var payloadRaw = Decode(parts[1]);
            using var doc = JsonDocument.Parse(payloadRaw);
            DateTime? T(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime : null;
            return new JwtInfo(header, Pretty(payloadRaw), T("exp"), T("iat"));
        }
        catch { return null; }
    }

    public static JwtInfo? FindInHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var h in headers)
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Value.StartsWith("eyJ", StringComparison.Ordinal))
                if (TryDecode(h.Value) is { } j) return j;
        return null;
    }

    static string Decode(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    static string Pretty(string json)
    {
        using var d = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class JsonNodeInfo
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Kind { get; init; } = "";       // object, array, string, number, bool, null
    public List<JsonNodeInfo> Children { get; } = new();
}

public static class JsonTree
{
    public static bool LooksLikeJson(string? contentType, byte[]? body)
    {
        if (body == null || body.Length == 0) return false;
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) return true;
        var first = body.FirstOrDefault(b => b != ' ' && b != '\n' && b != '\r' && b != '\t');
        return first == '{' || first == '[';
    }

    public static JsonNodeInfo? Build(byte[]? body)
    {
        if (body == null || body.Length == 0 || body.Length > 4 * 1024 * 1024) return null;
        try { using var d = JsonDocument.Parse(body); return Node("JSON", d.RootElement); }
        catch { return null; }
    }

    static JsonNodeInfo Node(string name, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var n = new JsonNodeInfo { Name = name, Kind = "object", Value = $"{{ {e.EnumerateObject().Count()} }}" };
                foreach (var p in e.EnumerateObject()) n.Children.Add(Node(p.Name, p.Value));
                return n;
            }
            case JsonValueKind.Array:
            {
                var n = new JsonNodeInfo { Name = name, Kind = "array", Value = $"[ {e.GetArrayLength()} ]" };
                var i = 0;
                foreach (var item in e.EnumerateArray()) n.Children.Add(Node($"[{i++}]", item));
                return n;
            }
            case JsonValueKind.String: return new JsonNodeInfo { Name = name, Kind = "string", Value = "\"" + e.GetString() + "\"" };
            case JsonValueKind.Number: return new JsonNodeInfo { Name = name, Kind = "number", Value = e.GetRawText() };
            case JsonValueKind.True or JsonValueKind.False: return new JsonNodeInfo { Name = name, Kind = "bool", Value = e.GetRawText() };
            default: return new JsonNodeInfo { Name = name, Kind = "null", Value = "null" };
        }
    }
}

public static class Redactor
{
    static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
        { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token", "X-Access-Token" };

    public const string Mask = "••••••";

    public static bool IsSecret(string header) => Secret.Contains(header);

    public static string MaskValue(string header, string value)
    {
        if (!IsSecret(header)) return value;
        var sp = value.IndexOf(' ');
        return sp > 0 && sp < 12 && header.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ? value[..sp] + " " + Mask : Mask;
    }

    public static List<KeyValuePair<string, string>> Apply(IEnumerable<KeyValuePair<string, string>> headers, bool mask) =>
        headers.Select(h => mask ? new KeyValuePair<string, string>(h.Key, MaskValue(h.Key, h.Value)) : h).ToList();
}

public enum FlowFilterKind { All, Errors, Json, Images, Slow }

public static class FlowFilter
{
    public static bool Matches(Flow f, FlowFilterKind kind) => kind switch
    {
        FlowFilterKind.Errors => (f.StatusCode >= 400) || f.Error != null,
        FlowFilterKind.Json => (f.ResponseContentType ?? f.RequestContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase),
        FlowFilterKind.Images => (f.ResponseContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        FlowFilterKind.Slow => f.Duration?.TotalMilliseconds > 1000,
        _ => true
    };

    public static bool MatchesText(Flow f, string q)
    {
        q = q.Trim();
        if (q.Length == 0) return true;
        return f.Url.Contains(q, StringComparison.OrdinalIgnoreCase) || f.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (f.StatusCode?.ToString() ?? "").Equals(q) || f.Method.Equals(q, StringComparison.OrdinalIgnoreCase)
            || Formatting.BodyText(f.RequestContentType, f.RequestBody, false).Contains(q, StringComparison.OrdinalIgnoreCase)
            || Formatting.BodyText(f.ResponseContentType, f.ResponseBody, false).Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}

public enum DiffKind { Same, Removed, Added }
public sealed record DiffLine(DiffKind Kind, string Text);

public static class LineDiff
{
    public static List<DiffLine> Compare(string a, string b)
    {
        var x = a.Replace("\r", "").Split('\n'); var y = b.Replace("\r", "").Split('\n');
        if (x.Length > 3000) x = x[..3000];
        if (y.Length > 3000) y = y[..3000];
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
            for (var j = y.Length - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var res = new List<DiffLine>();
        int p = 0, q = 0;
        while (p < x.Length && q < y.Length)
        {
            if (x[p] == y[q]) { res.Add(new(DiffKind.Same, x[p])); p++; q++; }
            else if (lcs[p + 1, q] >= lcs[p, q + 1]) res.Add(new(DiffKind.Removed, x[p++]));
            else res.Add(new(DiffKind.Added, y[q++]));
        }
        while (p < x.Length) res.Add(new(DiffKind.Removed, x[p++]));
        while (q < y.Length) res.Add(new(DiffKind.Added, y[q++]));
        return res;
    }
}
