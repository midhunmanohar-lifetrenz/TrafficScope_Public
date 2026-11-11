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
            "/usr/sbin/networksetup -setwebproxy *", "/usr/sbin/networksetup -setsecurewebproxy *",
            "/usr/sbin/networksetup -setwebproxystate *", "/usr/sbin/networksetup -setsecurewebproxystate *",
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
    public void InstalledCheckIsReadOnlyAndDoesNotThrow()
    {
        var _ = ProxyPermission.IsInstalled();   // runs `sudo -n -l`, which never executes the command
    }
}
