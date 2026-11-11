namespace TrafficScope.Core;

public static class AppInfo
{
    public static string Version { get; } = (typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
}
