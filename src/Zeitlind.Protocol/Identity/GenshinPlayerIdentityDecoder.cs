using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Identity;

public static class GenshinPlayerIdentityDecoder
{
    public const uint PacketHeadUidFieldNumber = 11;
    private const ulong MinimumNineDigitUid = 100_000_000;
    private const ulong MaximumPlausibleUid = 999_999_999;
    private const int MaximumNestedDepth = 1;

    public static bool TryDecode(CapturedPacket packet, out PlayerIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (TryDecodeFromHeader(packet, out evidence))
        {
            return true;
        }

        return TryDecodeFromBody(packet, out evidence);
    }

    private static bool TryDecodeFromHeader(CapturedPacket packet, out PlayerIdentityEvidence evidence)
    {
        evidence = default;
        if (packet.Header.Length == 0 || !ProtoWire.TryParse(packet.Header, out var header) || header is null)
        {
            return false;
        }

        if (TryReadUidField(header, PacketHeadUidFieldNumber, out var uid))
        {
            evidence = Confirmed(uid, $"包头字段 {PacketHeadUidFieldNumber}");
            return true;
        }

        // 正式服 PacketHead.user_id 经常是 0 或不出现。字段 11 无效时，
        // 只接受包头里唯一一个九位 varint。
        if (!TryFindUniqueUid(header, out uid, out var fallbackField, out _))
        {
            return false;
        }

        evidence = Confirmed(uid, $"包头字段 {fallbackField}");
        return true;
    }

    private static bool TryDecodeFromBody(CapturedPacket packet, out PlayerIdentityEvidence evidence)
    {
        evidence = default;
        if (packet.Body.Length == 0 || !ProtoWire.TryParse(packet.Body, out var message) || message is null)
        {
            return false;
        }

        if (!TryFindUniqueUid(message, out var uid, out var fieldNumber, out var depth))
        {
            return false;
        }

        // 19:12 实机命中：命令 1593，字段 14，嵌套 1。好友列表等大包会有多个
        // 九位数，唯一性不成立，不会误把他人 UID 当成当前玩家。
        evidence = Confirmed(uid, $"命令 {packet.CommandId}，字段 {fieldNumber}，嵌套 {depth}");
        return true;
    }

    private static PlayerIdentityEvidence Confirmed(ulong uid, string detail)
    {
        return new PlayerIdentityEvidence(uid, PlayerIdentityConfidence.Confirmed, detail);
    }

    private static bool TryReadUidField(ProtoMessage message, uint fieldNumber, out ulong uid)
    {
        uid = 0;
        var found = false;
        foreach (var field in message.Fields)
        {
            if (field.Number != fieldNumber || field.WireType != ProtoWireType.Varint)
            {
                continue;
            }

            if (!IsPlausibleUid(field.Varint))
            {
                continue;
            }

            if (found && uid != field.Varint)
            {
                uid = 0;
                return false;
            }

            uid = field.Varint;
            found = true;
        }

        return found;
    }

    private static bool TryFindUniqueUid(
        ProtoMessage message,
        out ulong uid,
        out uint fieldNumber,
        out int depth
    )
    {
        uid = 0;
        fieldNumber = 0;
        depth = 0;
        return TryCollectUniqueUid(message, currentDepth: 0, ref uid, ref fieldNumber, ref depth) && uid != 0;
    }

    private static bool TryCollectUniqueUid(
        ProtoMessage message,
        int currentDepth,
        ref ulong uid,
        ref uint fieldNumber,
        ref int depth
    )
    {
        foreach (var field in message.Fields)
        {
            if (field.WireType == ProtoWireType.Varint && IsPlausibleUid(field.Varint))
            {
                if (uid != 0 && uid != field.Varint)
                {
                    uid = 0;
                    fieldNumber = 0;
                    depth = 0;
                    return false;
                }

                uid = field.Varint;
                fieldNumber = field.Number;
                depth = currentDepth;
                continue;
            }

            if (
                field.WireType != ProtoWireType.LengthDelimited
                || currentDepth >= MaximumNestedDepth
                || !ProtoWire.TryParse(field.Bytes, out var nested)
                || nested is null
            )
            {
                continue;
            }

            if (!TryCollectUniqueUid(nested, currentDepth + 1, ref uid, ref fieldNumber, ref depth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlausibleUid(ulong value)
    {
        return value is >= MinimumNineDigitUid and <= MaximumPlausibleUid;
    }
}
