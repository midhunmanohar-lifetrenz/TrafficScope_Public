using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrafficScope.Core;

public sealed record PostmanWorkspace(string Id, string Name, string Type);
public sealed record PostmanResult(bool Ok, string Message, string? CollectionUid = null, string? RequestName = null);

/// <summary>Saves captured requests into a Postman collection through the official Postman API.</summary>
public sealed class PostmanClient
{
    public const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";
    static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Connection", "Proxy-Connection", "Accept-Encoding", "Transfer-Encoding" };

    readonly HttpClient _http;
    readonly string _apiKey;

    public PostmanClient(string apiKey, string baseUrl = "https://api.getpostman.com", HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey.Trim();
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TrafficScope/" + AppInfo.Version);
    }

    async Task<JsonNode> Send(HttpMethod method, string path, JsonNode? body = null)
    {
        using var msg = new HttpRequestMessage(method, path);
        if (body != null) msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(msg);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            string detail = text;
            try { detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; } catch { }
            throw new PostmanException((int)res.StatusCode, (int)res.StatusCode == 401 ? "Postman rejected the API key." : $"Postman API error {(int)res.StatusCode}: {detail}");
        }
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    /// <summary>Checks the key. Returns the Postman user name.</summary>
    public async Task<string> GetUserAsync()
    {
        var me = await Send(HttpMethod.Get, "me");
        var u = me["user"];
        return u?["fullName"]?.GetValue<string>() ?? u?["username"]?.GetValue<string>() ?? u?["email"]?.GetValue<string>() ?? "Postman user";
    }

    public async Task<List<PostmanWorkspace>> GetWorkspacesAsync()
    {
        var r = await Send(HttpMethod.Get, "workspaces");
        return (r["workspaces"] as JsonArray ?? new JsonArray())
            .Select(w => new PostmanWorkspace(w?["id"]?.GetValue<string>() ?? "", w?["name"]?.GetValue<string>() ?? "", w?["type"]?.GetValue<string>() ?? ""))
            .Where(w => w.Id.Length > 0).ToList();
    }

    public async Task<string> EnsureCollectionAsync(string workspaceId, string name)
    {
        var list = await Send(HttpMethod.Get, "collections?workspace=" + Uri.EscapeDataString(workspaceId));
        foreach (var c in list["collections"] as JsonArray ?? new JsonArray())
            if (string.Equals(c?["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                return c!["uid"]?.GetValue<string>() ?? c["id"]!.GetValue<string>();
        var created = await Send(HttpMethod.Post, "collections?workspace=" + Uri.EscapeDataString(workspaceId), new JsonObject
        {
            ["collection"] = new JsonObject
            {
                ["info"] = new JsonObject { ["name"] = name, ["description"] = "Requests sent from TrafficScope.", ["schema"] = SchemaV21 },
                ["item"] = new JsonArray(),
            }
        });
        return created["collection"]?["uid"]?.GetValue<string>() ?? created["collection"]!["id"]!.GetValue<string>();
    }

    /// <summary>Appends the flow as a new request at the top of the collection (read, add, write back).</summary>
    public async Task<PostmanResult> SaveRequestAsync(string workspaceId, string collectionName, Flow flow, bool mask)
    {
        try
        {
            var uid = await EnsureCollectionAsync(workspaceId, collectionName);
            var current = await Send(HttpMethod.Get, "collections/" + uid);
            var col = current["collection"]?.AsObject() ?? throw new PostmanException(0, "Postman returned an empty collection.");
            var items = col["item"] as JsonArray;
            if (items == null) { items = new JsonArray(); col["item"] = items; }
            var item = BuildItem(flow, mask);
            var name = item["name"]!.GetValue<string>();
            items.Insert(0, item);
            await Send(HttpMethod.Put, "collections/" + uid, new JsonObject { ["collection"] = JsonNode.Parse(col.ToJsonString()) });
            return new PostmanResult(true, $"Saved to Postman: {collectionName} › {name}", uid, name);
        }
        catch (PostmanException ex) { return new PostmanResult(false, ex.Message); }
        catch (HttpRequestException ex) { return new PostmanResult(false, "Could not reach Postman: " + ex.Message); }
        catch (TaskCanceledException) { return new PostmanResult(false, "Postman did not answer in time."); }
    }

    public static string RequestName(Flow f)
    {
        var path = f.Path.Split('?')[0];
        if (path.Length > 60) path = "…" + path[^59..];
        return $"{f.Method} {f.Host}{path}  ({f.StartedAt:dd MMM HH:mm:ss})";
    }

    public static JsonObject BuildItem(Flow f, bool mask)
    {
        var headers = new JsonArray();
        foreach (var h in Redactor.Apply(f.RequestHeaders, mask))
            if (!SkipHeaders.Contains(h.Key)) headers.Add(new JsonObject { ["key"] = h.Key, ["value"] = h.Value, ["type"] = "text" });
        var request = new JsonObject
        {
            ["method"] = f.Method,
            ["header"] = headers,
            ["url"] = new JsonObject { ["raw"] = f.Url },
        };
        if (f.RequestBody is { Length: > 0 } b)
        {
            var ct = f.RequestContentType ?? "";
            if (FormParser.IsForm(ct))
                request["body"] = new JsonObject
                {
                    ["mode"] = "urlencoded",
                    ["urlencoded"] = new JsonArray(FormParser.Parse(Encoding.UTF8.GetString(b))
                        .Select(p => (JsonNode)new JsonObject { ["key"] = p.Name, ["value"] = p.Value, ["type"] = "text" }).ToArray()),
                };
            else if (Formatting.IsText(ct, b))
            {
                var lang = ct.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json" : ct.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml" : "text";
                request["body"] = new JsonObject
                {
                    ["mode"] = "raw", ["raw"] = Encoding.UTF8.GetString(b),
                    ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = lang } },
                };
            }
        }
        return new JsonObject
        {
            ["name"] = RequestName(f),
            ["request"] = request,
            ["response"] = new JsonArray(),
        };
    }
}

public sealed class PostmanException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
