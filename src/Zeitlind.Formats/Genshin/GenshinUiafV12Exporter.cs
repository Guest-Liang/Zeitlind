using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Uiaf;

namespace Zeitlind.Formats.Genshin;

public static class GenshinUiafV12Exporter
{
    public static string Serialize(AchievementSnapshot snapshot, ulong? uid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var document = new UiafV12Document
        {
            Info = UiafExportContract.CreateInfo(snapshot),
            Hk4e = new UiafGameData
            {
                Uid = uid,
                List = snapshot
                    .Records.Where(GenshinUiafExporter.ShouldExport)
                    .OrderBy(static record => record.Id)
                    .Select(static record => new Zeitlind.Formats.Uiaf.UiafAchievement
                    {
                        Id = record.Id,
                        Current = record.Progress ?? 0,
                        Status = GenshinUiafExporter.MapStatus(record),
                        Timestamp = UiafExportContract.NormalizeTimestamp(record.FinishTimestamp),
                    })
                    .ToArray(),
            },
        };

        return JsonSerializer.Serialize(document, GenshinUiafV12JsonContext.Default.UiafV12Document);
    }
}

internal sealed class UiafV12Document
{
    [JsonPropertyName("info")]
    public required Zeitlind.Formats.Uiaf.UiafInfo Info { get; init; }

    [JsonPropertyName("hk4e")]
    public required UiafGameData Hk4e { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafV12Document))]
internal sealed partial class GenshinUiafV12JsonContext : JsonSerializerContext;
