using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Achievements;

public sealed record AchievementCandidateDiagnostic
{
    public required uint CommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint? StatusFieldNumber { get; init; }

    public required uint? FinishTimestampFieldNumber { get; init; }

    public required uint? ProgressFieldNumber { get; init; }

    public uint? CompletedFlagFieldNumber { get; init; }

    public required int RecordCount { get; init; }

    public required int CatalogMatchCount { get; init; }

    public required int UnknownIdCount { get; init; }

    public required int CompletionEvidenceCount { get; init; }

    public required bool IsAccepted { get; init; }

    public required string Decision { get; init; }
}

public sealed class HsrAchievementSnapshotDecoder
{
    private const int MinimumVerifiedRecordCount = 3;
    private const int MinimumDiscoveredRecordCount = 20;
    private const int MaximumTraversalDepth = 5;
    private const int MaximumFieldsPerRecord = 64;

    private readonly AchievementCatalog _catalog;
    private readonly string _gameVersion;
    private readonly HsrAchievementProtocolProfile _profile;

    public HsrAchievementSnapshotDecoder(
        AchievementCatalog catalog,
        string gameVersion,
        HsrAchievementProtocolProfile profile
    )
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        // 解码过程可以发现发生变动的字段，但如果内置配置本身格式有误，
        // 仍应在启动时以确定且一致的方式失败。
        _ = AchievementRecordPath.Parse(profile.RecordFieldPath);
        if (
            profile.PackedVarintFieldNumbers.Any(static fieldNumber => fieldNumber == 0)
            || profile.PackedVarintFieldNumbers.Distinct().Count() != profile.PackedVarintFieldNumbers.Count
        )
        {
            throw new ArgumentException("packed varint 字段号必须非零且不能重复", nameof(profile));
        }
    }

    public AchievementCandidateDiagnostic? BestCandidate { get; private set; }

    public bool TryDecode(CapturedPacket packet, out AchievementSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        snapshot = null;

        if (packet.Body.Length < 8 || !ProtoWire.TryParse(packet.Body, out var root) || root is null)
        {
            return false;
        }

        var collections = new List<RecordCollection>();
        CollectRecordCollections([root], [], depth: 0, collections);

        SnapshotCandidate? best = null;
        foreach (var collection in collections)
        {
            var candidate = EvaluateCollection(packet, collection);
            if (candidate is not null && (best is null || IsBetter(candidate, best)))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        RememberDiagnostic(ToDiagnostic(best));
        if (!best.IsAccepted)
        {
            return false;
        }

        snapshot = new AchievementSnapshot
        {
            Game = GameKind.HSR,
            CapturedAt = packet.CapturedAt,
            GameVersion = _gameVersion,
            SourceCommandId = packet.CommandId,
            RecordFieldPath = best.RecordFieldPath,
            IdFieldNumber = best.IdFieldNumber,
            StatusFieldNumber = best.StatusFieldNumber,
            FinishTimestampFieldNumber = best.FinishTimestampFieldNumber,
            ProgressFieldNumber = best.ProgressFieldNumber,
            PackedVarintFieldNumbers = best
                .Records.SelectMany(static record => record.RawPackedVarints.Keys)
                .Distinct()
                .Order()
                .ToArray(),
            CatalogMatchCount = best.CatalogMatchCount,
            UnknownIdCount = best.UnknownIdCount,
            Records = best.Records,
        };
        return true;
    }

    private SnapshotCandidate? EvaluateCollection(CapturedPacket packet, RecordCollection collection)
    {
        SnapshotCandidate? best = null;
        var possibleIdFields = collection.Rows.SelectMany(static row => row.Keys).Distinct().Order().ToArray();

        foreach (var idFieldNumber in possibleIdFields)
        {
            var candidate = EvaluateIdField(packet, collection, idFieldNumber);
            if (candidate is not null && (best is null || IsBetter(candidate, best)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private SnapshotCandidate? EvaluateIdField(CapturedPacket packet, RecordCollection collection, uint idFieldNumber)
    {
        var rowsWithId = collection.Rows.Where(row => row.TryGetValue(idFieldNumber, out _)).ToArray();
        if (rowsWithId.Length < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var plausibleRows = rowsWithId
            .Where(row => row[idFieldNumber] <= uint.MaxValue && LooksLikeAchievementId((uint)row[idFieldNumber]))
            .ToArray();
        if (plausibleRows.Length < MinimumVerifiedRecordCount)
        {
            return null;
        }

        // GetQuestDataScRsp 是包含多种任务的快照，而非仅包含成就的集合。
        // 因此，主线、支线、每日任务和成就的任务 ID 会共存于同一个字段中，
        // 所以检查所有任务 ID 中 4xxxxxx 值所占的比例并不能作为有效的安全校验。
        // 应改为使用内置目录，对筛选出的成就子集进行验证。
        var knownRows = plausibleRows.Count(row => _catalog.Ids.Contains((uint)row[idFieldNumber]));
        if (knownRows < MinimumVerifiedRecordCount || knownRows * 5 < plausibleRows.Length * 3)
        {
            return null;
        }

        var recordFieldPath = AchievementRecordPath.Format(collection.Path);
        var usesKnownRecordShape = idFieldNumber == _profile.IdFieldNumber;
        var isExactKnownProfile =
            packet.CommandId == _profile.FullSnapshotCommandId
            && string.Equals(recordFieldPath, _profile.RecordFieldPath, StringComparison.Ordinal)
            && usesKnownRecordShape;
        var finishTimestampFieldNumber = InferFinishTimestampField(
            plausibleRows,
            idFieldNumber,
            packet.CapturedAt,
            usesKnownRecordShape
        );
        var statusFieldNumber = InferStatusField(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            usesKnownRecordShape
        );
        var progressFieldNumber = InferProgressField(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            statusFieldNumber,
            usesKnownRecordShape
        );
        var records = BuildRecords(
            plausibleRows,
            idFieldNumber,
            statusFieldNumber,
            finishTimestampFieldNumber,
            progressFieldNumber,
            preserveProfilePackedVarints: isExactKnownProfile
        );
        if (records.Count < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var catalogMatches = records.Count(record => _catalog.Ids.Contains(record.Id));
        var unknownIds = records.Count - catalogMatches;
        var completionEvidence = records.Count(static record => record.FinishTimestamp is > 0);
        var minimumRecordCount = isExactKnownProfile ? MinimumVerifiedRecordCount : MinimumDiscoveredRecordCount;

        string decision;
        var isAccepted = false;
        if (records.Count < minimumRecordCount || catalogMatches < minimumRecordCount)
        {
            decision = isExactKnownProfile
                ? $"候选不足 {MinimumVerifiedRecordCount} 条"
                : $"自发现候选不足 {MinimumDiscoveredRecordCount} 条";
        }
        else if (catalogMatches * 5 < records.Count * 3)
        {
            decision = "元数据命中率低于 60%";
        }
        else if (finishTimestampFieldNumber is null || completionEvidence == 0)
        {
            decision = "未找到可信的完成时间字段，拒绝生成可能为空的导出";
        }
        else
        {
            decision = isExactKnownProfile ? "通过内置结构提示和元数据校验" : "通过元数据驱动的协议结构发现";
            isAccepted = true;
        }

        return new SnapshotCandidate(
            packet.CommandId,
            recordFieldPath,
            idFieldNumber,
            statusFieldNumber,
            finishTimestampFieldNumber,
            progressFieldNumber,
            records,
            catalogMatches,
            unknownIds,
            completionEvidence,
            isExactKnownProfile,
            isAccepted,
            decision
        );
    }

    private uint? InferFinishTimestampField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        DateTimeOffset capturedAt,
        bool useKnownHint
    )
    {
        uint? bestField = null;
        long bestScore = long.MinValue;
        foreach (var fieldNumber in rows.SelectMany(static row => row.Keys).Distinct())
        {
            if (fieldNumber == idFieldNumber || !TryScoreTimestampField(rows, fieldNumber, capturedAt, out var score))
            {
                continue;
            }

            if (useKnownHint && fieldNumber == _profile.FinishTimestampFieldNumber)
            {
                // 已知字段提示可用于打破原本无法区分的平局，但新观测到的
                // 仅在部分记录中出现的时间戳字段，仍应优先于在所有记录中都存在的提示字段。
                score += 100_000_000L;
            }

            if (score > bestScore || score == bestScore && fieldNumber < bestField)
            {
                bestField = fieldNumber;
                bestScore = score;
            }
        }

        return bestField;
    }

    private static bool TryScoreTimestampField(
        IReadOnlyList<RecordRow> rows,
        uint fieldNumber,
        DateTimeOffset capturedAt,
        out long score
    )
    {
        score = 0;
        var observed = 0;
        var positive = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldNumber, out var rawValue))
            {
                continue;
            }

            observed++;
            if (rawValue == 0)
            {
                continue;
            }

            positive++;
            if (rawValue > long.MaxValue || !AchievementTimestampEvidence.IsPlausible((long)rawValue, capturedAt))
            {
                score = 0;
                return false;
            }
        }

        if (positive == 0)
        {
            return false;
        }

        // 完成时间戳通常只存在于已完成的记录中。相比在所有记录中都存在的
        // 时间戳（例如接受时间），应优先选择具有这种分布特征的字段。
        var partialCompletionBonus = positive < rows.Count ? 1_000_000_000L : 0L;
        score = partialCompletionBonus + positive * 1_000L + observed;
        return true;
    }

    private uint? InferStatusField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        bool useKnownHint
    )
    {
        if (finishTimestampFieldNumber is null)
        {
            return null;
        }

        uint? bestField = null;
        long bestScore = long.MinValue;
        foreach (var fieldNumber in rows.SelectMany(static row => row.Keys).Distinct())
        {
            if (fieldNumber == idFieldNumber || fieldNumber == finishTimestampFieldNumber)
            {
                continue;
            }

            var observed = 0;
            var values = new uint[rows.Count];
            var valid = true;
            for (var index = 0; index < rows.Count; index++)
            {
                if (!rows[index].TryGetValue(fieldNumber, out var rawValue))
                {
                    values[index] = 0;
                    continue;
                }

                observed++;
                var maximumValue =
                    useKnownHint && fieldNumber == _profile.StatusFieldNumber ? uint.MaxValue : byte.MaxValue;
                if (rawValue > maximumValue)
                {
                    valid = false;
                    break;
                }

                values[index] = (uint)rawValue;
            }

            if (!valid || observed == 0)
            {
                continue;
            }

            var distinct = values.Distinct().Count();
            if (distinct is < 2 or > 16)
            {
                continue;
            }

            var correctlySeparated = values
                .Select(
                    (value, index) =>
                        new
                        {
                            Value = value,
                            Completed = rows[index].TryGetValue(finishTimestampFieldNumber.Value, out var rawTimestamp)
                                && rawTimestamp > 0,
                        }
                )
                .GroupBy(static item => item.Value)
                .Sum(static group =>
                    Math.Max(group.Count(static item => item.Completed), group.Count(static item => !item.Completed))
                );
            if (correctlySeparated * 10 < rows.Count * 8)
            {
                continue;
            }

            var score = correctlySeparated * 10_000L + observed * 10L - distinct;
            if (useKnownHint && fieldNumber == _profile.StatusFieldNumber)
            {
                // 这仅用于提示字段位置。数值会按观测到的原始值保留，
                // 并由各输出格式自行解释。
                score += 100_000_000L;
            }

            if (score > bestScore || score == bestScore && fieldNumber < bestField)
            {
                bestField = fieldNumber;
                bestScore = score;
            }
        }

        return bestField;
    }

    private uint? InferProgressField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        uint? statusFieldNumber,
        bool useKnownHint
    )
    {
        if (
            useKnownHint
            && _profile.ProgressFieldNumber != idFieldNumber
            && _profile.ProgressFieldNumber != finishTimestampFieldNumber
            && _profile.ProgressFieldNumber != statusFieldNumber
            && rows.Any(row => row.ContainsKey(_profile.ProgressFieldNumber))
        )
        {
            return _profile.ProgressFieldNumber;
        }

        // 仅凭数值特征无法可靠地推断进度。完整的原始 varint 映射会保留在备份中，以便后续确认协议结构。
        return null;
    }

    private static IReadOnlyList<AchievementRecord> BuildRecords(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? statusFieldNumber,
        uint? finishTimestampFieldNumber,
        uint? progressFieldNumber,
        bool preserveProfilePackedVarints
    )
    {
        var byId = new Dictionary<uint, AchievementRecord>();

        foreach (var row in rows)
        {
            if (
                !row.TryGetValue(idFieldNumber, out var rawId)
                || rawId > uint.MaxValue
                || !LooksLikeAchievementId((uint)rawId)
            )
            {
                continue;
            }

            var finishTimestamp = VarintFieldReader.ReadInt64(row, finishTimestampFieldNumber);
            var record = new AchievementRecord
            {
                Id = (uint)rawId,
                IsCompleted = finishTimestamp is > 0,
                Status = VarintFieldReader.ReadUInt32(row, statusFieldNumber, defaultWhenMissing: true),
                Progress = VarintFieldReader.ReadUInt64(row, progressFieldNumber, defaultWhenMissing: true),
                FinishTimestamp = finishTimestamp,
                RawVarints = new Dictionary<uint, ulong>(row),
                RawPackedVarints = preserveProfilePackedVarints
                    ? row.PackedVarints.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray())
                    : new Dictionary<uint, ulong[]>(),
            };

            if (!byId.TryGetValue(record.Id, out var previous) || Prefer(record, previous))
            {
                byId[record.Id] = record;
            }
        }

        return byId.Values.OrderBy(static record => record.Id).ToArray();
    }

    private void CollectRecordCollections(
        IReadOnlyList<ProtoMessage> containers,
        IReadOnlyList<uint> pathPrefix,
        int depth,
        ICollection<RecordCollection> output
    )
    {
        if (depth >= MaximumTraversalDepth)
        {
            return;
        }

        var childrenByField = new Dictionary<uint, List<ProtoMessage>>();
        foreach (var container in containers)
        {
            foreach (var field in container.Fields)
            {
                if (
                    field.WireType != ProtoWireType.LengthDelimited
                    || !ProtoWire.TryParse(field.Bytes, out var child)
                    || child is null
                )
                {
                    continue;
                }

                if (!childrenByField.TryGetValue(field.Number, out var children))
                {
                    children = [];
                    childrenByField.Add(field.Number, children);
                }

                children.Add(child);
            }
        }

        foreach (var pair in childrenByField.OrderBy(static pair => pair.Key))
        {
            var path = AppendPath(pathPrefix, pair.Key);
            var rows = new List<RecordRow>();
            foreach (var child in pair.Value)
            {
                if (TryCreateRecordRow(child, out var row))
                {
                    rows.Add(row);
                }
            }

            if (rows.Count >= MinimumVerifiedRecordCount)
            {
                output.Add(new RecordCollection(path, rows));
            }

            CollectRecordCollections(pair.Value, path, depth + 1, output);
        }
    }

    private static uint[] AppendPath(IReadOnlyList<uint> prefix, uint fieldNumber)
    {
        var path = new uint[prefix.Count + 1];
        for (var index = 0; index < prefix.Count; index++)
        {
            path[index] = prefix[index];
        }

        path[^1] = fieldNumber;
        return path;
    }

    private bool TryCreateRecordRow(ProtoMessage message, out RecordRow row)
    {
        row = new RecordRow();
        if (message.Fields.Count is < 1 or > MaximumFieldsPerRecord)
        {
            return false;
        }

        foreach (var field in message.Fields)
        {
            if (field.WireType == ProtoWireType.Varint)
            {
                // RawVarints 有意为每个字段只保留一个值。重复的标量字段
                // 不会导致该记录中的其他有效字段失效。
                row.TryAdd(field.Number, field.Varint);
            }
            else if (
                field.WireType == ProtoWireType.LengthDelimited
                && _profile.PackedVarintFieldNumbers.Contains(field.Number)
                && ProtoWire.TryParsePackedVarints(field.Bytes, out var values)
            )
            {
                // Wire type 2 也用于字符串、字节数据和子消息。
                // 只有经当前协议配置确认的字段才会按 packed varint 进行解码。

                if (row.PackedVarints.TryGetValue(field.Number, out var previous))
                {
                    row.PackedVarints[field.Number] = [.. previous, .. values];
                }
                else
                {
                    row.PackedVarints.Add(field.Number, values);
                }
            }
        }

        return row.Count != 0;
    }

    private void RememberDiagnostic(AchievementCandidateDiagnostic diagnostic)
    {
        if (
            BestCandidate is null
            || diagnostic.IsAccepted && !BestCandidate.IsAccepted
            || diagnostic.IsAccepted == BestCandidate.IsAccepted
                && (
                    diagnostic.CatalogMatchCount > BestCandidate.CatalogMatchCount
                    || diagnostic.CatalogMatchCount == BestCandidate.CatalogMatchCount
                        && diagnostic.RecordCount > BestCandidate.RecordCount
                    || diagnostic.CatalogMatchCount == BestCandidate.CatalogMatchCount
                        && diagnostic.RecordCount == BestCandidate.RecordCount
                        && diagnostic.CompletionEvidenceCount > BestCandidate.CompletionEvidenceCount
                )
        )
        {
            BestCandidate = diagnostic;
        }
    }

    private static AchievementCandidateDiagnostic ToDiagnostic(SnapshotCandidate candidate)
    {
        return new AchievementCandidateDiagnostic
        {
            CommandId = candidate.CommandId,
            RecordFieldPath = candidate.RecordFieldPath,
            IdFieldNumber = candidate.IdFieldNumber,
            StatusFieldNumber = candidate.StatusFieldNumber,
            FinishTimestampFieldNumber = candidate.FinishTimestampFieldNumber,
            ProgressFieldNumber = candidate.ProgressFieldNumber,
            RecordCount = candidate.Records.Count,
            CatalogMatchCount = candidate.CatalogMatchCount,
            UnknownIdCount = candidate.UnknownIdCount,
            CompletionEvidenceCount = candidate.CompletionEvidenceCount,
            IsAccepted = candidate.IsAccepted,
            Decision = candidate.Decision,
        };
    }

    private static bool IsBetter(SnapshotCandidate candidate, SnapshotCandidate previous)
    {
        if (candidate.IsAccepted != previous.IsAccepted)
        {
            return candidate.IsAccepted;
        }

        if (candidate.CatalogMatchCount != previous.CatalogMatchCount)
        {
            return candidate.CatalogMatchCount > previous.CatalogMatchCount;
        }

        if (candidate.Records.Count != previous.Records.Count)
        {
            return candidate.Records.Count > previous.Records.Count;
        }

        if (candidate.CompletionEvidenceCount != previous.CompletionEvidenceCount)
        {
            return candidate.CompletionEvidenceCount > previous.CompletionEvidenceCount;
        }

        return candidate.IsExactKnownProfile && !previous.IsExactKnownProfile;
    }

    private static bool Prefer(AchievementRecord candidate, AchievementRecord previous)
    {
        if (candidate.IsCompleted != previous.IsCompleted)
        {
            return candidate.IsCompleted;
        }

        if (candidate.FinishTimestamp.HasValue != previous.FinishTimestamp.HasValue)
        {
            return candidate.FinishTimestamp.HasValue;
        }

        if (candidate.Status.HasValue != previous.Status.HasValue)
        {
            return candidate.Status.HasValue;
        }

        if (candidate.RawVarints.Count != previous.RawVarints.Count)
        {
            return candidate.RawVarints.Count > previous.RawVarints.Count;
        }

        return candidate.RawPackedVarints.Sum(static pair => pair.Value.Length)
            > previous.RawPackedVarints.Sum(static pair => pair.Value.Length);
    }

    private static bool LooksLikeAchievementId(uint value)
    {
        return value is >= 4_000_000 and <= 4_999_999;
    }

    private sealed class RecordRow : Dictionary<uint, ulong>
    {
        public Dictionary<uint, ulong[]> PackedVarints { get; } = [];
    }

    private sealed record RecordCollection(IReadOnlyList<uint> Path, IReadOnlyList<RecordRow> Rows);

    private sealed record SnapshotCandidate(
        uint CommandId,
        string RecordFieldPath,
        uint IdFieldNumber,
        uint? StatusFieldNumber,
        uint? FinishTimestampFieldNumber,
        uint? ProgressFieldNumber,
        IReadOnlyList<AchievementRecord> Records,
        int CatalogMatchCount,
        int UnknownIdCount,
        int CompletionEvidenceCount,
        bool IsExactKnownProfile,
        bool IsAccepted,
        string Decision
    );
}
