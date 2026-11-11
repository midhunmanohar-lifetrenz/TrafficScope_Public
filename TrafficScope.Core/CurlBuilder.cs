using System.Text;

namespace TrafficScope.Core;

public static class CurlBuilder
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Connection", "Proxy-Connection", "Accept-Encoding" };

    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Build(Flow f, bool mask = false)
    {
        var sb = new StringBuilder("curl");
        if (!f.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)) sb.Append(" -X ").Append(f.Method);
        sb.Append(' ').Append(Quote(f.Url));
        if (f.RequestHeaders.Any(h => h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))) sb.Append(" --compressed");
        foreach (var h in Redactor.Apply(f.RequestHeaders, mask))
            if (!Skip.Contains(h.Key)) sb.Append(" \\\n  -H ").Append(Quote($"{h.Key}: {h.Value}"));
        if (f.RequestBody is { Length: > 0 } body)
        {
            if (Formatting.IsText(f.RequestContentType, body))
                sb.Append(" \\\n  --data-raw ").Append(Quote(Encoding.UTF8.GetString(body)));
            else
                sb.Append(" \\\n  --data-binary ").Append(Quote("@request-body.bin")).Append("  # binary body: " + body.Length + " bytes");
        }
        return sb.ToString();
    }
}
