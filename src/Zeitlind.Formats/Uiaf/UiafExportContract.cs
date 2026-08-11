using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;

namespace Zeitlind.Formats.Uiaf;

internal static class UiafExportContract
{
    private const long UnknownCompletionTimestamp = 253_402_271_999;

    public static UiafInfo CreateInfo(AchievementSnapshot snapshot)
    {
        return new UiafInfo
        {
            ExportApp = "Zeitlind",
            UiafVersion = "v1.2",
            ExportTimestamp = snapshot.CapturedAt.ToUnixTimeSeconds(),
        };
    }

    public static long NormalizeTimestamp(long? rawTimestamp)
    {
        return AchievementTimestamp.Normalize(rawTimestamp)?.ToUnixTimeSeconds() ?? UnknownCompletionTimestamp;
    }
}

internal sealed class UiafInfo
{
    [JsonPropertyName("export_timestamp")]
    public required long ExportTimestamp { get; init; }

    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("uiaf_version")]
    public required string UiafVersion { get; init; }
}

internal sealed class UiafGameData
{
    [JsonPropertyName("uid")]
    public required uint Uid { get; init; }

    [JsonPropertyName("list")]
    public required UiafAchievement[] List { get; init; }
}

internal sealed class UiafAchievement
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("current")]
    public required ulong Current { get; init; }

    [JsonPropertyName("status")]
    public required uint Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}
