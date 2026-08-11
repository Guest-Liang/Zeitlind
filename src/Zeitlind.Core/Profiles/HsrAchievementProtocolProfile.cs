namespace Zeitlind.Core.Profiles;

public sealed record HsrAchievementProtocolProfile
{
    public required uint FullSnapshotCommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint StatusFieldNumber { get; init; }

    public required uint FinishTimestampFieldNumber { get; init; }

    public required uint ProgressFieldNumber { get; init; }

    public required IReadOnlyList<uint> PackedVarintFieldNumbers { get; init; }
}
