using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Achievements;

public sealed class ZzzAchievementSnapshotDecoder
{
    private readonly AchievementCatalog _catalog;
    private readonly string _gameVersion;
    private readonly ZzzAchievementProtocolProfile _profile;
    private readonly uint[] _recordPath;

    public ZzzAchievementSnapshotDecoder(
        AchievementCatalog catalog,
        string gameVersion,
        ZzzAchievementProtocolProfile profile
    )
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _recordPath = AchievementRecordPath.Parse(profile.RecordFieldPath);
    }

    public bool TryDecode(CapturedPacket packet, out AchievementSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        snapshot = null;

        if (
            packet.CommandId != _profile.FullSnapshotCommandId
            || packet.Body.Length < 16
            || !ProtoWire.TryParse(packet.Body, out var root)
            || root is null
        )
        {
            return false;
        }

        var rows = ReadRows(root, _recordPath);
        if (!HasVerifiedIdShape(rows))
        {
            return false;
        }

        var records = BuildRecords(rows, packet.CapturedAt);
        if (records is null || records.Count < 3)
        {
            return false;
        }

        var catalogMatches = records.Count(record => _catalog.Ids.Contains(record.Id));
        snapshot = new AchievementSnapshot
        {
            Game = GameKind.ZZZ,
            CapturedAt = packet.CapturedAt,
            GameVersion = _gameVersion,
            SourceCommandId = packet.CommandId,
            RecordFieldPath = _profile.RecordFieldPath,
            IdFieldNumber = _profile.IdFieldNumber,
            FinishTimestampFieldNumber = _profile.FinishTimestampFieldNumber,
            CompletedFlagFieldNumber = _profile.CompletedFlagFieldNumber,
            PackedVarintFieldNumbers = [],
            CatalogMatchCount = catalogMatches,
            UnknownIdCount = records.Count - catalogMatches,
            Records = records,
        };
        return true;
    }

    private bool HasVerifiedIdShape(IReadOnlyList<Dictionary<uint, ulong>> rows)
    {
        var values = rows.Where(row => row.ContainsKey(_profile.IdFieldNumber))
            .Select(row => row[_profile.IdFieldNumber])
            .ToArray();
        if (values.Length < 3)
        {
            return false;
        }

        var known = values.Count(value => value <= uint.MaxValue && _catalog.Ids.Contains((uint)value));
        var plausible = values.Count(value => value <= uint.MaxValue && LooksLikeAchievementId((uint)value));

        return known >= 3 && known * 5 >= values.Length * 3 && plausible * 10 >= values.Length * 9;
    }

    private IReadOnlyList<AchievementRecord>? BuildRecords(
        IReadOnlyList<Dictionary<uint, ulong>> rows,
        DateTimeOffset capturedAt
    )
    {
        var byId = new Dictionary<uint, AchievementRecord>();

        foreach (var row in rows)
        {
            if (!row.TryGetValue(_profile.IdFieldNumber, out var rawId) || rawId > uint.MaxValue)
            {
                continue;
            }

            var id = (uint)rawId;
            if (!LooksLikeAchievementId(id))
            {
                continue;
            }

            var finishTimestamp = VarintFieldReader.ReadInt64(row, _profile.FinishTimestampFieldNumber);
            if (finishTimestamp is > 0 && !AchievementTimestampEvidence.IsPlausible(finishTimestamp.Value, capturedAt))
            {
                return null;
            }

            var completedFlag = VarintFieldReader.ReadBoolean(row, _profile.CompletedFlagFieldNumber);
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

    private static IReadOnlyList<Dictionary<uint, ulong>> ReadRows(ProtoMessage root, IReadOnlyList<uint> path)
    {
        var containers = new List<ProtoMessage> { root };

        for (var index = 0; index < path.Count - 1; index++)
        {
            var next = new List<ProtoMessage>();
            foreach (var container in containers)
            {
                foreach (var field in container.Fields)
                {
                    if (
                        field.Number != path[index]
                        || field.WireType != ProtoWireType.LengthDelimited
                        || !ProtoWire.TryParse(field.Bytes, out var child)
                        || child is null
                    )
                    {
                        continue;
                    }

                    next.Add(child);
                }
            }

            if (next.Count == 0)
            {
                return [];
            }

            containers = next;
        }

        var rows = new List<Dictionary<uint, ulong>>();
        var recordFieldNumber = path[^1];
        foreach (var container in containers)
        {
            foreach (var field in container.Fields)
            {
                if (
                    field.Number != recordFieldNumber
                    || field.WireType != ProtoWireType.LengthDelimited
                    || !ProtoWire.TryParse(field.Bytes, out var record)
                    || record is null
                    || !TryCreateVarintRow(record, out var row)
                )
                {
                    continue;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static bool TryCreateVarintRow(ProtoMessage message, out Dictionary<uint, ulong> row)
    {
        row = new Dictionary<uint, ulong>();
        if (message.Fields.Count is < 1 or > 32)
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
}
