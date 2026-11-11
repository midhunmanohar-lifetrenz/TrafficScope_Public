using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrafficScope.Core;

/// <summary>Points the OS HTTP/HTTPS proxy at TrafficScope and restores it afterwards.</summary>
public static class SystemProxy
{
    const string WinKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    static object? _prevEnable, _prevServer;

    [DllImport("wininet.dll")] static extern bool InternetSetOption(IntPtr h, int option, IntPtr buf, int len);

    public static (bool Ok, string Message) Enable(int port)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WinKey, true)!;
                _prevEnable ??= k.GetValue("ProxyEnable");
                _prevServer ??= k.GetValue("ProxyServer");
                SaveBackup(_prevEnable, _prevServer);
                k.SetValue("ProxyServer", $"127.0.0.1:{port}");
                k.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
                Notify();
                return (true, "System proxy enabled.");
            }
            return RunMac(MacCommands(true, port, Services()), "TrafficScope needs to set the system proxy.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool Ok, string Message) Disable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WinKey, true)!;
                if (_prevEnable == null && _prevServer == null) (_prevEnable, _prevServer) = LoadBackup();
                k.SetValue("ProxyEnable", _prevEnable ?? 0, Microsoft.Win32.RegistryValueKind.DWord);
                if (_prevServer != null) k.SetValue("ProxyServer", _prevServer); else k.DeleteValue("ProxyServer", false);
                _prevEnable = _prevServer = null;
                DeleteBackup();
                Notify();
                return (true, "System proxy restored.");
            }
            return RunMac(MacCommands(false, 0, Services()), "TrafficScope needs to restore the system proxy.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static string BackupFile => Path.Combine(FavoritesStore.DataDir, "proxy-backup.json");

    static void SaveBackup(object? enable, object? server)
    {
        try
        {
            File.WriteAllText(BackupFile, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            { ["enable"] = enable?.ToString(), ["server"] = server?.ToString() }));
        }
        catch { }
    }

    static (object? Enable, object? Server) LoadBackup()
    {
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(BackupFile))!;
            object? e = int.TryParse(d.GetValueOrDefault("enable"), out var n) ? n : null;
            return (e, d.GetValueOrDefault("server"));
        }
        catch { return (null, null); }
    }

    static void DeleteBackup() { try { File.Delete(BackupFile); } catch { } }

    /// <summary>
    /// Finds a system proxy that still points at a TrafficScope port nobody is listening on
    /// (left behind if the app crashed or was force-quit while recording). Read-only.
    /// </summary>
    public static (bool Leftover, string Detail) DetectLeftover()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WinKey, false);
                var enabled = Convert.ToInt32(k?.GetValue("ProxyEnable") ?? 0) == 1;
                var server = k?.GetValue("ProxyServer") as string;
                return IsDeadLocalProxy(enabled, server) ? (true, server!) : (false, "");
            }
            if (!OperatingSystem.IsMacOS()) return (false, "");
            foreach (var svc in Services())
                foreach (var verb in new[] { "-getwebproxy", "-getsecurewebproxy" })
                {
                    var (_, output) = ProxyPermission.Run(ProxyPermission.NetworkSetup, verb, svc);
                    var (enabled, server) = ParseNetworkSetup(output);
                    if (IsDeadLocalProxy(enabled, server)) return (true, $"{svc}: {server}");
                }
        }
        catch { }
        return (false, "");
    }

    /// <summary>Parses `networksetup -getwebproxy` output into (enabled, "host:port").</summary>
    public static (bool Enabled, string? Server) ParseNetworkSetup(string output)
    {
        string? V(string key) => output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..].Trim();
        var enabled = string.Equals(V("Enabled"), "Yes", StringComparison.OrdinalIgnoreCase);
        var host = V("Server"); var port = V("Port");
        return (enabled, string.IsNullOrEmpty(host) ? null : $"{host}:{port}");
    }

    public static bool IsDeadLocalProxy(bool enabled, string? server, Func<int, bool>? isListening = null)
    {
        if (!enabled || string.IsNullOrEmpty(server)) return false;
        var s = server.Contains('=') ? server.Split(';')[0].Split('=').Last() : server;   // "http=127.0.0.1:8888;https=…"
        var i = s.LastIndexOf(':');
        if (i < 0 || !int.TryParse(s[(i + 1)..], out var port)) return false;
        var host = s[..i].Trim();
        if (host != "127.0.0.1" && host != "localhost" && host != "[::1]") return false;
        return !(isListening ?? IsListening)(port);
    }

    static bool IsListening(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            return c.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    static void Notify()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    /// <summary>True when the last Mac change ran without a password prompt.</summary>
    public static bool LastRunWithoutPrompt { get; private set; }

    static List<string> Services()
    {
        var psi = new ProcessStartInfo(ProxyPermission.NetworkSetup, "-listallnetworkservices") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        return p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('*')).ToList();
    }

    public static List<string[]> MacCommands(bool on, int port, IEnumerable<string> services)
    {
        var cmds = new List<string[]>();
        foreach (var svc in services)
        {
            if (on)
            {
                cmds.Add(new[] { "-setwebproxy", svc, "127.0.0.1", port.ToString() });
                cmds.Add(new[] { "-setsecurewebproxy", svc, "127.0.0.1", port.ToString() });
            }
            else
            {
                cmds.Add(new[] { "-setwebproxystate", svc, "off" });
                cmds.Add(new[] { "-setsecurewebproxystate", svc, "off" });
            }
        }
        return cmds;
    }

    static (bool Ok, string Message) RunMac(List<string[]> cmds, string prompt)
    {
        if (ProxyPermission.IsInstalled())
        {
            var failed = new List<string>();
            foreach (var c in cmds)
            {
                var (code, output) = ProxyPermission.Run("/usr/bin/sudo", new[] { "-n", ProxyPermission.NetworkSetup }.Concat(c).ToArray());
                if (code != 0) failed.Add($"{string.Join(' ', c)}: {output.Trim()}");
            }
            if (failed.Count == 0) { LastRunWithoutPrompt = true; return (true, "Done."); }
            // fall through to the password prompt if the rule stopped working
        }
        LastRunWithoutPrompt = false;
        var script = string.Join("; ", cmds.Select(c => ProxyPermission.NetworkSetup + " " + string.Join(' ', c.Select(ShellQuote))));
        return MacShell.RunAdmin(script, prompt);
    }

    static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

public static class PostmanLauncher
{
    public static void Open()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", new[] { "-a", "Postman" });
            else Process.Start(new ProcessStartInfo("postman://") { UseShellExecute = true });
        }
        catch { }
    }
}
