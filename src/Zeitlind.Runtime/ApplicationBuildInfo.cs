namespace Zeitlind.Runtime;

internal static class ApplicationBuildInfo
{
    public const string Version = "3.3.0";
    public const string Copyright = "Copyright © 2026 GuestLiang";
    public const string RepositoryUrl = "https://github.com/Guest-Liang/Zeitlind";

#if DEBUG
    public const string Configuration = "Debug";
    public const bool IsDebugBuild = true;
#else
    public const string Configuration = "Release";
    public const bool IsDebugBuild = false;
#endif
}
