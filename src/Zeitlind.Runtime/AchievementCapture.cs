using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Runtime.Games;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Runtime;

internal sealed class AchievementCapture(
    IGameModule module,
    AchievementSnapshot snapshot,
    ulong? uid,
    AchievementCatalog catalog
)
{
    public string GameName => module.Descriptor.DisplayName;
    public ulong? Uid => uid;
    public int RecordCount => snapshot.Records.Count;
    public int CompletedCount => snapshot.Records.Count(static record => record.IsCompleted);
    public ExportFormatOption[] Formats => ExportFormats.For(module);

    public async Task<ExportResult> SaveAsync(
        ExportTarget target,
        string? outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExportFormats.Validate(module, target);
        var result = await AchievementExportWriter.WriteAsync(
            module,
            snapshot,
            uid,
            catalog,
            target,
            outputDirectory,
            cancellationToken
        );
        ApplicationLog.WriteInfo(module.BuildExportSummary(target, snapshot, catalog));
        ApplicationLog.WriteInfo($"{result.DisplayName}：{result.Path}");
        return result;
    }
}
