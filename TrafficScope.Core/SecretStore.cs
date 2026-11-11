using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TrafficScope.Core;

/// <summary>Stores small secrets (the Postman API key): macOS Keychain, Windows DPAPI (current user).</summary>
public static class SecretStore
{
    const string Service = "TrafficScope";

    public static string? Get(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var f = WinFile(name);
                return File.Exists(f) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser)) : null;
            }
            var (code, output) = Run("find-generic-password", "-s", Service, "-a", name, "-w");
            return code == 0 ? output.TrimEnd('\n', '\r') : null;
        }
        catch { return null; }
    }

    public static bool Set(string name, string value)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(WinFile(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
                return true;
            }
            return Run("add-generic-password", "-U", "-s", Service, "-a", name, "-l", "TrafficScope " + name, "-w", value).Code == 0;
        }
        catch { return false; }
    }

    public static void Delete(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { File.Delete(WinFile(name)); return; }
            Run("delete-generic-password", "-s", Service, "-a", name);
        }
        catch { }
    }

    static string WinFile(string name) => Path.Combine(FavoritesStore.DataDir, name + ".secret");

    static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, o);
    }
}
