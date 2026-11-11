using System.Reflection;

namespace TrafficScope.Setup;

public interface ISystem
{
    void CreateShortcut(string linkPath, string target, string workingDir, string description);
    void WriteUninstallEntry(string keyName, IDictionary<string, object> values);
    void DeleteUninstallEntry(string keyName);
    bool IsAppRunning();
}

public sealed record InstallPaths(string InstallDir, string StartMenuDir, string DesktopDir)
{
    public string AppExe => Path.Combine(InstallDir, Installer.AppExeName);
    public string Uninstaller => Path.Combine(InstallDir, Installer.UninstallerName);
    public string StartMenuLink => Path.Combine(StartMenuDir, "TrafficScope.lnk");
    public string DesktopLink => Path.Combine(DesktopDir, "TrafficScope.lnk");

    public static InstallPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TrafficScope"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
}

/// <summary>Per-user install: no admin rights, no files outside the user's profile.</summary>
public static class Installer
{
    public const string AppExeName = "TrafficScope.exe";
    public const string UninstallerName = "Uninstall TrafficScope.exe";
    public const string UninstallKey = "TrafficScope";
    public const string Publisher = "Lifetrenz";

    public static string Version => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public static Stream? OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe");

    public static void Install(Stream payload, string setupExePath, InstallPaths p, ISystem sys, bool desktopShortcut)
    {
        Directory.CreateDirectory(p.InstallDir);
        var tmp = p.AppExe + ".new";
        using (var fs = File.Create(tmp)) payload.CopyTo(fs);
        File.Move(tmp, p.AppExe, overwrite: true);
        if (!string.Equals(Path.GetFullPath(setupExePath), Path.GetFullPath(p.Uninstaller), StringComparison.OrdinalIgnoreCase))
            File.Copy(setupExePath, p.Uninstaller, overwrite: true);

        Directory.CreateDirectory(p.StartMenuDir);
        sys.CreateShortcut(p.StartMenuLink, p.AppExe, p.InstallDir, "Inspect HTTP(S) traffic per app");
        if (desktopShortcut) sys.CreateShortcut(p.DesktopLink, p.AppExe, p.InstallDir, "Inspect HTTP(S) traffic per app");

        sys.WriteUninstallEntry(UninstallKey, UninstallValues(p, new FileInfo(p.AppExe).Length));
    }

    public static Dictionary<string, object> UninstallValues(InstallPaths p, long appBytes) => new()
    {
        ["DisplayName"] = "TrafficScope",
        ["DisplayVersion"] = Version,
        ["Publisher"] = Publisher,
        ["DisplayIcon"] = p.AppExe,
        ["InstallLocation"] = p.InstallDir,
        ["UninstallString"] = $"\"{p.Uninstaller}\" /uninstall",
        ["QuietUninstallString"] = $"\"{p.Uninstaller}\" /uninstall /S",
        ["EstimatedSize"] = (int)Math.Max(1, appBytes / 1024),
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
    };

    /// <summary>Removes shortcuts, the uninstall entry and the app. The running uninstaller deletes itself afterwards.</summary>
    public static void Uninstall(InstallPaths p, ISystem sys)
    {
        foreach (var link in new[] { p.StartMenuLink, p.DesktopLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        sys.DeleteUninstallEntry(UninstallKey);
        try { if (File.Exists(p.AppExe)) File.Delete(p.AppExe); } catch { }
        foreach (var f in Directory.Exists(p.InstallDir) ? Directory.GetFiles(p.InstallDir) : Array.Empty<string>())
            if (!string.Equals(Path.GetFileName(f), UninstallerName, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch { }
    }
}
