using Zeitlind.Core.Achievements;

namespace Zeitlind.Formats.Genshin;

/// <summary>原神 UIAF v1.1 和 v1.2 共用的记录筛选与状态映射。</summary>
internal static class GenshinUiafProjection
{
    private const uint Unfinished = 1;
    private const uint Finished = 2;
    private const uint RewardTaken = 3;

    internal static bool ShouldExport(AchievementRecord record)
    {
        var status = MapStatus(record);
        var current = record.Progress ?? 0;
        return status >= Finished || current > 0;
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
