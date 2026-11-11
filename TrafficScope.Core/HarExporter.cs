using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrafficScope.Core;

public static class HarExporter
{
    public static string Build(IEnumerable<Flow> flows, bool mask)
    {
        var entries = new JsonArray();
        foreach (var f in flows.Where(f => !f.IsTunnel))
        {
            JsonArray H(IEnumerable<KeyValuePair<string, string>> h) =>
                new(Redactor.Apply(h, mask).Select(x => (JsonNode)new JsonObject { ["name"] = x.Key, ["value"] = x.Value }).ToArray());
            JsonArray Q() => new(FormParser.Query(f.Url).Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["value"] = x.Value }).ToArray());

            var req = new JsonObject
            {
                ["method"] = f.Method, ["url"] = f.Url, ["httpVersion"] = f.HttpVersion,
                ["headers"] = H(f.RequestHeaders), ["queryString"] = Q(), ["cookies"] = new JsonArray(),
                ["headersSize"] = -1, ["bodySize"] = f.RequestBody?.Length ?? 0,
            };
            if (f.RequestBody is { Length: > 0 } rb)
                req["postData"] = new JsonObject { ["mimeType"] = f.RequestContentType ?? "", ["text"] = BodyString(f.RequestContentType, rb, out _) };

            var content = new JsonObject { ["size"] = f.ResponseBody?.Length ?? 0, ["mimeType"] = f.ResponseContentType ?? "" };
            if (f.ResponseBody is { Length: > 0 } sb)
            {
                content["text"] = BodyString(f.ResponseContentType, sb, out var b64);
                if (b64) content["encoding"] = "base64";
            }
            var ms = f.Duration?.TotalMilliseconds ?? 0;
            entries.Add(new JsonObject
            {
                ["startedDateTime"] = f.StartedAt.ToUniversalTime().ToString("o"),
                ["time"] = ms,
                ["request"] = req,
                ["response"] = new JsonObject
                {
                    ["status"] = f.StatusCode ?? 0, ["statusText"] = f.StatusDescription ?? "", ["httpVersion"] = f.HttpVersion,
                    ["headers"] = H(f.ResponseHeaders), ["cookies"] = new JsonArray(), ["content"] = content,
                    ["redirectURL"] = Flow.Header(f.ResponseHeaders, "Location") ?? "", ["headersSize"] = -1, ["bodySize"] = f.ResponseBody?.Length ?? 0,
                },
                ["cache"] = new JsonObject(),
                ["timings"] = new JsonObject { ["send"] = 0, ["wait"] = ms, ["receive"] = 0 },
                ["_app"] = f.AppName,
            });
        }
        var har = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject { ["name"] = "TrafficScope", ["version"] = AppInfo.Version },
                ["entries"] = entries,
            }
        };
        return har.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static string BodyString(string? ct, byte[] body, out bool base64)
    {
        base64 = !Formatting.IsText(ct, body);
        return base64 ? Convert.ToBase64String(body) : Encoding.UTF8.GetString(body);
    }
}
