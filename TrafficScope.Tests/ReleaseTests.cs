using System.Text;
using TrafficScope.Core;
using TrafficScope.Setup;
using Xunit;

namespace TrafficScope.Tests;

sealed class FakeSystem : ISystem
{
    public readonly List<(string Link, string Target)> Shortcuts = new();
    public readonly Dictionary<string, IDictionary<string, object>> Entries = new();
    public bool Running;
    public void CreateShortcut(string linkPath, string target, string workingDir, string description) { Shortcuts.Add((linkPath, target)); File.WriteAllText(linkPath, target); }
    public void WriteUninstallEntry(string keyName, IDictionary<string, object> values) => Entries[keyName] = values;
    public void DeleteUninstallEntry(string keyName) => Entries.Remove(keyName);
    public bool IsAppRunning() => Running;
}

public class InstallerTests
{
    static (InstallPaths P, string Setup, string Root) Temp()
    {
        var root = Path.Combine(Path.GetTempPath(), "ts-inst-" + Guid.NewGuid().ToString("N")[..6]);
        var p = new InstallPaths(Path.Combine(root, "Programs", "TrafficScope"), Path.Combine(root, "Start Menu"), Path.Combine(root, "Desktop"));
        Directory.CreateDirectory(p.DesktopDir);
        var setup = Path.Combine(root, "TrafficScope-0.3.0-Setup.exe");
        File.WriteAllText(setup, "setup-binary");
        return (p, setup, root);
    }

    [Fact]
    public void InstallCopiesAppAndUninstallerAndRegisters()
    {
        var (p, setup, _) = Temp();
        var sys = new FakeSystem();
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app-v1")), setup, p, sys, desktopShortcut: true);
        Assert.Equal("app-v1", File.ReadAllText(p.AppExe));
        Assert.Equal("setup-binary", File.ReadAllText(p.Uninstaller));
        Assert.Contains(sys.Shortcuts, s => s.Link == p.StartMenuLink && s.Target == p.AppExe);
        Assert.Contains(sys.Shortcuts, s => s.Link == p.DesktopLink);
        var e = sys.Entries[Installer.UninstallKey];
        Assert.Equal("TrafficScope", e["DisplayName"]);
        Assert.Equal(AppInfo.Version, e["DisplayVersion"]);
        Assert.Equal("Lifetrenz", e["Publisher"]);
        Assert.Equal($"\"{p.Uninstaller}\" /uninstall", e["UninstallString"]);
        Assert.Equal($"\"{p.Uninstaller}\" /uninstall /S", e["QuietUninstallString"]);
        Assert.IsType<int>(e["EstimatedSize"]);
    }

    [Fact]
    public void ReinstallUpgradesInPlaceAndCanSkipDesktop()
    {
        var (p, setup, _) = Temp();
        var sys = new FakeSystem();
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app-v1")), setup, p, sys, true);
        sys.Shortcuts.Clear();
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app-v2")), setup, p, sys, desktopShortcut: false);
        Assert.Equal("app-v2", File.ReadAllText(p.AppExe));
        Assert.DoesNotContain(sys.Shortcuts, s => s.Link == p.DesktopLink);
        Assert.False(File.Exists(p.AppExe + ".new"));
    }

    [Fact]
    public void RunningFromTheInstalledUninstallerDoesNotCopyOntoItself()
    {
        var (p, setup, _) = Temp();
        var sys = new FakeSystem();
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app")), setup, p, sys, true);
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app")), p.Uninstaller, p, sys, true);
        Assert.Equal("setup-binary", File.ReadAllText(p.Uninstaller));
    }

    [Fact]
    public void UninstallRemovesEverythingExceptTheRunningUninstaller()
    {
        var (p, setup, _) = Temp();
        var sys = new FakeSystem();
        Installer.Install(new MemoryStream(Encoding.UTF8.GetBytes("app")), setup, p, sys, true);
        File.WriteAllText(Path.Combine(p.InstallDir, "extra.log"), "x");
        Installer.Uninstall(p, sys);
        Assert.False(File.Exists(p.AppExe));
        Assert.False(File.Exists(p.StartMenuLink));
        Assert.False(File.Exists(p.DesktopLink));
        Assert.False(File.Exists(Path.Combine(p.InstallDir, "extra.log")));
        Assert.True(File.Exists(p.Uninstaller));      // removed by the delayed cmd after exit
        Assert.Empty(sys.Entries);
    }

    [Fact]
    public void SetupVersionMatchesApp() => Assert.Equal(AppInfo.Version, Installer.Version);

    [Fact]
    public void VersionIsCentral() => Assert.Equal("0.3.1", AppInfo.Version);
}

public class LeftoverProxyTests
{
    [Fact]
    public void ParsesNetworksetupOutput()
    {
        var (on, server) = SystemProxy.ParseNetworkSetup("Enabled: Yes\nServer: 127.0.0.1\nPort: 59309\nAuthenticated Proxy Enabled: 0\n");
        Assert.True(on); Assert.Equal("127.0.0.1:59309", server);
        var (off, _) = SystemProxy.ParseNetworkSetup("Enabled: No\nServer: 127.0.0.1\nPort: 59309\n");
        Assert.False(off);
        var (_, none) = SystemProxy.ParseNetworkSetup("Enabled: No\nServer: \nPort: 0\n");
        Assert.Null(none);
    }

    [Theory]
    [InlineData(true, "127.0.0.1:59309", false, true)]       // left on, nobody listening -> leftover
    [InlineData(true, "127.0.0.1:59309", true, false)]       // something is listening (TrafficScope or another tool) -> fine
    [InlineData(false, "127.0.0.1:59309", false, false)]     // proxy off
    [InlineData(true, "proxy.corp.local:8080", false, false)]// corporate proxy is never touched
    [InlineData(true, "http=127.0.0.1:9000;https=127.0.0.1:9000", false, true)]
    [InlineData(true, null, false, false)]
    public void DetectsOnlyDeadLocalProxies(bool enabled, string? server, bool listening, bool expected) =>
        Assert.Equal(expected, SystemProxy.IsDeadLocalProxy(enabled, server, _ => listening));

    [Fact]
    public void RealCheckIsReadOnlyAndDoesNotThrow() => SystemProxy.DetectLeftover();
}
