namespace Zeitlind.Protocol.Achievements;

internal static class AchievementTimestampEvidence
{
    private const long EarliestPlausibleUnixSeconds = 1_262_304_000;

    public static bool IsPlausible(long value, DateTimeOffset capturedAt)
    {
        var latestSeconds = capturedAt.AddYears(5).ToUnixTimeSeconds();

        return value >= EarliestPlausibleUnixSeconds && value <= latestSeconds
            || value >= EarliestPlausibleUnixSeconds * 1_000 && value <= latestSeconds * 1_000
            || value >= EarliestPlausibleUnixSeconds * 1_000_000 && value <= latestSeconds * 1_000_000;
    }
}
