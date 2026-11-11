using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TrafficScope.Core;
using Xunit;

namespace TrafficScope.Tests;

/// <summary>Minimal stand-in for api.getpostman.com: /me, /workspaces, /collections (list, create, get, put).</summary>
public sealed class FakePostman : IDisposable
{
    readonly HttpListener _l = new();
    public int Port { get; }
    public string Key = "PMAK-test";
    public Dictionary<string, JsonObject> Collections = new();   // uid -> collection
    public Dictionary<string, string> CollectionWorkspace = new();
    public List<string> Calls = new();
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public FakePostman()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            string body; using (var r = new StreamReader(c.Request.InputStream)) body = await r.ReadToEndAsync();
            var path = c.Request.Url!.AbsolutePath.Trim('/');
            var ws = c.Request.QueryString["workspace"];
            Calls.Add($"{c.Request.HttpMethod} {path}");
            JsonNode res; int code = 200;
            if (c.Request.Headers["X-Api-Key"] != Key) { code = 401; res = new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid API Key" } }; }
            else if (path == "me") res = new JsonObject { ["user"] = new JsonObject { ["id"] = 1, ["username"] = "demo-user", ["fullName"] = "Demo User" } };
            else if (path == "workspaces") res = new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "ws1", ["name"] = "My Workspace", ["type"] = "personal" }, new JsonObject { ["id"] = "ws2", ["name"] = "QA Team", ["type"] = "team" }) };
            else if (path == "collections" && c.Request.HttpMethod == "GET")
                res = new JsonObject { ["collections"] = new JsonArray(Collections.Where(kv => CollectionWorkspace[kv.Key] == ws)
                    .Select(kv => (JsonNode)new JsonObject { ["uid"] = kv.Key, ["id"] = kv.Key, ["name"] = kv.Value["info"]!["name"]!.GetValue<string>() }).ToArray()) };
            else if (path == "collections" && c.Request.HttpMethod == "POST")
            {
                var col = JsonNode.Parse(body)!["collection"]!.AsObject();
                var uid = "uid-" + (Collections.Count + 1);
                Collections[uid] = JsonNode.Parse(col.ToJsonString())!.AsObject(); CollectionWorkspace[uid] = ws ?? "";
                res = new JsonObject { ["collection"] = new JsonObject { ["uid"] = uid, ["id"] = uid, ["name"] = col["info"]!["name"]!.GetValue<string>() } };
            }
            else if (path.StartsWith("collections/") && c.Request.HttpMethod == "GET" && Collections.TryGetValue(path[12..], out var got))
                res = new JsonObject { ["collection"] = JsonNode.Parse(got.ToJsonString()) };
            else if (path.StartsWith("collections/") && c.Request.HttpMethod == "PUT" && Collections.ContainsKey(path[12..]))
            {
                Collections[path[12..]] = JsonNode.Parse(body)!["collection"]!.AsObject();
                res = new JsonObject { ["collection"] = new JsonObject { ["uid"] = path[12..] } };
            }
            else { code = 404; res = new JsonObject { ["error"] = new JsonObject { ["message"] = "not found" } }; }
            var bytes = Encoding.UTF8.GetBytes(res.ToJsonString());
            c.Response.StatusCode = code; c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        }
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}

public class PostmanTests
{
    static Flow Login(bool form = false)
    {
        var f = new Flow { Method = "POST", Url = "https://api.example.com/v2/login?site=56", Host = "api.example.com", Path = "/v2/login?site=56", StartedAt = new DateTime(2026, 11, 10, 18, 30, 5) };
        f.RequestHeaders.Add(new("Host", "api.example.com"));
        f.RequestHeaders.Add(new("Content-Length", "15"));
        f.RequestHeaders.Add(new("Authorization", "Bearer real-token"));
        f.RequestHeaders.Add(new("Content-Type", form ? "application/x-www-form-urlencoded" : "application/json"));
        f.RequestBody = Encoding.UTF8.GetBytes(form ? "start%5Fdate=04%2F01%2F2026&site=12" : "{\"user\":\"demo\"}");
        return f;
    }

    [Fact]
    public void ItemUsesCollectionV21ShapeAndSkipsTransportHeaders()
    {
        var item = PostmanClient.BuildItem(Login(), mask: false);
        Assert.Equal("POST api.example.com/v2/login  (10 Nov 18:30:05)", item["name"]!.GetValue<string>());
        var req = item["request"]!;
        Assert.Equal("POST", req["method"]!.GetValue<string>());
        Assert.Equal("https://api.example.com/v2/login?site=56", req["url"]!["raw"]!.GetValue<string>());
        var keys = req["header"]!.AsArray().Select(h => h!["key"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("Host", keys); Assert.DoesNotContain("Content-Length", keys);
        Assert.Contains("Authorization", keys);
        Assert.Equal("raw", req["body"]!["mode"]!.GetValue<string>());
        Assert.Equal("json", req["body"]!["options"]!["raw"]!["language"]!.GetValue<string>());
        Assert.Equal("{\"user\":\"demo\"}", req["body"]!["raw"]!.GetValue<string>());
    }

    [Fact]
    public void FormBodyBecomesUrlencodedFieldsAndMaskingHidesToken()
    {
        var item = PostmanClient.BuildItem(Login(form: true), mask: true);
        var body = item["request"]!["body"]!;
        Assert.Equal("urlencoded", body["mode"]!.GetValue<string>());
        Assert.Equal("start_date", body["urlencoded"]![0]!["key"]!.GetValue<string>());
        Assert.Equal("04/01/2026", body["urlencoded"]![0]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("real-token", item.ToJsonString());
    }

    [Fact]
    public async Task ConnectListsUserAndWorkspaces()
    {
        using var fake = new FakePostman();
        var c = new PostmanClient("PMAK-test", fake.BaseUrl);
        Assert.Equal("Demo User", await c.GetUserAsync());
        var ws = await c.GetWorkspacesAsync();
        Assert.Equal(2, ws.Count);
        Assert.Equal("QA Team", ws[1].Name);
    }

    [Fact]
    public async Task WrongKeyGivesClearMessage()
    {
        using var fake = new FakePostman();
        var c = new PostmanClient("PMAK-wrong", fake.BaseUrl);
        var ex = await Assert.ThrowsAsync<PostmanException>(() => c.GetUserAsync());
        Assert.Equal(401, ex.Status);
        Assert.Contains("API key", ex.Message);
        var r = await c.SaveRequestAsync("ws1", "TrafficScope", Login(), false);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task SaveCreatesCollectionOnceAndAppendsNewestFirst()
    {
        using var fake = new FakePostman();
        var c = new PostmanClient("PMAK-test", fake.BaseUrl);
        var r1 = await c.SaveRequestAsync("ws2", "TrafficScope", Login(), false);
        Assert.True(r1.Ok, r1.Message);
        var second = Login(); second.Method = "GET"; second.RequestBody = null;
        var r2 = await c.SaveRequestAsync("ws2", "TrafficScope", second, false);
        Assert.True(r2.Ok, r2.Message);
        Assert.Single(fake.Collections);
        Assert.Equal("ws2", fake.CollectionWorkspace[r1.CollectionUid!]);
        var items = fake.Collections[r1.CollectionUid!]["item"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.StartsWith("GET ", items[0]!["name"]!.GetValue<string>());
        Assert.Equal(PostmanClient.SchemaV21, fake.Collections[r1.CollectionUid!]["info"]!["schema"]!.GetValue<string>());
        Assert.Contains("Saved to Postman: TrafficScope", r2.Message);
        Assert.Equal(1, fake.Calls.Count(x => x == "POST collections"));
    }

    [Fact]
    public async Task ExistingCollectionInAnotherWorkspaceIsNotReused()
    {
        using var fake = new FakePostman();
        var c = new PostmanClient("PMAK-test", fake.BaseUrl);
        await c.SaveRequestAsync("ws1", "TrafficScope", Login(), false);
        await c.SaveRequestAsync("ws2", "TrafficScope", Login(), false);
        Assert.Equal(2, fake.Collections.Count);
    }
}
