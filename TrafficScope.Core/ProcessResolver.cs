using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrafficScope.Core;

public interface IProcessResolver
{
    /// <summary>Finds the process that owns the client side of a loopback connection to the proxy.</summary>
    Task<ProcessInfo?> ResolveAsync(int clientPort, int proxyPort);
}

public static class ProcessResolver
{
    public static IProcessResolver Create() =>
        OperatingSystem.IsWindows() ? new CachingResolver(new WindowsProcessResolver())
        : new CachingResolver(new MacProcessResolver());
}

sealed class CachingResolver(IProcessResolver inner) : IProcessResolver
{
    readonly ConcurrentDictionary<int, (ProcessInfo? Info, DateTime At)> _cache = new();
    readonly ConcurrentDictionary<int, Task<ProcessInfo?>> _pending = new();

    public async Task<ProcessInfo?> ResolveAsync(int clientPort, int proxyPort)
    {
        if (_cache.TryGetValue(clientPort, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(15) && c.Info != null)
            return c.Info;
        var task = _pending.GetOrAdd(clientPort, _ => inner.ResolveAsync(clientPort, proxyPort));
        try
        {
            var info = await task;
            _cache[clientPort] = (info, DateTime.UtcNow);
            return info;
        }
        finally { _pending.TryRemove(clientPort, out _); }
    }
}

sealed class MacProcessResolver : IProcessResolver
{
    static readonly Regex Local = new(@":(\d+)->", RegexOptions.Compiled);

    public async Task<ProcessInfo?> ResolveAsync(int clientPort, int proxyPort)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var output = await Run("/usr/sbin/lsof", $"-nP -iTCP:{clientPort} -F pn");
            int pid = 0;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line[0] == 'p') int.TryParse(line.AsSpan(1), out pid);
                else if (line[0] == 'n' && pid != 0 && pid != Environment.ProcessId)
                {
                    var m = Local.Match(line);
                    if (m.Success && m.Groups[1].Value == clientPort.ToString())
                    {
                        var path = LibProc.PathOf(pid);
                        if (string.IsNullOrEmpty(path)) return null;
                        return new ProcessInfo(pid, path, System.IO.Path.GetFileName(path));
                    }
                }
            }
            await Task.Delay(40);
        }
        return null;
    }

    internal static async Task<string> Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text;
    }
}

sealed class WindowsProcessResolver : IProcessResolver
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public async Task<ProcessInfo?> ResolveAsync(int clientPort, int proxyPort)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pid = FindPid(clientPort, proxyPort);
            if (pid > 0)
            {
                var path = ImagePath(pid);
                if (path != null) return new ProcessInfo(pid, path, System.IO.Path.GetFileName(path));
            }
            await Task.Delay(40);
        }
        return null;
    }

    static int FindPid(int clientPort, int proxyPort)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return 0;
            var count = Marshal.ReadInt32(buf);
            var row = buf + 4;
            for (var i = 0; i < count; i++, row += 24)
            {
                var localPort = (ushort)IPHostToNetwork(Marshal.ReadInt32(row, 8));
                var remotePort = (ushort)IPHostToNetwork(Marshal.ReadInt32(row, 16));
                var pid = Marshal.ReadInt32(row, 20);
                if (localPort == clientPort && remotePort == proxyPort && pid != Environment.ProcessId) return pid;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return 0;
    }

    static int IPHostToNetwork(int v) => ((v & 0xFF) << 8) | ((v >> 8) & 0xFF);

    internal static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
