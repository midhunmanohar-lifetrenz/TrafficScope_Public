namespace TrafficScope.Core;

public static class AppMatcher
{
    static readonly StringComparison Cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when the process image path equals the app key or lives under it (bundle or install folder).</summary>
    public static bool Matches(string? processPath, string key)
    {
        if (string.IsNullOrEmpty(processPath) || string.IsNullOrEmpty(key)) return false;
        if (processPath.Equals(key, Cmp)) return true;
        var k = key.TrimEnd('/', '\\');
        return processPath.Length > k.Length
            && processPath.StartsWith(k, Cmp)
            && (processPath[k.Length] == '/' || processPath[k.Length] == '\\');
    }

    public static bool MatchesAny(string? processPath, IEnumerable<string> keys) =>
        keys.Any(k => Matches(processPath, k));
}
