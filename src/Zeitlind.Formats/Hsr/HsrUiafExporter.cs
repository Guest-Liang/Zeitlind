using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Uiaf;

namespace Zeitlind.Formats.Hsr;

public static class HsrUiafExporter
{
    private const uint MinimumObservedStatus = 1;
    private const uint MaximumObservedStatus = 3;

    public static string Serialize(AchievementSnapshot snapshot, uint uid, IReadOnlySet<uint> knownAchievementIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(knownAchievementIds);
        ArgumentOutOfRangeException.ThrowIfZero(uid);

        var document = new UiafDocument
        {
            Info = UiafExportContract.CreateInfo(snapshot),
            Hkrpg = new UiafGameData
            {
                Uid = uid,
                List = snapshot
                    .Records.Where(record => knownAchievementIds.Contains(record.Id) && IsObservedStatus(record.Status))
                    .OrderBy(static record => record.Id)
                    .Select(static record => new UiafAchievement
                    {
                        Id = record.Id,
                        Current = record.Progress ?? 0,
                        Status = MapStatus(record),
                        Timestamp = UiafExportContract.NormalizeTimestamp(record.FinishTimestamp),
                    })
                    .ToArray(),
            },
        };

        return JsonSerializer.Serialize(document, HsrUiafJsonContext.Default.UiafDocument);
    }

    private static uint MapStatus(AchievementRecord record)
    {
        // 对元数据中已知成就进行前后对比捕获后确认：
        // 1 = 未完成，2 = 已完成但未领取奖励，3 = 已领取奖励。
        // 这些值可直接映射到拟议的 hkrpg UIAF 状态范围。
        return IsObservedStatus(record.Status)
            ? record.Status!.Value
            : throw new InvalidOperationException("UIAF hkrpg 只接受本次样本实际观察到的状态值 1、2、3");
    }

    private static bool IsObservedStatus(uint? status)
    {
        return status is >= MinimumObservedStatus and <= MaximumObservedStatus;
    }
}

internal sealed class UiafDocument
{
    [JsonPropertyName("info")]
    public required UiafInfo Info { get; init; }

    [JsonPropertyName("hkrpg")]
    public required UiafGameData Hkrpg { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafDocument))]
internal sealed partial class HsrUiafJsonContext : JsonSerializerContext;
