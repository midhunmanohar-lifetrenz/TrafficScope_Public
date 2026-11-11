using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace TrafficScope.Setup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        var uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        var noDesktop = args.Any(a => a.Equals("/nodesktop", StringComparison.OrdinalIgnoreCase));
        var sys = new WindowsSystem();
        var paths = InstallPaths.ForCurrentUser();
        try { return uninstall ? RunUninstall(paths, sys, silent) : RunInstall(paths, sys, silent, !noDesktop); }
        catch (Exception ex)
        {
            if (!silent) Dialog.Show("Something went wrong:\n\n" + ex.Message, Dialog.Ok | Dialog.Error);
            return 1;
        }
    }

    static bool WaitForAppToClose(ISystem sys, bool silent)
    {
        while (sys.IsAppRunning())
        {
            if (silent) { foreach (var p in Process.GetProcessesByName("TrafficScope")) try { p.CloseMainWindow(); p.WaitForExit(5000); } catch { } return !sys.IsAppRunning(); }
            if (Dialog.Show("TrafficScope is running. Close it (press Stop first if it is recording), then press Retry.", Dialog.RetryCancel | Dialog.Warning) != Dialog.IdRetry)
                return false;
        }
        return true;
    }

    static int RunInstall(InstallPaths paths, ISystem sys, bool silent, bool desktop)
    {
        using var payload = Installer.OpenPayload();
        if (payload == null) { if (!silent) Dialog.Show("This setup file is incomplete (no application inside). Download it again.", Dialog.Ok | Dialog.Error); return 2; }
        if (!silent && Dialog.Show(
                $"Install TrafficScope {Installer.Version} for {Environment.UserName}?\n\n" +
                $"It is installed in your user profile (no administrator rights needed):\n{paths.InstallDir}\n\n" +
                "Start menu and Desktop shortcuts will be added.", Dialog.YesNo | Dialog.Question) != Dialog.IdYes)
            return 3;
        if (!WaitForAppToClose(sys, silent)) return 4;
        Installer.Install(payload, Environment.ProcessPath!, paths, sys, desktop);
        if (!silent && Dialog.Show("TrafficScope is installed.\n\nOpen it now?", Dialog.YesNo | Dialog.Info) == Dialog.IdYes)
            Process.Start(new ProcessStartInfo(paths.AppExe) { UseShellExecute = true, WorkingDirectory = paths.InstallDir });
        return 0;
    }

    static int RunUninstall(InstallPaths paths, ISystem sys, bool silent)
    {
        if (!silent && Dialog.Show("Remove TrafficScope from this PC?", Dialog.YesNo | Dialog.Question) != Dialog.IdYes) return 3;
        if (!WaitForAppToClose(sys, silent)) return 4;
        RestoreProxyIfLeftOn();
        Installer.Uninstall(paths, sys);
        if (!silent && Dialog.Show("Also remove TrafficScope's certificate and your settings (favourites, rules, Postman connection)?",
                Dialog.YesNo | Dialog.Question) == Dialog.IdYes)
            RemoveUserData();
        if (!silent) Dialog.Show("TrafficScope has been removed.", Dialog.Ok | Dialog.Info);
        // The uninstaller cannot delete itself while running: let cmd remove the folder once we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{paths.InstallDir}\"")
        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        return 0;
    }

    /// <summary>If the proxy still points at 127.0.0.1 (TrafficScope), switch it off so the PC keeps working.</summary>
    static void RestoreProxyIfLeftOn()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (k == null) return;
            var server = k.GetValue("ProxyServer") as string ?? "";
            if (Convert.ToInt32(k.GetValue("ProxyEnable") ?? 0) == 1 && server.StartsWith("127.0.0.1:"))
            {
                k.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
                k.DeleteValue("ProxyServer", false);
            }
        }
        catch { }
    }

    static void RemoveUserData()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindBySubjectName, "TrafficScope Root CA", false)) store.Remove(c);
        }
        catch { }
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficScope");
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        catch { }
    }
}
