using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TrafficScope.Setup;

public sealed class WindowsSystem : ISystem
{
    const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool IsAppRunning() => Process.GetProcessesByName("TrafficScope").Length > 0;

    public void CreateShortcut(string linkPath, string target, string workingDir, string description)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(linkPath, false);
        }
        catch
        {
            // Fallback: Windows Script Host via PowerShell.
            var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{linkPath.Replace("'", "''")}');$s.TargetPath='{target.Replace("'", "''")}';$s.WorkingDirectory='{workingDir.Replace("'", "''")}';$s.Save()";
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{ps}\"") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(15000);
        }
    }

    public void WriteUninstallEntry(string keyName, IDictionary<string, object> values)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallRoot + "\\" + keyName);
        foreach (var (name, value) in values)
            if (value is int i) k.SetValue(name, i, Microsoft.Win32.RegistryValueKind.DWord);
            else k.SetValue(name, value.ToString()!);
    }

    public void DeleteUninstallEntry(string keyName)
    {
        if (!OperatingSystem.IsWindows()) return;
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallRoot + "\\" + keyName, false);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

static class Dialog
{
    public const uint Ok = 0x0, YesNo = 0x4, RetryCancel = 0x5, Info = 0x40, Question = 0x20, Warning = 0x30, Error = 0x10;
    public const int IdYes = 6, IdRetry = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static int Show(string text, uint type) => MessageBoxW(IntPtr.Zero, text, "TrafficScope Setup", type | 0x00010000 /* foreground */);
}
