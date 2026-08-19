using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Uiaf;

namespace Zeitlind.Formats.Genshin;

public static class GenshinUiafExporter
{
    private const uint Unfinished = 1;
    private const uint Finished = 2;
    private const uint RewardTaken = 3;

    public static string Serialize(AchievementSnapshot snapshot, string exportAppVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportAppVersion);

        var document = new UiafDocument
        {
            Info = new UiafInfo
            {
                ExportApp = "Zeitlind",
                ExportAppVersion = exportAppVersion,
                ExportTimestamp = snapshot.CapturedAt.ToUnixTimeSeconds(),
                UiafVersion = "v1.1",
            },
            List = snapshot.Records.Where(ShouldExport).OrderBy(static record => record.Id).Select(ToEntry).ToArray(),
        };

        return JsonSerializer.Serialize(document, GenshinUiafJsonContext.Default.UiafDocument);
    }

    internal static bool ShouldExport(AchievementRecord record)
    {
        var status = MapStatus(record);
        var current = record.Progress ?? 0;
        return status >= Finished || current > 0;
    }

    private static UiafAchievement ToEntry(AchievementRecord record)
    {
        return new UiafAchievement
        {
            Id = record.Id,
            Current = (uint)Math.Min(record.Progress ?? 0, uint.MaxValue),
            Status = MapStatus(record),
            Timestamp = UiafExportContract.NormalizeTimestamp(record.FinishTimestamp),
        };
    }

    internal static uint MapStatus(AchievementRecord record)
    {
        if (record.Status is >= Unfinished and <= RewardTaken)
        {
            return record.Status.Value;
        }

        return record.IsCompleted || record.FinishTimestamp is > 0 ? Finished : Unfinished;
    }
}

internal sealed class UiafDocument
{
    [JsonPropertyName("info")]
    public required UiafInfo Info { get; init; }

    [JsonPropertyName("list")]
    public required UiafAchievement[] List { get; init; }
}

internal sealed class UiafInfo
{
    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("export_app_version")]
    public required string ExportAppVersion { get; init; }

    [JsonPropertyName("export_timestamp")]
    public required long ExportTimestamp { get; init; }

    [JsonPropertyName("uiaf_version")]
    public required string UiafVersion { get; init; }
}

internal sealed class UiafAchievement
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("current")]
    public required uint Current { get; init; }

    [JsonPropertyName("status")]
    public required uint Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafDocument))]
internal sealed partial class GenshinUiafJsonContext : JsonSerializerContext;
