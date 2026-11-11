using System.Runtime.InteropServices;
using System.Text;

namespace TrafficScope.Core;

/// <summary>macOS libproc bindings: reliable full executable paths for any pid.</summary>
static class LibProc
{
    [DllImport("libproc.dylib")] static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    [DllImport("libproc.dylib")] static extern int proc_listallpids(int[]? buffer, int size);

    public static string? PathOf(int pid)
    {
        var buf = new byte[4096];
        var n = proc_pidpath(pid, buf, (uint)buf.Length);
        return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
    }

    public static IEnumerable<(int Pid, string Path)> All()
    {
        var count = proc_listallpids(null, 0);
        if (count <= 0) yield break;
        var pids = new int[count + 64];
        count = proc_listallpids(pids, pids.Length * sizeof(int));
        for (var i = 0; i < count; i++)
        {
            var p = PathOf(pids[i]);
            if (p != null) yield return (pids[i], p);
        }
    }
}
