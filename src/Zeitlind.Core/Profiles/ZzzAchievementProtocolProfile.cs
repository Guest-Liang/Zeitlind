namespace Zeitlind.Core.Profiles;

public sealed record ZzzAchievementProtocolProfile
{
    public required uint FullSnapshotCommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint FinishTimestampFieldNumber { get; init; }

    public required uint CompletedFlagFieldNumber { get; init; }
}
