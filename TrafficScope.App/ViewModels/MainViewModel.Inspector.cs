using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

/// <summary>Fills the inspector tabs for the selected request.</summary>
public partial class MainViewModel
{
    // ---------- inspector ----------
    /// <summary>Everything about a request that takes real work to prepare. Built off the screen thread for large bodies.</summary>
    sealed record InspectorData(string RequestBody, string ResponseBody, string RequestRaw, string ResponseRaw,
        string RequestHex, string ResponseHex, JsonNodeInfo? RequestTree, JsonNodeInfo? ResponseTree, bool Large);

    /// <summary>Bodies above this are prepared in the background so the window never freezes.</summary>
    public const int BackgroundLoadBytes = 256 * 1024;
    int _inspectorVersion;
    [ObservableProperty] bool _inspectorLoading;

    static long BodyBytes(Flow f) => (long)(f.RequestBody?.Length ?? 0) + (f.ResponseBody?.Length ?? 0);

    static InspectorData Prepare(Flow f)
    {
        var large = BodyBytes(f) > BackgroundLoadBytes;
        var requestBody = f.RequestBodyNote ?? (f.RequestBody is not { Length: > 0 } ? "No request body"
            : FormParser.IsForm(f.RequestContentType)
                ? "Form fields (decoded, see Raw for the original)\n\n" + string.Join("\n", FormParser.Parse(System.Text.Encoding.UTF8.GetString(f.RequestBody)).Select(p => $"{p.Name} = {p.Value}"))
                : Formatting.BodyText(f.RequestContentType, f.RequestBody));
        var responseRaw = Formatting.RawResponse(f);
        var responseBody = f.IsTunnel ? responseRaw : f.ResponseBodyNote ?? (f.ResponseBody is { Length: > 0 } ? Formatting.BodyText(f.ResponseContentType, f.ResponseBody)
            : f.StatusCode == null ? (f.Error ?? "Waiting for response…") : "No response body");
        return new InspectorData(
            Formatting.Preview(requestBody, f.RequestBody?.Length ?? 0),
            Formatting.Preview(responseBody, f.ResponseBody?.Length ?? 0),
            Formatting.Preview(Formatting.RawRequest(f), f.RequestBody?.Length ?? 0),
            Formatting.Preview(responseRaw, f.ResponseBody?.Length ?? 0),
            Formatting.HexDump(f.RequestBody) is { Length: > 0 } rh ? rh : f.RequestBodyNote ?? "No request body",
            Formatting.HexDump(f.ResponseBody) is { Length: > 0 } sh ? sh : f.ResponseBodyNote ?? "No response body",
            JsonTree.LooksLikeJson(f.RequestContentType, f.RequestBody) ? JsonTree.Build(f.RequestBody) : null,
            JsonTree.LooksLikeJson(f.ResponseContentType, f.ResponseBody) ? JsonTree.Build(f.ResponseBody) : null,
            large);
    }

    void LoadInspector()
    {
        var f = SelectedFlow?.Flow;
        var version = ++_inspectorVersion;
        RequestTree.Clear(); ResponseTree.Clear(); RequestHeaders.Clear(); ResponseHeaders.Clear();
        QueryParams.Clear(); FormFields.Clear(); RequestCookies.Clear(); ResponseCookies.Clear(); TimingRows.Clear();
        ResponseImage = null; JwtText = "";
        InspectorLoading = false;
        OnPropertyChanged(nameof(CanSaveRequestBody)); OnPropertyChanged(nameof(CanSaveResponseBody));
        if (f == null)
        {
            RequestHexText = ResponseHexText = "";
            RequestText = ResponseText = RequestBodyText = ResponseBodyText = RequestHeadersText = ResponseHeadersText = "";
            NotifyInspector(); return;
        }
        if (BodyBytes(f) > BackgroundLoadBytes)
        {
            // Large body: show the light parts now, prepare text and tree in the background.
            InspectorLoading = true;
            var loading = $"Loading {Formatting.Size(BodyBytes(f))}…";
            RequestBodyText = ResponseBodyText = RequestText = ResponseText = RequestHexText = ResponseHexText = loading;
            _ = Task.Run(() => Prepare(f)).ContinueWith(t =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _inspectorVersion) return;          // another request was selected meanwhile
                    InspectorLoading = false;
                    if (t.IsCompletedSuccessfully) ApplyPrepared(t.Result);
                    else RequestBodyText = ResponseBodyText = "Could not show this body. Use the save icon to get it as a file.";
                    NotifyInspector();
                }));
        }
        else ApplyPrepared(Prepare(f));
        RequestHeadersText = string.Join("\n", f.RequestHeaders.Select(h => $"{h.Key}: {h.Value}"));
        ResponseHeadersText = string.Join("\n", f.ResponseHeaders.Select(h => $"{h.Key}: {h.Value}"));

        if ((f.ResponseContentType ?? "").StartsWith("image/") && f.ResponseBody is { Length: > 0 } img && !(f.ResponseContentType ?? "").Contains("svg"))
        {
            try { using var ms = new MemoryStream(img); ResponseImage = new Bitmap(ms); } catch { ResponseImage = null; }
        }
        foreach (var h in f.RequestHeaders) RequestHeaders.Add(new NameValue(h.Key, h.Value));
        foreach (var h in f.ResponseHeaders) ResponseHeaders.Add(new NameValue(h.Key, h.Value));
        if (JwtDecoder.FindInHeaders(f.RequestHeaders) is { } jwt)
        {
            var exp = jwt.ExpiresUtc is { } e ? $"Expires {e.ToLocalTime():dd MMM yyyy HH:mm}" + (e < DateTime.UtcNow ? " (expired)" : "") + "\n" : "";
            JwtText = exp + "Header\n" + jwt.HeaderJson + "\n\nPayload\n" + jwt.PayloadJson;
        }
        foreach (var q in FormParser.Query(f.Url)) QueryParams.Add(q);
        if (FormParser.IsForm(f.RequestContentType) && f.RequestBody is { Length: > 0 } fb)
            foreach (var p in FormParser.Parse(System.Text.Encoding.UTF8.GetString(fb))) FormFields.Add(p);
        foreach (var c in CookieParser.Request(f.RequestHeaders)) RequestCookies.Add(c);
        foreach (var c in CookieParser.Response(f.ResponseHeaders)) ResponseCookies.Add(c);

        TimingRows.Add(new("Started", f.StartedAt.ToString("dd MMM yyyy HH:mm:ss.fff")));
        TimingRows.Add(new("Total time", f.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "pending"));
        TimingRows.Add(new("Request size", Formatting.Size(f.RequestBody?.Length ?? 0)));
        TimingRows.Add(new("Response size", Formatting.Size(f.ResponseBody?.Length ?? 0)));
        TimingRows.Add(new("Protocol", f.HttpVersion));
        TimingRows.Add(new("App", f.Process != null ? $"{f.AppName} (pid {f.Process.Pid})" : f.AppName));
        if (f.Process != null) TimingRows.Add(new("Executable", f.Process.Path));
        if (f.Mapped == "Local") TimingRows.Add(new("Map Local", "Answered from " + f.MappedTo + "; the server was not called."));
        if (f.Mapped == "Remote") TimingRows.Add(new("Map Remote", "Sent to " + f.MappedTo));
        if (f.Mocked && f.Mapped.Length == 0) TimingRows.Add(new("Note", "Answered by a mock rule; the server was not called."));
        if (f.Edited) TimingRows.Add(new("Note", "Changed at a breakpoint."));
        if (Settings.DelayMs > 0) TimingRows.Add(new("Slow network", $"+{Settings.DelayMs} ms added per request"));
        NotifyInspector();
    }

    void ApplyPrepared(InspectorData d)
    {
        RequestBodyText = d.RequestBody; ResponseBodyText = d.ResponseBody;
        RequestText = d.RequestRaw; ResponseText = d.ResponseRaw;
        RequestHexText = d.RequestHex; ResponseHexText = d.ResponseHex;
        RequestTree.Clear(); ResponseTree.Clear();
        // Large responses open one level only; every list shows 100 rows at a time.
        var open = d.Large ? 1 : 3;
        if (d.RequestTree != null) RequestTree.Add(new JsonNodeVm(d.RequestTree, 0, open));
        if (d.ResponseTree != null) ResponseTree.Add(new JsonNodeVm(d.ResponseTree, 0, open));
    }

    // ---------- save a body to a file ----------
    public bool CanSaveRequestBody => Sel?.RequestBody is { Length: > 0 };
    public bool CanSaveResponseBody => Sel?.ResponseBody is { Length: > 0 };

    [RelayCommand] Task SaveRequestBody() => SaveBody(true);
    [RelayCommand] Task SaveResponseBody() => SaveBody(false);

    async Task<bool> SaveBody(bool request)
    {
        var f = Sel;
        var body = request ? f?.RequestBody : f?.ResponseBody;
        if (f == null || Ui == null || body is not { Length: > 0 }) { await ShowToast(request ? "This request has no body to save." : "This response has no body to save."); return false; }
        var type = request ? f.RequestContentType : f.ResponseContentType;
        if (JsonTree.LooksLikeJson(type, body) && Formatting.IsText(type, body)) type = "application/json";
        var path = await Ui.PickSaveFileAsync(Formatting.BodyFileName(f.Url, type, request));
        if (path == null) return false;
        try
        {
            await File.WriteAllBytesAsync(path, body);
            Say($"Saved {Formatting.Size(body.Length)} to {Path.GetFileName(path)}.", NoticeKind.Success);
            return true;
        }
        catch (Exception ex) { Fail("Could not save the file. " + ex.Message); return false; }
    }

    /// <summary>Copies a body, or for a large one saves it to a file instead (a huge clipboard text freezes most apps).</summary>
    async Task CopyOrSaveBody(bool request)
    {
        var f = Sel; if (f == null) return;
        var body = request ? f.RequestBody : f.ResponseBody;
        if (body is { Length: > Formatting.LargeBodyBytes })
        {
            Say($"This body is {Formatting.Size(body.Length)}, too large to copy comfortably, so choose where to save it as a file.", NoticeKind.Info);
            await SaveBody(request);
            return;
        }
        await Copy(Formatting.BodyForCopy(request ? f.RequestContentType : f.ResponseContentType, body), request ? "Request body copied" : "Response body copied");
    }

    void NotifyInspector()
    {
        NotifyBodyViews();
        foreach (var p in new[] { nameof(HasForm), nameof(HasQuery), nameof(HasParams), nameof(NoParams), nameof(HasJwt) }) OnPropertyChanged(p);
    }

    [RelayCommand] void ToggleTree() => ShowTree = !ShowTree;
    /// <summary>Most rows "Expand all" will open at once; beyond that the rest stay closed so the window stays responsive.</summary>
    public const int ExpandAllBudget = 3000;
    [RelayCommand]
    void ExpandAll()
    {
        var budget = ExpandAllBudget;
        foreach (var n in RequestTree.Concat(ResponseTree)) n.SetExpanded(true, ref budget);
        if (budget <= 0) _ = ShowToast("Opened the first few thousand rows. Open the rest row by row, or save the body to a file.");
    }
    [RelayCommand] void CollapseAll() { foreach (var n in RequestTree.Concat(ResponseTree)) { n.SetExpanded(false); n.IsExpanded = true; } }
}
