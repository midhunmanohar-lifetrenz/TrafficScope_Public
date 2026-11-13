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
            // Pass the secret on stdin, not as a command-line argument: arguments are visible to other
            // programs (ps) while the command runs. Unusual characters fall back to the argument form.
            if (StdinSafe.IsMatch(value) && StdinSafe.IsMatch(name))
                return RunStdin($"add-generic-password -U -s {Service} -a {name} -l \"TrafficScope {name}\" -w {value}\n") == 0;
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

    static readonly System.Text.RegularExpressions.Regex StdinSafe = new("^[A-Za-z0-9._~+/=-]{1,512}$");

    static int RunStdin(string commands)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-i");
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(commands);
        p.StandardInput.Close();
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode;
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
