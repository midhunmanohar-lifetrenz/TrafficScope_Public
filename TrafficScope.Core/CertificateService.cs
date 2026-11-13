using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace TrafficScope.Core;

public static class CertificateService
{
    public const string RootName = "TrafficScope Root CA";

    public static string ExportPath(string dataDir) => Path.Combine(dataDir, "TrafficScopeRootCA.cer");
    public static string KeyPath(string dataDir) => Path.Combine(dataDir, "rootCert.pfx");

    /// <summary>Password of key files written before 0.3.3, and of isolated test folders.</summary>
    public const string LegacyPassword = "trafficscope-local";
    public const string PasswordName = "root-ca-key";

    /// <summary>Owner-only access to the data folder and the root key (macOS). Windows profiles are already per-user.</summary>
    public static void RestrictKeyFile(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (Directory.Exists(dataDir)) File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var pfx = KeyPath(dataDir);
            if (File.Exists(pfx)) File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    /// <summary>
    /// Returns the password for the root key file. In the real data folder the password is random and
    /// lives in the Keychain / DPAPI; an older key file is re-encrypted in place (same certificate, so
    /// it stays trusted). Other folders (tests, TS_DATA_DIR) keep the fixed password and never touch the Keychain.
    /// </summary>
    public static string ProtectKey(string dataDir)
    {
        var real = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TS_DATA_DIR"))
                   && string.Equals(Path.GetFullPath(dataDir).TrimEnd('/', '\\'), Path.GetFullPath(FavoritesStore.DataDir).TrimEnd('/', '\\'), StringComparison.Ordinal);
        if (!real) { RestrictKeyFile(dataDir); return LegacyPassword; }
        return ProtectKey(dataDir, () => SecretStore.Get(PasswordName), v => SecretStore.Set(PasswordName, v));
    }

    public static string ProtectKey(string dataDir, Func<string?> getSecret, Func<string, bool> setSecret)
    {
        RestrictKeyFile(dataDir);
        try
        {
            var pfx = KeyPath(dataDir);
            var stored = getSecret();
            if (!File.Exists(pfx))
            {
                if (!string.IsNullOrEmpty(stored)) return stored;
                var fresh = NewPassword();
                return setSecret(fresh) ? fresh : LegacyPassword;
            }
            if (!string.IsNullOrEmpty(stored) && CanOpen(pfx, stored)) return stored;
            if (!CanOpen(pfx, LegacyPassword))
            {
                // Neither password opens the key (for example the Keychain could not be read). The proxy
                // library would now write a new key over it, so keep one copy of the unreadable file.
                try
                {
                    var keep = Path.Combine(dataDir, "rootCert.unreadable.pfx");
                    if (!File.Exists(keep)) { File.Copy(pfx, keep); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keep, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                }
                catch { }
                return string.IsNullOrEmpty(stored) ? LegacyPassword : stored;
            }

            // Older key file: same key and certificate, new random password.
            var next = NewPassword();
            var tmp = pfx + ".new";
            using (var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, LegacyPassword, X509KeyStorageFlags.Exportable))
                File.WriteAllBytes(tmp, cert.Export(X509ContentType.Pkcs12, next));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!CanOpen(tmp, next) || !setSecret(next)) { try { File.Delete(tmp); } catch { } return LegacyPassword; }
            File.Move(tmp, pfx, overwrite: true);
            return next;
        }
        catch { return LegacyPassword; }
    }

    static string NewPassword() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    static bool CanOpen(string pfx, string password)
    {
        try { using var c = X509CertificateLoader.LoadPkcs12FromFile(pfx, password, X509KeyStorageFlags.Exportable); return c.HasPrivateKey; }
        catch { return false; }
    }

    /// <summary>Stops trusting the root CA and removes it from the user's store (login keychain / CurrentUser Root).</summary>
    public static (bool Ok, string Message) Remove(X509Certificate2 cert, string dataDir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false)) store.Remove(c);
            }
            else
            {
                var file = ExportPath(dataDir);
                File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
                var keychain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");
                RunSecurity("remove-trusted-cert", file);
                RunSecurity("delete-certificate", "-Z", cert.Thumbprint, keychain);
            }
            return IsTrusted(cert) ? (false, "The certificate is still trusted. Approve the system prompt, or remove TrafficScope Root CA in Keychain Access.")
                : (true, "Certificate removed. HTTPS can no longer be read until you install it again.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static int RunSecurity(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

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
                store.Add(X509CertificateLoader.LoadCertificateFromFile(file));
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
