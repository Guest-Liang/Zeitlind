namespace Zeitlind.Protocol.Achievements;

/// <summary>三款游戏共用的成就快照候选诊断信息。</summary>
public sealed record AchievementCandidateDiagnostic
{
    public required uint CommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint? StatusFieldNumber { get; init; }

    public required uint? FinishTimestampFieldNumber { get; init; }

    public required uint? ProgressFieldNumber { get; init; }

    public uint? TotalProgressFieldNumber { get; init; }

    public uint? CompletedFlagFieldNumber { get; init; }

    public required int RecordCount { get; init; }

    public required int CatalogMatchCount { get; init; }

    public required int UnknownIdCount { get; init; }

    public required int CompletionEvidenceCount { get; init; }

    public required bool IsAccepted { get; init; }

    public required string Decision { get; init; }
}
