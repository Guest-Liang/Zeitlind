using System.Diagnostics;
using System.Security.Principal;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Runtime.Games;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Runtime;

internal static class ExportService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static RegistryGameStatus[] DiscoverGames() =>
        GameRegistry.All.Select(GameLocator.ReadRegistryStatus).ToArray();

    public static GameSelection ResolveInstallation(string configuredPath)
    {
        var resolved = GameLocator.Resolve(configuredPath);
        var version = resolved.Module.ValidateInstallation(resolved.ExecutablePath);
        return new GameSelection(resolved.Module, resolved.ExecutablePath, version);
    }

    public static void EnsureGameIsNotRunning(GameSelection selection)
    {
        var descriptor = selection.Module.Descriptor;
        var processes = Process.GetProcessesByName(descriptor.ProcessName);
        try
        {
            if (processes.Length != 0)
            {
                throw new InvalidOperationException(
                    $"检测到{descriptor.DisplayName}已经在运行，请先退出游戏，再运行 Zeitlind"
                );
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static async Task<AchievementCapture> CaptureAsync(
        string executablePath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = ResolveInstallation(executablePath);
        EnsureGameIsNotRunning(selection);
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("请以管理员身份运行 Zeitlind");
        }

        using var extractedHook =
            EmbeddedHook.TryExtract(selection.Module)
            ?? throw new InvalidOperationException(
                $"当前构建没有内嵌 {selection.Module.Descriptor.DisplayName} Hook DLL；请使用完整 Zeitlind"
            );
        var catalog = AchievementCatalog.LoadBundled(selection.Module.Descriptor.Kind);
        ApplicationLog.WriteInfo($"游戏：{selection.Module.Descriptor.DisplayName}");
        ApplicationLog.WriteInfo($"游戏路径：{selection.ExecutablePath}");
        ApplicationLog.WriteInfo($"游戏版本：{selection.Version}");
        ApplicationLog.WriteInfo($"成就元数据：{catalog.LatestVersion}（{catalog.Count} 项）");
        ApplicationLog.WriteInfo("正在启动游戏");

        var captured = await AchievementExportSession.CaptureAsync(
            selection,
            extractedHook.Path,
            catalog,
            progress,
            cancellationToken
        );
        return new AchievementCapture(selection.Module, captured.Snapshot, captured.Uid, catalog);
    }
}
