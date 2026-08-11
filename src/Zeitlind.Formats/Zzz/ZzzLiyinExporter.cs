using Zeitlind.Core.Achievements;
using Zeitlind.Formats.Liyin;

namespace Zeitlind.Formats.Zzz;

public static class ZzzLiyinExporter
{
    public static string Serialize(AchievementSnapshot snapshot, uint uid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return LiyinExporter.Serialize(snapshot, uid, snapshot.Records.Where(static record => record.IsCompleted));
    }
}
