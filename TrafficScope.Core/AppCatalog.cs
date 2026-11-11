using System.Diagnostics;
using System.Text.Json;

namespace TrafficScope.Core;

public static class AppCatalog
{
    public static async Task<List<AppEntry>> GetRunningAsync() =>
        OperatingSystem.IsWindows() ? WindowsRunning() : await MacRunningAsync();

    public static List<AppEntry> GetInstalled() =>
        OperatingSystem.IsWindows() ? WindowsInstalled() : MacInstalled();

    static int BundleEnd(string path)
    {
        var i = path.IndexOf(".app/", StringComparison.Ordinal);
        return i < 0 ? -1 : i + 4;
    }

    static Task<List<AppEntry>> MacRunningAsync() => Task.Run(() =>
    {
        var map = new Dictionary<string, AppEntry>();
        foreach (var (_, path) in LibProc.All())
        {
            var end = BundleEnd(path);
            if (end < 0 || path.StartsWith("/System/Library/") || path.StartsWith("/usr/")) continue;
            var bundle = path[..end];
            map.TryAdd(bundle, new AppEntry(System.IO.Path.GetFileNameWithoutExtension(bundle), bundle) { IsRunning = true });
        }
        return map.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    static List<AppEntry> MacInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<AppEntry>();
        foreach (var dir in new[] { "/Applications", "/Applications/Utilities", System.IO.Path.Combine(home, "Applications"), "/System/Applications" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var app in Directory.EnumerateDirectories(dir, "*.app"))
                result.Add(new AppEntry(System.IO.Path.GetFileNameWithoutExtension(app), app));
        }
        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<AppEntry> WindowsRunning()
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                var name = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                map.TryAdd(path, new AppEntry(name!, path) { IsRunning = true });
            }
            catch { }
            finally { p.Dispose(); }
        }
        return map.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<AppEntry> WindowsInstalled()
    {
        var result = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return new();
        foreach (var (hive, sub) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            using var root = hive.OpenSubKey(sub);
            if (root == null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    var dn = k?.GetValue("DisplayName") as string;
                    var loc = (k?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) continue;
                    result.TryAdd(loc.TrimEnd('\\'), new AppEntry(dn, loc.TrimEnd('\\')));
                }
                catch { }
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class FavoritesStore
{
    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TS_DATA_DIR");
            if (!string.IsNullOrEmpty(env)) { Directory.CreateDirectory(env); return env; }
            var d = OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "TrafficScope")
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficScope");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    readonly string _file = System.IO.Path.Combine(DataDir, "favorites.json");

    public List<AppEntry> Load()
    {
        try { return JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(_file)) ?? new(); }
        catch { return new(); }
    }

    public void Save(IEnumerable<AppEntry> favorites)
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(favorites.Select(f => new AppEntry(f.Name, f.Key)), new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
