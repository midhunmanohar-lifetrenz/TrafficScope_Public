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
    void LoadInspector()
    {
        var f = SelectedFlow?.Flow;
        RequestTree.Clear(); ResponseTree.Clear(); RequestHeaders.Clear(); ResponseHeaders.Clear();
        QueryParams.Clear(); FormFields.Clear(); RequestCookies.Clear(); ResponseCookies.Clear(); TimingRows.Clear();
        ResponseImage = null; JwtText = "";
        if (f == null)
        {
            RequestHexText = ResponseHexText = "";
            RequestText = ResponseText = RequestBodyText = ResponseBodyText = RequestHeadersText = ResponseHeadersText = "";
            NotifyInspector(); return;
        }
        RequestHexText = Formatting.HexDump(f.RequestBody) is { Length: > 0 } rh ? rh : f.RequestBodyNote ?? "No request body";
        ResponseHexText = Formatting.HexDump(f.ResponseBody) is { Length: > 0 } sh ? sh : f.ResponseBodyNote ?? "No response body";
        RequestText = Formatting.RawRequest(f);
        ResponseText = Formatting.RawResponse(f);
        RequestBodyText = f.RequestBodyNote ?? (f.RequestBody is not { Length: > 0 } ? "No request body"
            : FormParser.IsForm(f.RequestContentType)
                ? "Form fields (decoded, see Raw for the original)\n\n" + string.Join("\n", FormParser.Parse(System.Text.Encoding.UTF8.GetString(f.RequestBody)).Select(p => $"{p.Name} = {p.Value}"))
                : Formatting.BodyText(f.RequestContentType, f.RequestBody));
        ResponseBodyText = f.IsTunnel ? ResponseText : f.ResponseBodyNote ?? (f.ResponseBody is { Length: > 0 } ? Formatting.BodyText(f.ResponseContentType, f.ResponseBody)
            : f.StatusCode == null ? (f.Error ?? "Waiting for response…") : "No response body");
        RequestHeadersText = string.Join("\n", f.RequestHeaders.Select(h => $"{h.Key}: {h.Value}"));
        ResponseHeadersText = string.Join("\n", f.ResponseHeaders.Select(h => $"{h.Key}: {h.Value}"));

        if (JsonTree.LooksLikeJson(f.RequestContentType, f.RequestBody) && JsonTree.Build(f.RequestBody) is { } rq) RequestTree.Add(new JsonNodeVm(rq, 0));
        if (JsonTree.LooksLikeJson(f.ResponseContentType, f.ResponseBody) && JsonTree.Build(f.ResponseBody) is { } rs) ResponseTree.Add(new JsonNodeVm(rs, 0));
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

    void NotifyInspector()
    {
        NotifyBodyViews();
        foreach (var p in new[] { nameof(HasForm), nameof(HasQuery), nameof(HasParams), nameof(NoParams), nameof(HasJwt) }) OnPropertyChanged(p);
    }

    [RelayCommand] void ToggleTree() => ShowTree = !ShowTree;
    [RelayCommand] void ExpandAll() { foreach (var n in RequestTree.Concat(ResponseTree)) n.SetExpanded(true); }
    [RelayCommand] void CollapseAll() { foreach (var n in RequestTree.Concat(ResponseTree)) { n.SetExpanded(false); n.IsExpanded = true; } }
}
