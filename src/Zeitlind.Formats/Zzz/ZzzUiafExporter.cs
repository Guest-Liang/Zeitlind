using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Uiaf;

namespace Zeitlind.Formats.Zzz;

public static class ZzzUiafExporter
{
    private const uint ProgressFieldNumber = 2;
    private const uint CompletedValueFieldNumber = 5;
    private const uint UnfinishedStatus = 1;
    private const uint FinishedStatus = 2;

    public static string Serialize(AchievementSnapshot snapshot, uint uid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfZero(uid);

        var document = new UiafDocument
        {
            Info = UiafExportContract.CreateInfo(snapshot),
            Nap = new UiafGameData
            {
                Uid = uid,
                List = snapshot
                    .Records.OrderBy(static record => record.Id)
                    .Select(static record => new UiafAchievement
                    {
                        Id = record.Id,
                        Current = ReadCurrent(record),
                        Status = record.IsCompleted ? FinishedStatus : UnfinishedStatus,
                        Timestamp = UiafExportContract.NormalizeTimestamp(record.FinishTimestamp),
                    })
                    .ToArray(),
            },
        };

        return JsonSerializer.Serialize(document, ZzzUiafJsonContext.Default.UiafDocument);
    }

    private static ulong ReadCurrent(AchievementRecord record)
    {
        if (record.RawVarints.TryGetValue(CompletedValueFieldNumber, out var completedValue))
        {
            return completedValue;
        }

        return record.RawVarints.TryGetValue(ProgressFieldNumber, out var progress) ? progress : 0;
    }
}

internal sealed class UiafDocument
{
    [JsonPropertyName("info")]
    public required UiafInfo Info { get; init; }

    [JsonPropertyName("nap")]
    public required UiafGameData Nap { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafDocument))]
internal sealed partial class ZzzUiafJsonContext : JsonSerializerContext;
