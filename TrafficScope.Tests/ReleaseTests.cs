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
    public void VersionIsCentral() => Assert.Equal("0.4.1", AppInfo.Version);
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

public class KeyProtectionTests
{
    static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "ts-key-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void OldKeyFileIsReEncryptedWithARandomPasswordAndKeepsTheSameCertificate()
    {
        var dir = NewDir();
        string thumb;
        using (var e = new ProxyEngine(dir)) { e.EnsureRootCertificate(); thumb = e.RootCertificate!.Thumbprint; }   // test folders use the old fixed password
        var pfx = CertificateService.KeyPath(dir);
        Assert.True(File.Exists(pfx));

        string? stored = null;
        var password = CertificateService.ProtectKey(dir, () => stored, v => { stored = v; return true; });

        Assert.NotEqual(CertificateService.LegacyPassword, password);
        Assert.Equal(stored, password);
        Assert.True(password.Length >= 32);
        Assert.ThrowsAny<Exception>(() => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfx, CertificateService.LegacyPassword));
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfx, password);
        Assert.Equal(thumb, cert.Thumbprint);
        Assert.True(cert.HasPrivateKey);
        // a second start reuses the stored password and changes nothing
        var before = File.ReadAllBytes(pfx);
        Assert.Equal(password, CertificateService.ProtectKey(dir, () => stored, _ => throw new Exception("must not rewrite")));
        Assert.Equal(before, File.ReadAllBytes(pfx));
    }

    [Fact]
    public void KeyStaysUsableWhenThePasswordCannotBeStored()
    {
        var dir = NewDir();
        using (var e = new ProxyEngine(dir)) e.EnsureRootCertificate();
        var pfx = CertificateService.KeyPath(dir);
        var before = File.ReadAllBytes(pfx);
        Assert.Equal(CertificateService.LegacyPassword, CertificateService.ProtectKey(dir, () => null, _ => false));
        Assert.Equal(before, File.ReadAllBytes(pfx));
        Assert.False(File.Exists(pfx + ".new"));
    }

    [Fact]
    public void UnreadableKeyIsCopiedAsideBeforeItCanBeReplaced()
    {
        var dir = NewDir();
        var pfx = CertificateService.KeyPath(dir);
        File.WriteAllBytes(pfx, new byte[] { 1, 2, 3, 4 });             // opens with no password we know
        CertificateService.ProtectKey(dir, () => null, _ => true);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(dir, "rootCert.unreadable.pfx")));
    }

    [Fact]
    public void KeyFileAndFolderAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        using (var e = new ProxyEngine(dir)) e.EnsureRootCertificate();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CertificateService.KeyPath(dir)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void TestFoldersNeverUseTheKeychain() =>
        Assert.Equal(CertificateService.LegacyPassword, CertificateService.ProtectKey(NewDir()));

    [Theory]
    [InlineData(9L * 1024 * 1024, "application/json", true)]      // over the limit
    [InlineData(1024L, "application/octet-stream", false)]         // small binary is fine
    [InlineData(-1L, "application/json", false)]                   // unknown size, text: capture
    [InlineData(-1L, "video/mp4", true)]                           // unknown size, binary stream: pass through
    [InlineData(-1L, "application/octet-stream", true)]
    public void LargeOrStreamingBodiesAreNotBuffered(long length, string type, bool skipped) =>
        Assert.Equal(skipped, ProxyEngine.SkipReason(length, type) != null);
}
