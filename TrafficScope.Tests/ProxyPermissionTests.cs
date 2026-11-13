using System.Diagnostics;
using TrafficScope.Core;
using Xunit;

namespace TrafficScope.Tests;

public class ProxyPermissionTests
{
    [Fact]
    public void RuleAllowsOnlyTheFourProxyCommands()
    {
        var rule = ProxyPermission.BuildRule("jane.doe");
        var line = rule.Split('\n').Single(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.StartsWith("jane.doe ALL=(root) NOPASSWD: ", line);
        var cmds = line[(line.IndexOf("NOPASSWD:") + 9)..].Split(',').Select(c => c.Trim()).ToList();
        Assert.Equal(new[]
        {
            "/usr/sbin/networksetup -setwebproxy * 127.0.0.1 *", "/usr/sbin/networksetup -setsecurewebproxy * 127.0.0.1 *",
            "/usr/sbin/networksetup -setwebproxystate * off", "/usr/sbin/networksetup -setsecurewebproxystate * off",
        }, cmds);
        Assert.DoesNotContain("ALL\n", rule.Replace("ALL=(root)", ""));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x;rm")]
    [InlineData("root\nALL")]
    [InlineData("")]
    [InlineData("%admin")]
    public void UnsafeUserNamesAreRefused(string user)
    {
        Assert.False(ProxyPermission.IsValidUser(user));
        Assert.Throws<ArgumentException>(() => ProxyPermission.BuildRule(user));
    }

    [Fact]
    public void RulePassesVisudoSyntaxCheck()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var f = Path.Combine(Path.GetTempPath(), "ts-rule-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(f, ProxyPermission.BuildRule(ProxyPermission.CurrentUser));
        var psi = new ProcessStartInfo("/usr/sbin/visudo") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-cf"); psi.ArgumentList.Add(f);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        File.Delete(f);
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("parsed OK", output);
    }

    [Fact]
    public void ProxyCommandsAreCoveredByTheRuleAndKeepServiceNamesWhole()
    {
        var on = SystemProxy.MacCommands(true, 8888, new[] { "Wi-Fi", "USB 10/100/1000 LAN" });
        var off = SystemProxy.MacCommands(false, 0, new[] { "Wi-Fi", "USB 10/100/1000 LAN" });
        Assert.Equal(4, on.Count); Assert.Equal(4, off.Count);
        Assert.All(on.Concat(off), c => Assert.Contains(c[0], ProxyPermission.AllowedVerbs));
        Assert.Equal(new[] { "-setwebproxy", "USB 10/100/1000 LAN", "127.0.0.1", "8888" }, on[2]);
        Assert.Equal(new[] { "-setsecurewebproxystate", "Wi-Fi", "off" }, off[1]);
    }

    [Fact]
    public void InstallScriptBuildsTheRuleAsRootInsideSudoersDir()
    {
        var script = ProxyPermission.InstallScript("jane.doe");
        Assert.Contains("mktemp /etc/sudoers.d/.trafficscope.", script);       // never a user-writable temp file
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/'), script);
        Assert.Contains("visudo -cf \"$T\"", script);
        Assert.Contains("mv -f \"$T\" /etc/sudoers.d/trafficscope", script);
        Assert.True(script.IndexOf("visudo") < script.IndexOf("/bin/mv"));
    }

    [Fact]
    public void InstallScriptWritesExactlyTheRuleText()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Run only the harmless printf part of the script and compare with the rule.
        var script = ProxyPermission.InstallScript("jane.doe");
        var start = script.IndexOf("/usr/bin/printf"); var end = script.IndexOf(" > \"$T\"");
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script[start..end]);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        Assert.Equal(ProxyPermission.BuildRule("jane.doe"), output);
    }

    [Fact]
    public void RestorePutsBackAPreviousProxyAndSwitchesOursOff()
    {
        var backup = new[] { new SystemProxy.MacProxy("Wi-Fi", true, "proxy.corp.example", "3128") };
        var cmds = SystemProxy.MacRestoreCommands(new[] { "Wi-Fi", "USB 10/100/1000 LAN" }, backup);
        Assert.Equal(4, cmds.Count);
        Assert.Equal(new[] { "-setwebproxystate", "Wi-Fi", "off" }, cmds[0]);
        Assert.Equal(new[] { "-setsecurewebproxy", "Wi-Fi", "proxy.corp.example", "3128" }, cmds[1]);
        Assert.Equal(new[] { "-setsecurewebproxystate", "USB 10/100/1000 LAN", "off" }, cmds[3]);
        Assert.Equal(SystemProxy.MacCommands(false, 0, new[] { "Wi-Fi" }), SystemProxy.MacRestoreCommands(new[] { "Wi-Fi" }, Array.Empty<SystemProxy.MacProxy>()));
    }

    [Theory]
    [InlineData("Enabled: Yes\nServer: proxy.corp.example\nPort: 3128\n", "proxy.corp.example", "3128")]
    [InlineData("Enabled: Yes\nServer: 127.0.0.1\nPort: 61462\n", null, null)]     // ours: nothing to restore
    [InlineData("Enabled: No\nServer: proxy.corp.example\nPort: 3128\n", null, null)]
    [InlineData("Enabled: No\nServer: \nPort: 0\n", null, null)]
    public void OnlyARealPreviousProxyIsRemembered(string output, string? host, string? port)
    {
        var p = SystemProxy.ParseMacProxy("Wi-Fi", false, output);
        Assert.Equal(host, p?.Host); Assert.Equal(port, p?.Port);
    }

    [Theory]
    [InlineData("proxy.corp.example", "3128", true)]
    [InlineData("10.0.0.8", "8080", true)]
    [InlineData("evil.example; rm -rf ~", "3128", false)]
    [InlineData("proxy.corp.example", "99999", false)]
    [InlineData("proxy.corp.example", "abc", false)]
    [InlineData("127.0.0.1", "8888", false)]
    public void SavedProxyIsValidatedBeforeItIsRestored(string host, string port, bool ok) =>
        Assert.Equal(ok, SystemProxy.IsValidBackup(new SystemProxy.MacProxy("Wi-Fi", false, host, port)));

    [Fact]
    public void InstalledCheckIsReadOnlyAndDoesNotThrow()
    {
        var _ = ProxyPermission.IsInstalled();   // runs `sudo -n -l`, which never executes the command
    }
}
