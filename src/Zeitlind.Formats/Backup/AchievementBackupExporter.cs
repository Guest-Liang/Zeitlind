using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeitlind.Core.Achievements;

namespace Zeitlind.Formats.Backup;

public static class AchievementBackupExporter
{
    public static string Serialize(AchievementSnapshot snapshot, uint uid, string metadataVersion, int metadataCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfZero(uid);

        var document = new AchievementBackupDocument
        {
            Schema = "Zeitlind.AchievementBackup",
            SchemaVersion = 1,
            ExportApp = "Zeitlind",
            Game = snapshot.Game.ToString().ToLowerInvariant(),
            Uid = uid,
            CapturedAt = snapshot.CapturedAt.ToOffset(AchievementTimestamp.ChinaStandardOffset),
            GameVersion = snapshot.GameVersion,
            MetadataVersion = metadataVersion,
            MetadataCount = metadataCount,
            Detection = new DetectionInfo
            {
                CommandId = snapshot.SourceCommandId,
                RecordFieldPath = snapshot.RecordFieldPath,
                IdFieldNumber = snapshot.IdFieldNumber,
                StatusFieldNumber = snapshot.StatusFieldNumber,
                FinishTimestampFieldNumber = snapshot.FinishTimestampFieldNumber,
                CompletedFlagFieldNumber = snapshot.CompletedFlagFieldNumber,
                ProgressFieldNumber = snapshot.ProgressFieldNumber,
                PackedVarintFieldNumbers = snapshot.PackedVarintFieldNumbers.ToArray(),
                CatalogMatchCount = snapshot.CatalogMatchCount,
                UnknownIdCount = snapshot.UnknownIdCount,
            },
            Records = snapshot.Records.Select(ToDocumentRecord).ToArray(),
        };

        return JsonSerializer.Serialize(document, AchievementBackupJsonContext.Default.AchievementBackupDocument);
    }

    private static AchievementBackupRecord ToDocumentRecord(AchievementRecord record)
    {
        return new AchievementBackupRecord
        {
            Id = record.Id,
            IsCompleted = record.IsCompleted,
            Status = record.Status,
            Progress = record.Progress,
            FinishTimestamp = record.FinishTimestamp,
            FinishTimeUtc8 = AchievementTimestamp
                .Normalize(record.FinishTimestamp)
                ?.ToOffset(AchievementTimestamp.ChinaStandardOffset),
            CompletedFlag = record.CompletedFlag,
            RawVarints = record.RawVarints.ToDictionary(
                static pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                static pair => pair.Value
            ),
            RawPackedVarints =
                record.RawPackedVarints.Count == 0
                    ? null
                    : record.RawPackedVarints.ToDictionary(
                        static pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                        static pair => pair.Value.ToArray()
                    ),
        };
    }
}

internal sealed class AchievementBackupDocument
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("game")]
    public required string Game { get; init; }

    [JsonPropertyName("uid")]
    public required uint Uid { get; init; }

    [JsonPropertyName("captured_at")]
    public required DateTimeOffset CapturedAt { get; init; }

    [JsonPropertyName("game_version")]
    public required string GameVersion { get; init; }

    [JsonPropertyName("metadata_version")]
    public required string MetadataVersion { get; init; }

    [JsonPropertyName("metadata_count")]
    public required int MetadataCount { get; init; }

    [JsonPropertyName("detection")]
    public required DetectionInfo Detection { get; init; }

    [JsonPropertyName("records")]
    public required AchievementBackupRecord[] Records { get; init; }
}

internal sealed class DetectionInfo
{
    [JsonPropertyName("command_id")]
    public required uint CommandId { get; init; }

    [JsonPropertyName("record_field_path")]
    public required string RecordFieldPath { get; init; }

    [JsonPropertyName("id_field_number")]
    public required uint IdFieldNumber { get; init; }

    [JsonPropertyName("status_field_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? StatusFieldNumber { get; init; }

    [JsonPropertyName("finish_timestamp_field_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? FinishTimestampFieldNumber { get; init; }

    [JsonPropertyName("completed_flag_field_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? CompletedFlagFieldNumber { get; init; }

    [JsonPropertyName("progress_field_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ProgressFieldNumber { get; init; }

    [JsonPropertyName("packed_varint_field_numbers")]
    public required uint[] PackedVarintFieldNumbers { get; init; }

    [JsonPropertyName("catalog_match_count")]
    public required int CatalogMatchCount { get; init; }

    [JsonPropertyName("unknown_id_count")]
    public required int UnknownIdCount { get; init; }
}

internal sealed class AchievementBackupRecord
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("is_completed")]
    public required bool IsCompleted { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Status { get; init; }

    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Progress { get; init; }

    [JsonPropertyName("finish_timestamp")]
    public long? FinishTimestamp { get; init; }

    [JsonPropertyName("finish_time_utc8")]
    public DateTimeOffset? FinishTimeUtc8 { get; init; }

    [JsonPropertyName("completed_flag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CompletedFlag { get; init; }

    [JsonPropertyName("raw_varints")]
    public required Dictionary<string, ulong> RawVarints { get; init; }

    [JsonPropertyName("raw_packed_varints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ulong[]>? RawPackedVarints { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AchievementBackupDocument))]
internal sealed partial class AchievementBackupJsonContext : JsonSerializerContext;
