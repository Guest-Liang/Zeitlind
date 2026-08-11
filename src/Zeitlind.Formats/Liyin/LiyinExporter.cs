using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;

namespace Zeitlind.Formats.Liyin;

internal static class LiyinExporter
{
    public static string Serialize(
        AchievementSnapshot snapshot,
        uint uid,
        IEnumerable<AchievementRecord> completedRecords
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(completedRecords);
        ArgumentOutOfRangeException.ThrowIfZero(uid);

        var document = new LiyinDocument
        {
            Info = new LiyinInfo
            {
                ExportApp = "Zeitlind",
                ExportTimestamp = snapshot.CapturedAt.ToUnixTimeMilliseconds(),
                Uid = uid,
            },
            List = completedRecords.ToDictionary(
                static record => record.Id.ToString(),
                static record => new LiyinAchievement { Id = record.Id, Status = 3 }
            ),
        };

        return JsonSerializer.Serialize(document, LiyinJsonContext.Default.LiyinDocument);
    }
}

internal sealed class LiyinDocument
{
    [JsonPropertyName("info")]
    public required LiyinInfo Info { get; init; }

    [JsonPropertyName("list")]
    public required Dictionary<string, LiyinAchievement> List { get; init; }
}

internal sealed class LiyinInfo
{
    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("export_timestamp")]
    public required long ExportTimestamp { get; init; }

    [JsonPropertyName("uid")]
    public required uint Uid { get; init; }
}

internal sealed class LiyinAchievement
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("status")]
    public required uint Status { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LiyinDocument))]
internal sealed partial class LiyinJsonContext : JsonSerializerContext;
