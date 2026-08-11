using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App;

internal static class AchievementExportWriter
{
    public static async Task<ExportResult> WriteAsync(
        IGameModule module,
        AchievementSnapshot snapshot,
        uint uid,
        AchievementCatalog catalog,
        ExportTarget target,
        string? configuredOutputDirectory,
        CancellationToken cancellationToken
    )
    {
        var stamp = snapshot.CapturedAt.ToOffset(AchievementTimestamp.ChinaStandardOffset).ToString("yyyyMMdd-HHmmss");
        var directory = Path.GetFullPath(configuredOutputDirectory ?? Environment.CurrentDirectory);
        Directory.CreateDirectory(directory);

        var format = target switch
        {
            ExportTarget.AchievementBackup => "achievements",
            ExportTarget.Liyin => "liyin",
            ExportTarget.UiafExperimental => "uiaf",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
        var fileName = $"Zeitlind-{module.Descriptor.Id}-{format}-{stamp}.json";
        var displayName = target switch
        {
            ExportTarget.AchievementBackup => "Zeitlind 成就数据备份",
            ExportTarget.Liyin => "Zeitlind Liyin JSON",
            ExportTarget.UiafExperimental => "Zeitlind 实验性 UIAF（非官方）",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
        var content = module.Serialize(target, snapshot, uid, catalog);
        var outputPath = UniquePath(directory, fileName);
        await AtomicFile.WriteAllTextAsync(outputPath, content, cancellationToken);
        return new ExportResult(displayName, outputPath);
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为导出结果选择未占用的文件名");
    }
}

internal sealed record ExportResult(string DisplayName, string Path);
