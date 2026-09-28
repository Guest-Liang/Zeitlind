using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Uiaf;

namespace Zeitlind.Formats.Genshin;

/// <summary>原神正式 UIAF v1.1 导出。</summary>
public static class GenshinUiafExporter
{
    public static string Serialize(AchievementSnapshot snapshot, string exportAppVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportAppVersion);

        var document = new UiafV11Document
        {
            Info = new UiafV11Info
            {
                ExportApp = "Zeitlind",
                ExportAppVersion = exportAppVersion,
                ExportTimestamp = snapshot.CapturedAt.ToUnixTimeSeconds(),
                UiafVersion = "v1.1",
            },
            List = snapshot
                .Records.Where(GenshinUiafProjection.ShouldExport)
                .OrderBy(static record => record.Id)
                .Select(ToEntry)
                .ToArray(),
        };

        return JsonSerializer.Serialize(document, GenshinUiafJsonContext.Default.UiafV11Document);
    }

    private static UiafV11Achievement ToEntry(AchievementRecord record)
    {
        return new UiafV11Achievement
        {
            Id = record.Id,
            Current = (uint)Math.Min(record.Progress ?? 0, uint.MaxValue),
            Status = GenshinUiafProjection.MapStatus(record),
            Timestamp = UiafExportContract.NormalizeTimestamp(record.FinishTimestamp),
        };
    }
}

internal sealed class UiafV11Document
{
    [JsonPropertyName("info")]
    public required UiafV11Info Info { get; init; }

    [JsonPropertyName("list")]
    public required UiafV11Achievement[] List { get; init; }
}

internal sealed class UiafV11Info
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

internal sealed class UiafV11Achievement
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
[JsonSerializable(typeof(UiafV11Document))]
internal sealed partial class GenshinUiafJsonContext : JsonSerializerContext;
