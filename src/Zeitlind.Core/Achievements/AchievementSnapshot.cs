using Zeitlind.Core.Games;

namespace Zeitlind.Core.Achievements;

public sealed record AchievementSnapshot
{
    public required GameKind Game { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public required string GameVersion { get; init; }

    public required uint SourceCommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public uint? StatusFieldNumber { get; init; }

    public uint? FinishTimestampFieldNumber { get; init; }

    public uint? CompletedFlagFieldNumber { get; init; }

    public uint? ProgressFieldNumber { get; init; }

    public required IReadOnlyList<uint> PackedVarintFieldNumbers { get; init; }

    public required int CatalogMatchCount { get; init; }

    public required int UnknownIdCount { get; init; }

    public required IReadOnlyList<AchievementRecord> Records { get; init; }
}
