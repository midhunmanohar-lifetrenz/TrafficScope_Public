using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TrafficScope.Core;

/// <summary>
/// Optional one-time macOS rule (/etc/sudoers.d/trafficscope) so Record/Stop can switch the system
/// proxy without a password. It allows only networksetup's four proxy on/off commands.
/// </summary>
public static class ProxyPermission
{
    public const string RulePath = "/etc/sudoers.d/trafficscope";
    public const string NetworkSetup = "/usr/sbin/networksetup";
    public static readonly string[] AllowedVerbs = { "-setwebproxy", "-setsecurewebproxy", "-setwebproxystate", "-setsecurewebproxystate" };
    static readonly Regex SafeUser = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    public static string CurrentUser => Environment.UserName;

    public static bool IsValidUser(string user) => SafeUser.IsMatch(user);

    public static string BuildRule(string user)
    {
        if (!IsValidUser(user)) throw new ArgumentException("Unsupported user name for a permission rule: " + user);
        var cmds = string.Join(", ", AllowedVerbs.Select(v => $"{NetworkSetup} {v} *"));
        return "# Installed by TrafficScope so Record/Stop can switch the HTTP/HTTPS proxy without a password.\n"
             + "# Remove it in TrafficScope > Tools > Settings, or delete this file.\n"
             + $"{user} ALL=(root) NOPASSWD: {cmds}\n";
    }

    /// <summary>Read-only check: asks sudo whether one allowed command would run without a password.</summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var (code, _) = Run("/usr/bin/sudo", "-n", "-l", NetworkSetup, "-setwebproxystate", "Wi-Fi", "off");
            return code == 0;
        }
        catch { return false; }
    }

    /// <summary>Checks the rule with visudo, then installs it with one admin prompt.</summary>
    public static (bool Ok, string Message) Install()
    {
        if (!OperatingSystem.IsMacOS()) return (false, "Only needed on macOS.");
        var user = CurrentUser;
        if (!IsValidUser(user)) return (false, $"Your user name ({user}) contains characters a permission rule cannot use.");
        var tmp = Path.Combine(Path.GetTempPath(), "trafficscope-sudoers-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(tmp, BuildRule(user));
        try
        {
            var script = $"/usr/sbin/visudo -cf '{tmp}' && /usr/sbin/chown root:wheel '{tmp}' && /bin/chmod 0440 '{tmp}' && /bin/mv -f '{tmp}' {RulePath}";
            var (ok, msg) = MacShell.RunAdmin(script, "TrafficScope will stop asking for your password when you press Record or Stop.");
            if (!ok) return (false, msg.Contains("-128") ? "Cancelled." : msg);
            return IsInstalled() ? (true, "Done. Record and Stop will no longer ask for your password.")
                : (false, "The rule was written but macOS is not using it. Check that /etc/sudoers includes /private/etc/sudoers.d.");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static (bool Ok, string Message) Remove()
    {
        if (!OperatingSystem.IsMacOS()) return (false, "Only needed on macOS.");
        var (ok, msg) = MacShell.RunAdmin($"/bin/rm -f {RulePath}", "TrafficScope will remove its proxy permission rule.");
        if (!ok) return (false, msg.Contains("-128") ? "Cancelled." : msg);
        return IsInstalled() ? (false, "The rule is still active.") : (true, "Removed. Record and Stop will ask for your password again.");
    }

    internal static (int Code, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o + e);
    }
}
