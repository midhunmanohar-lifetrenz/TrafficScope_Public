using System.Text;
using System.Text.Json;

namespace TrafficScope.Core;

/// <summary>Reads a HAR 1.2 file (saved by TrafficScope, a browser, Proxyman, Fiddler, Charles…) back into flows.</summary>
public static class HarImporter
{
    public static List<Flow> Parse(string json, Func<long> nextId)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("log", out var log) || log.ValueKind != JsonValueKind.Object
            || !log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new FormatException("This is not a HAR file (it has no log.entries list).");

        var flows = new List<Flow>();
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object) continue;
            var url = Str(req, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var started = DateTime.TryParse(Str(e, "startedDateTime"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : DateTime.Now;
            var flow = new Flow
            {
                Id = nextId(), StartedAt = started, Method = Str(req, "method").ToUpperInvariant(), Url = url, Host = uri.Host, Path = uri.PathAndQuery,
                HttpVersion = Str(req, "httpVersion") is { Length: > 0 } v ? v : "HTTP/1.1",
                AppName = Str(e, "_app") is { Length: > 0 } app ? app : "Imported", Source = "Imported",
                RequestHeaders = Headers(req),
                Duration = e.TryGetProperty("time", out var time) && time.TryGetDouble(out var ms) && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null,
            };
            if (req.TryGetProperty("postData", out var post) && post.ValueKind == JsonValueKind.Object && Str(post, "text") is { Length: > 0 } text)
                flow.RequestBody = Decode(text, Str(post, "encoding") == "base64" || Str(post, "_encoding") == "base64");

            if (e.TryGetProperty("response", out var res) && res.ValueKind == JsonValueKind.Object)
            {
                var status = res.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 0;
                if (status > 0) { flow.StatusCode = status; flow.StatusDescription = Str(res, "statusText"); }
                flow.ResponseHeaders = Headers(res);
                if (res.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && Str(content, "text") is { Length: > 0 } body)
                    flow.ResponseBody = Decode(body, Str(content, "encoding") == "base64");
                if (Str(res, "_error") is { Length: > 0 } err) flow.Error = err;
            }
            if (flow.StatusCode == null && flow.Error == null) flow.Error = "No response was recorded for this request.";
            flow.Mocked = Bool(e, "_mocked"); flow.Edited = Bool(e, "_edited");
            flows.Add(flow);
        }
        return flows;
    }

    static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static List<KeyValuePair<string, string>> Headers(JsonElement o)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (o.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray())
                if (Str(h, "name") is { Length: > 0 } n && !n.StartsWith(':')) list.Add(new(n, Str(h, "value")));   // skip HTTP/2 pseudo-headers
        return list;
    }

    static byte[] Decode(string text, bool base64)
    {
        if (base64) try { return Convert.FromBase64String(text); } catch (FormatException) { }
        return Encoding.UTF8.GetBytes(text);
    }
}
