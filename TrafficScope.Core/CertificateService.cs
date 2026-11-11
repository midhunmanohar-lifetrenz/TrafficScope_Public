using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace TrafficScope.Core;

public static class CertificateService
{
    public const string RootName = "TrafficScope Root CA";

    public static string ExportPath(string dataDir) => Path.Combine(dataDir, "TrafficScopeRootCA.cer");

    public static bool IsTrusted(X509Certificate2 cert)
    {
        if (OperatingSystem.IsWindows())
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
        }
        // verify-cert fails (non-zero) unless the system actually trusts the root, not merely stores it.
        var file = Path.Combine(Path.GetTempPath(), "ts-verify-" + Guid.NewGuid().ToString("N")[..8] + ".cer");
        File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("verify-cert"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(file);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Installs the root CA. Windows shows a security prompt; macOS asks for the admin password.</summary>
    public static (bool Ok, string Message) Install(X509Certificate2 cert, string dataDir)
    {
        var file = ExportPath(dataDir);
        File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Add(new X509Certificate2(file));
                return (true, "Certificate installed for the current user.");
            }
            // Trust for the current user (login keychain). Run as the user, not via an admin script:
            // macOS only allows trust changes when it can show its own password / Touch ID prompt,
            // and a root shell from osascript has no way to show it ("no user interaction was possible").
            var keychain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");
            var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in MacTrustArgs(keychain, file)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) return (true, "Done.");
            return (false, err.Contains("canceled", StringComparison.OrdinalIgnoreCase) || err.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? "macOS did not get your approval. Press Install certificate again and approve the macOS password or Touch ID prompt."
                : err.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Arguments for user-domain trust (no -d admin flag, no sudo).</summary>
    public static string[] MacTrustArgs(string keychain, string certFile) =>
        new[] { "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-p", "basic", "-k", keychain, certFile };
}

public static class MacShell
{
    public static (bool Ok, string Message) RunAdmin(string shellScript, string prompt)
    {
        var escaped = shellScript.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var apple = $"do shell script \"{escaped}\" with prompt \"{prompt}\" with administrator privileges";
        var psi = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(apple);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? (true, "Done.") : (false, err.Trim());
    }
}
