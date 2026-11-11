using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using TrafficScope.Core;

namespace TrafficScope;

/// <summary>Loads real app icons: .icns via sips on macOS, the exe's associated icon on Windows. Cached on disk.</summary>
public static class AppIcons
{
    static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();
    static readonly string Dir = Path.Combine(FavoritesStore.DataDir, "icons");

    public static Task<Bitmap?> GetAsync(string key) => Cache.GetOrAdd(key, k => Task.Run(() => Load(k)));

    static Bitmap? Load(string key)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var png = Path.Combine(Dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16] + ".png");
            if (!File.Exists(png))
            {
                if (OperatingSystem.IsMacOS()) { if (!MacIcon(key, png)) return null; }
                else if (OperatingSystem.IsWindows()) { if (!WinIcon(key, png)) return null; }
                else return null;
            }
            using var fs = File.OpenRead(png);
            return new Bitmap(fs);
        }
        catch { return null; }
    }

    static bool MacIcon(string bundle, string png)
    {
        var res = Path.Combine(bundle, "Contents", "Resources");
        if (!Directory.Exists(res)) return false;
        string? icns = null;
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (File.Exists(plist))
        {
            var name = Run("/usr/bin/plutil", "-extract", "CFBundleIconFile", "raw", "-o", "-", plist).Trim();
            if (name.Length > 0)
            {
                var c = Path.Combine(res, name.EndsWith(".icns") ? name : name + ".icns");
                if (File.Exists(c)) icns = c;
            }
        }
        icns ??= Directory.EnumerateFiles(res, "*.icns").FirstOrDefault();
        if (icns == null) return false;
        Run("/usr/bin/sips", "-s", "format", "png", "-Z", "64", icns, "--out", png);
        return File.Exists(png);
    }

    static bool WinIcon(string path, string png)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var exe = File.Exists(path) ? path : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.exe").FirstOrDefault() : null;
        if (exe == null) return false;
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        if (icon == null) return false;
        using var bmp = icon.ToBitmap();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return true;
    }

    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
        return o;
    }
}
