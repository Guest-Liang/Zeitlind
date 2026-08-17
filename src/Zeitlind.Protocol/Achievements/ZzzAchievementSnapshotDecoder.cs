using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Achievements;

public sealed class ZzzAchievementSnapshotDecoder
{
    private const int MinimumVerifiedRecordCount = 3;
    private const int MinimumDiscoveredRecordCount = 20;
    private const int MaximumTraversalDepth = 6;
    private const int MaximumFieldsPerRecord = 32;

    private readonly AchievementCatalog _catalog;
    private readonly string _gameVersion;
    private readonly ZzzAchievementProtocolProfile _profile;

    public ZzzAchievementSnapshotDecoder(
        AchievementCatalog catalog,
        string gameVersion,
        ZzzAchievementProtocolProfile profile
    )
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        // 解码过程可以发现发生变动的字段，但如果内置配置本身格式有误，
        // 仍应在启动时以确定且一致的方式失败。
        _ = AchievementRecordPath.Parse(profile.RecordFieldPath);
    }

    public AchievementCandidateDiagnostic? BestCandidate { get; private set; }

    public bool TryDecode(CapturedPacket packet, out AchievementSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        snapshot = null;

        if (packet.Body.Length < 16 || !ProtoWire.TryParse(packet.Body, out var root) || root is null)
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
            Game = GameKind.ZZZ,
            CapturedAt = packet.CapturedAt,
            GameVersion = _gameVersion,
            SourceCommandId = packet.CommandId,
            RecordFieldPath = best.RecordFieldPath,
            IdFieldNumber = best.IdFieldNumber,
            FinishTimestampFieldNumber = best.FinishTimestampFieldNumber,
            CompletedFlagFieldNumber = best.CompletedFlagFieldNumber,
            PackedVarintFieldNumbers = [],
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
        var rowsWithId = collection.Rows.Where(row => row.ContainsKey(idFieldNumber)).ToArray();
        if (rowsWithId.Length < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var plausibleRows = rowsWithId
            .Where(row => row[idFieldNumber] <= uint.MaxValue && LooksLikeAchievementId((uint)row[idFieldNumber]))
            .ToArray();
        if (plausibleRows.Length < MinimumVerifiedRecordCount || plausibleRows.Length * 10 < rowsWithId.Length * 9)
        {
            return null;
        }

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
        var completedFlagFieldNumber = InferCompletedFlagField(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            usesKnownRecordShape
        );
        var records = BuildRecords(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            completedFlagFieldNumber,
            packet.CapturedAt,
            rejectImplausibleTimestamps: isExactKnownProfile
                && finishTimestampFieldNumber == _profile.FinishTimestampFieldNumber
        );
        if (records is null || records.Count < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var catalogMatches = records.Count(record => _catalog.Ids.Contains(record.Id));
        var unknownIds = records.Count - catalogMatches;
        var completionEvidence = records.Count(static record =>
            record.FinishTimestamp is > 0 || record.CompletedFlag is true
        );
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
        else if (
            !isExactKnownProfile
            && (
                finishTimestampFieldNumber is null || records.Count(static record => record.FinishTimestamp is > 0) == 0
            )
        )
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
            finishTimestampFieldNumber,
            completedFlagFieldNumber,
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
        IReadOnlyList<Dictionary<uint, ulong>> rows,
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
        IReadOnlyList<Dictionary<uint, ulong>> rows,
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

        var partialCompletionBonus = positive < rows.Count ? 1_000_000_000L : 0L;
        score = partialCompletionBonus + positive * 1_000L + observed;
        return true;
    }

    private uint? InferCompletedFlagField(
        IReadOnlyList<Dictionary<uint, ulong>> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        bool useKnownHint
    )
    {
        uint? bestField = null;
        long bestScore = long.MinValue;
        foreach (var fieldNumber in rows.SelectMany(static row => row.Keys).Distinct())
        {
            if (fieldNumber == idFieldNumber || fieldNumber == finishTimestampFieldNumber)
            {
                continue;
            }

            var observed = 0;
            var ones = 0;
            var agreement = 0;
            var valid = true;
            for (var index = 0; index < rows.Count; index++)
            {
                var hasFlag = rows[index].TryGetValue(fieldNumber, out var rawValue);
                if (hasFlag)
                {
                    observed++;
                    if (rawValue > 1)
                    {
                        valid = false;
                        break;
                    }

                    if (rawValue == 1)
                    {
                        ones++;
                    }
                }

                var completed =
                    finishTimestampFieldNumber is not null
                    && rows[index].TryGetValue(finishTimestampFieldNumber.Value, out var rawTimestamp)
                    && rawTimestamp > 0;
                var flagged = hasFlag && rawValue == 1;
                if (completed == flagged)
                {
                    agreement++;
                }
            }

            if (!valid || ones == 0 || ones == rows.Count)
            {
                continue;
            }

            if (finishTimestampFieldNumber is not null && agreement * 10 < rows.Count * 8)
            {
                continue;
            }

            var score = agreement * 10_000L + ones * 100L + observed;
            if (useKnownHint && fieldNumber == _profile.CompletedFlagFieldNumber)
            {
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

    private static IReadOnlyList<AchievementRecord>? BuildRecords(
        IReadOnlyList<Dictionary<uint, ulong>> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        uint? completedFlagFieldNumber,
        DateTimeOffset capturedAt,
        bool rejectImplausibleTimestamps
    )
    {
        var byId = new Dictionary<uint, AchievementRecord>();

        foreach (var row in rows)
        {
            if (!row.TryGetValue(idFieldNumber, out var rawId) || rawId > uint.MaxValue)
            {
                continue;
            }

            var id = (uint)rawId;
            if (!LooksLikeAchievementId(id))
            {
                continue;
            }

            var finishTimestamp = VarintFieldReader.ReadInt64(row, finishTimestampFieldNumber);
            if (finishTimestamp is > 0 && !AchievementTimestampEvidence.IsPlausible(finishTimestamp.Value, capturedAt))
            {
                if (rejectImplausibleTimestamps)
                {
                    return null;
                }

                finishTimestamp = null;
            }

            var completedFlag = VarintFieldReader.ReadBoolean(row, completedFlagFieldNumber);
            var record = new AchievementRecord
            {
                Id = id,
                IsCompleted = finishTimestamp is > 0 || completedFlag is true,
                FinishTimestamp = finishTimestamp,
                CompletedFlag = completedFlag,
                RawVarints = new Dictionary<uint, ulong>(row),
                RawPackedVarints = new Dictionary<uint, ulong[]>(),
            };

            if (!byId.TryGetValue(id, out var previous) || Prefer(record, previous))
            {
                byId[id] = record;
            }
        }

        return byId.Values.OrderBy(static record => record.Id).ToArray();
    }

    private static void CollectRecordCollections(
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
            var rows = new List<Dictionary<uint, ulong>>();
            foreach (var child in pair.Value)
            {
                if (TryCreateVarintRow(child, out var row))
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

    private static bool TryCreateVarintRow(ProtoMessage message, out Dictionary<uint, ulong> row)
    {
        row = new Dictionary<uint, ulong>();
        if (message.Fields.Count is < 1 or > MaximumFieldsPerRecord)
        {
            return false;
        }

        foreach (var field in message.Fields)
        {
            if (field.WireType != ProtoWireType.Varint)
            {
                continue;
            }

            if (!row.TryAdd(field.Number, field.Varint))
            {
                row.Clear();
                return false;
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
            StatusFieldNumber = null,
            FinishTimestampFieldNumber = candidate.FinishTimestampFieldNumber,
            ProgressFieldNumber = null,
            CompletedFlagFieldNumber = candidate.CompletedFlagFieldNumber,
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

        if (candidate.IsExactKnownProfile != previous.IsExactKnownProfile)
        {
            return candidate.IsExactKnownProfile;
        }

        if (candidate.CatalogMatchCount != previous.CatalogMatchCount)
        {
            return candidate.CatalogMatchCount > previous.CatalogMatchCount;
        }

        if (candidate.Records.Count != previous.Records.Count)
        {
            return candidate.Records.Count > previous.Records.Count;
        }

        return candidate.CompletionEvidenceCount > previous.CompletionEvidenceCount;
    }

    private static bool Prefer(AchievementRecord candidate, AchievementRecord previous)
    {
        if (candidate.IsCompleted != previous.IsCompleted)
        {
            return candidate.IsCompleted;
        }

        return candidate.RawVarints.Count > previous.RawVarints.Count;
    }

    private static bool LooksLikeAchievementId(uint value)
    {
        return value is >= 1_000_000 and <= 9_999_999;
    }

    private sealed record RecordCollection(IReadOnlyList<uint> Path, IReadOnlyList<Dictionary<uint, ulong>> Rows);

    private sealed record SnapshotCandidate(
        uint CommandId,
        string RecordFieldPath,
        uint IdFieldNumber,
        uint? FinishTimestampFieldNumber,
        uint? CompletedFlagFieldNumber,
        IReadOnlyList<AchievementRecord> Records,
        int CatalogMatchCount,
        int UnknownIdCount,
        int CompletionEvidenceCount,
        bool IsExactKnownProfile,
        bool IsAccepted,
        string Decision
    );
}
