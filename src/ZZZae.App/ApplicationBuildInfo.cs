namespace ZZZae.App;

internal static class ApplicationBuildInfo
{
    public const string Version = "1.8.2";

#if DEBUG
    public const string Configuration = "Debug";
    public const bool IsDebugBuild = true;
#else
    public const string Configuration = "Release";
    public const bool IsDebugBuild = false;
#endif
}
