namespace Zeitlind.Core.Achievements;

public sealed record AchievementRecord
{
    public required uint Id { get; init; }

    public required bool IsCompleted { get; init; }

    public uint? Status { get; init; }

    public ulong? Progress { get; init; }

    public long? FinishTimestamp { get; init; }

    public bool? CompletedFlag { get; init; }

    public required IReadOnlyDictionary<uint, ulong> RawVarints { get; init; }

    public required IReadOnlyDictionary<uint, ulong[]> RawPackedVarints { get; init; }
}
