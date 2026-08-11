using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Liyin;

namespace Zeitlind.Formats.Hsr;

public static class HsrLiyinExporter
{
    public static string Serialize(AchievementSnapshot snapshot, uint uid, IReadOnlySet<uint> knownAchievementIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(knownAchievementIds);

        return LiyinExporter.Serialize(
            snapshot,
            uid,
            snapshot.Records.Where(record => knownAchievementIds.Contains(record.Id) && record.IsCompleted)
        );
    }
}
